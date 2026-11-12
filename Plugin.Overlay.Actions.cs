using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// Overlay partial: what the window's controls DO — save, wear, rename, update / delete behind an inline confirm, drag
/// reorder — and the R13 feedback where the user clicked: the row flashes (green "Saved" / red "Not now", ~1 s) and the
/// footer carries a one-line result (~3 s). Flash and footer are keyed by the OUTFIT object and expired by the 10 Hz
/// tick, so a re-render, scroll or reorder keeps the flash on the right row and no poll reads the clock.
/// </summary>
public sealed partial class Plugin
{
    private const float FlashSec = 1.0f;
    private const float FooterSec = 3.0f;

    private OutfitSlot? _flashSlot;
    private bool _flashOk;
    private string _flashText = "";
    private float _flashUntil;

    private string _footerText = "";
    private bool _footerOk;
    private float _footerUntil;   // 0 = no feedback line (the footer shows its hint)

    private void Feedback(OutfitSlot? flashSlot, bool ok, string? flashText, string footer)
    {
        float now = _services.Framework.TimeNow;
        _flashSlot = flashSlot;
        _flashOk = ok;
        _flashText = flashText ?? "";
        _flashUntil = now + FlashSec;
        _footerText = footer;
        _footerOk = ok;
        _footerUntil = now + FooterSec;
        Repaint();
    }

    private void ExpireFeedback(float now)
    {
        if (_flashSlot != null && now >= _flashUntil) { _flashSlot = null; Repaint(); }
        if (_footerUntil > 0f && now >= _footerUntil) { _footerUntil = 0f; Repaint(); }
    }

    private bool Flashing(OutfitSlot? slot) => slot != null && ReferenceEquals(slot, _flashSlot);

    // ── save (header Primary pill) ────────────────────────────────────────────────────────────────────────────────
    private void OnSaveCurrent()
    {
        // Appends at the END, so an open rename / confirm (armed by index) is unaffected and stays open.
        if (!SaveCurrentOutfit() || ResolveKeyOrSkip("save") is not { } key) return;
        var rows = _store.Get(key);
        var saved = rows[rows.Count - 1];
        // The list can't be scrolled to a given row (uniform VirtualList: ResetScroll only goes to the TOP), so the new
        // row flashes where it lands; the footer always confirms it.
        Feedback(saved, true, _loc.T("wardrobe.flash.saved"),
            _loc.TFormat("wardrobe.footer.saved", saved.Name, CountText(rows.Count)));
        _nextWornCheck = 0f;
    }

    // ── wear ──────────────────────────────────────────────────────────────────────────────────────────────────────
    private bool CanWear(int idx) => RowAt(idx) is not null && !Busy && _switchingSlot is null;

    private void OnWearRow(int idx)
    {
        if (!CanWear(idx) || RowAt(idx) is not { } slot) return;
        if (!_services.Wardrobe.IsAvailable)
        {
            Toast(NoticeTipType.RedBar, _loc.T("wardrobe.toast.apiNotReady"));
            return;
        }
        ApplyOutfit(slot);
    }

    // Main thread (posted by ApplyAndReportAsync's finally): the switch resolved one way or the other.
    private void EndSwitch()
    {
        _switchingSlot = null;
        _nextWornCheck = 0f;   // re-check at once so the row flips to "Worn" on the next tick
        Repaint();
    }

    // Main thread (posted by Report): the game refused the switch (combat, rejected, timeout). The game toasts the reason.
    private void OnApplyFailed(OutfitSlot slot)
        => Feedback(slot, false, _loc.T("wardrobe.flash.notNow"), _loc.TFormat("wardrobe.footer.applyFailed", slot.Name));

    // ── rename ────────────────────────────────────────────────────────────────────────────────────────────────────
    private bool IsEditing(int idx) => _editingIdx >= 0 && _editingIdx == idx;

    // Pencil: turn the row's name + pieces into one input seeded with the name, and focus it (FocusRequest).
    private void EnterEdit(int idx)
    {
        if (Busy || RowAt(idx) is not { } slot) return;
        _editingIdx = idx;
        _editBuffer = slot.Name;
        _focusSeq++;
        Repaint();
    }

    // Persist the edited name (from the live _editBuffer) and return the row to a label. Called by ✓ AND by the input's
    // Enter submit — both go through here so they can't disagree. An empty name keeps the old one.
    private void CommitRename(int idx)
    {
        if (!IsEditing(idx)) return;
        var name = _editBuffer?.Trim() ?? "";
        _editingIdx = -1;
        var oldName = RowAt(idx)?.Name;   // Rename overwrites it in place
        if (RowAt(idx) is { } slot && name.Length > 0 && name != slot.Name
            && ResolveKeyOrSkip("rename") is { } key && _store.Rename(key, idx, name))
        {
            Persist();
            LogChange($"renamed slot {idx + 1} '{oldName}' -> '{name}' for {key}");
            if (ReferenceEquals(slot, _previewSlot)) RefreshPreviewInfo();
            Feedback(slot, true, _loc.T("wardrobe.flash.renamed"), _loc.TFormat("wardrobe.footer.renamed", slot.Name));
            return;
        }
        Repaint();
    }

    private void CancelEdits()
    {
        _editingIdx = -1;
        _confirmIdx = -1;
        _confirmKind = ConfirmNone;
        Repaint();
    }

    // ── confirms (update / delete) ────────────────────────────────────────────────────────────────────────────────
    private bool IsConfirming(int idx) => _confirmKind != ConfirmNone && _confirmIdx == idx;

    private void ArmConfirm(int idx, int kind)
    {
        if (Busy || RowAt(idx) is null) return;
        _confirmIdx = idx;
        _confirmKind = kind;
        Repaint();
    }

    // Confirm pill (Replace / Delete) — run the armed action, then clear the confirm.
    private void ConfirmYes()
    {
        int idx = _confirmIdx, kind = _confirmKind;
        _confirmIdx = -1;
        _confirmKind = ConfirmNone;
        if (kind == ConfirmUpdate) DoUpdate(idx);
        else if (kind == ConfirmDelete) DoDelete(idx);
        Repaint();
    }

    // Re-capture the outfit the player is WEARING now into this slot (keeps its name + position, refreshes its dyes and
    // weapon skin). Same guards as Save current outfit.
    private void DoUpdate(int idx)
    {
        if (RowAt(idx) is not { } slot) return;
        if (!_services.Wardrobe.IsAvailable)
        {
            Toast(NoticeTipType.RedBar, _loc.T("wardrobe.toast.apiNotReady"));
            return;
        }
        if (CaptureCurrent() is not { } captured)
        {
            Toast(NoticeTipType.RedBar, _loc.T("wardrobe.toast.captureEmpty"));
            return;
        }
        if (ResolveKeyOrSkip("update") is not { } key || !_store.Update(key, idx, captured)) return;
        Persist();
        LogChange($"replaced slot {idx + 1} '{slot.Name}' with the worn outfit for {key}");
        DiagUpdated(slot, key);   // slot IS the updated entry (Update overwrites in place)
        Toast(NoticeTipType.GreenBar, _loc.TFormat("wardrobe.toast.updated", slot.Name));
        if (ReferenceEquals(slot, _previewSlot))
        {
            _previewSlot = null;   // re-dress the model with the new pieces
            ShowPreview(slot);
        }
        _nextWornCheck = 0f;
        Feedback(slot, true, _loc.T("wardrobe.flash.updated"), _loc.TFormat("wardrobe.footer.updated", slot.Name));
    }

    private void DoDelete(int idx)
    {
        if (RowAt(idx) is not { } slot || ResolveKeyOrSkip("delete") is not { } key || !_store.Delete(key, idx)) return;
        Persist();
        LogChange($"deleted '{slot.Name}' from slot {idx + 1} ({_store.Get(key).Count} left) for {key}");
        if (ReferenceEquals(slot, _previewSlot)) HidePreview();
        if (ReferenceEquals(slot, _wornSlot)) _wornSlot = null;
        Feedback(null, true, null, _loc.TFormat("wardrobe.footer.deleted", slot.Name, CountText(Rows.Count)));
    }

    // ── drag reorder (VirtualListElement.OnReorder: remove-then-insert ITEM indices, never a no-op) ────────────────
    // The list has no "can reorder" gate, so a drop while a rename or confirm is open is IGNORED (the list re-pulls and
    // the rows stay put): both are armed against a logical index that the move would shift under them.
    private void OnReorder(int from, int to)
    {
        if (Busy) { Repaint(); return; }
        if (ResolveKeyOrSkip("move") is { } key && _store.MoveTo(key, from, to))
        {
            Persist();
            LogChange($"moved '{_store.Get(key)[to].Name}' slot {from + 1} -> {to + 1} for {key}");
        }
        Repaint();
    }
}
