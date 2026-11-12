using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// The wardrobe overlay: a "Save current outfit" button plus a scroll-windowed list of the current
/// character's saved outfits — each row shows a badge (hotkey H1..H8 for the first 8, then a plain row
/// number 9, 10, 11… for the rest), an editable
/// name, its piece count, and rename / update / reorder / apply / delete controls. Storage + apply logic
/// live in <see cref="Plugin"/> (<c>Plugin.cs</c> / <see cref="WardrobeStore"/>); this partial is
/// presentation only. The list is read LIVE from the store (see <c>Rows</c>) so it always reflects the
/// current character's outfits, and is empty until the char id resolves at boot.
///
/// <para>The list is a <see cref="VirtualListElement"/>: a small pool of row widgets is recycled over the
/// logical outfit list as the user scrolls, so the number of outfits it can show is UNBOUNDED. Each pooled
/// row therefore renders a DIFFERENT outfit from refresh to refresh — every row closure resolves its
/// logical index through <c>Idx()</c> (= <c>_windowFirst</c> + pool slot), never a captured constant.</para>
/// </summary>
public sealed partial class Plugin
{
    // Pool size = rows that fit the 460px viewport (≈14 at scale 1.0) plus margin for partial rows at both
    // edges. NOT a cap on the outfit count: the VirtualListElement recycles these over the whole list.
    // (1.0.0 used a fixed 48-row EAGER pool, which silently hid outfit 49 onward — Discord "can't scroll
    // past the 48th outfit". A pool constant must never bound what the user can reach again.)
    private const int PoolRows = 20;

    // First logical outfit index the pool currently renders — written by the VirtualListElement's OnWindow
    // BEFORE any row Func is pulled, so a row's Idx() is correct for the frame being drawn.
    private int _windowFirst;

    private IWindowControl _window = null!;
    private IDisposable? _launcherEntry;

    // The displayed list is read LIVE from the store each frame (keyed by the char id), NOT cached —
    // empty (never a name/"default" fallback) until CharacterKey resolves. A store read is a cheap dict
    // lookup; the overlay only renders when open.
    private IReadOnlyList<OutfitSlot> Rows => CharacterKey is { } key ? _store.Get(key) : Array.Empty<OutfitSlot>();

    // Inline rename edit state. The name is a LABEL by default; clicking Edit turns THAT row into an
    // input field (Save/Enter commits it and returns to a label). _editBuffer is kept live by the
    // input's OnChange so the ✓ chip can read the typed value without an Enter first: the framework's
    // InputElement submits on ENTER ONLY — losing focus does NOT submit (UGuiTextInput registers no
    // onEndEdit listener; see Stellar.Infrastructure/Game/UGuiTextInput.cs).
    // With the recycled virtual list this matters: scrolling the row being renamed out of view destroys
    // its keyboard focus, but the typed text lives in _editBuffer (not in the field), so scrolling back
    // re-shows it intact — the user finishes by clicking the field again or pressing the ✓ chip.
    private int _editingIdx = -1;
    private string _editBuffer = "";

    // Inline confirm state for the destructive actions (update overwrites the slot; delete removes it). The
    // framework overlay has no modal popup, so the row's action area swaps to a "Overwrite? / Delete?  ✓ ✗"
    // bar. Mutually exclusive with the rename edit. 0 = none, 1 = update, 2 = delete.
    private const int ConfirmNone = 0, ConfirmUpdate = 1, ConfirmDelete = 2;
    private int _confirmIdx = -1;
    private int _confirmKind = ConfirmNone;

    // Hover-to-preview state. Hovering a row arms a debounced 3D preview (re-dressing the model loads each
    // cosmetic's assets, so a short settle avoids thrash when sweeping the mouse). _previewIdx is the outfit
    // currently shown (also highlights that row).
    // Pane sized/styled to read like the Entity Inspector's portrait (transparent backdrop, comparable
    // proportions); the body-framing algorithm is shared (PortraitModelHost). Height aligns with the list.
    private const int PreviewW = 260;
    private const int PreviewH = 440;
    private const long HoverDebounceTicks = 8;   // ~130 ms before the hovered outfit loads
    private long _tick;
    private int _hoverPendingIdx = -1;
    private long _hoverDeadline;
    private int _previewIdx = -1;

    private void InitOverlay()
    {
        _window = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "wardrobeloadout.window",
                Title:       _loc.T("wardrobe.window.title"),
                // 540 list column (454 row budget + scroll inset) + 8 gap + (260+8) preview pane + GlassMenu
                // body padding (24) + margin.
                DefaultRect: new WindowRect(20f, 120f, 860f, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            {
                StartVisible = false, Closable = true, Draggable = true,
                Anchor = WindowAnchor.TopRight,
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            },
            BuildRoot(),
            OnClose: () => { _window.SetVisiblePersist(false); HidePreview(); }));

        // Title stays the fixed literal "Wardrobe" — the stable pin-identity key (ILauncher.cs:49-50) —
        // so a pinned tile survives a language change; TitleProvider carries the live-localized display.
        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            "Wardrobe", IconPng: LauncherIcon, IconKey: null,
            OnOpen: ToggleOverlay)
        {
            ShouldShow = () => _services.ClientState.Phase == GamePhase.World,
            TitleProvider = () => _loc.T("wardrobe.window.title"),
        });

        _services.WardrobePreview.SetViewport(PreviewW, PreviewH);
        _services.Framework.Update += OnPreviewTick;
    }

    private void DisposeOverlay()
    {
        _services.Framework.Update -= OnPreviewTick;
        HidePreview();
        try { _launcherEntry?.Dispose(); } catch { /* disposal must not throw */ }
        try { _window?.Remove(); } catch { /* disposal must not throw */ }
    }

    // Toggle called by the window-toggle hotkey (declared in Plugin.cs) and the launcher tile.
    private void ToggleOverlay()
    {
        if (_window is null) return;
        var show = !_window.IsShown;
        _window.SetVisiblePersist(show);
        if (!show) HidePreview();
    }

    // Row hover-enter arms a debounced preview; hover-leave keeps the last preview (no flicker moving
    // between rows). The actual load fires from OnPreviewTick once the hover settles.
    private void OnRowHover(int idx, bool entered)
    {
        if (!entered) return;
        _hoverPendingIdx = idx;
        _hoverDeadline = _tick + HoverDebounceTicks;
    }

    // Debounce tick (framework Update); also the per-frame hook for TryMigrateNameToCharId (Plugin.cs).
    private void OnPreviewTick(float dt)
    {
        TryMigrateNameToCharId();
        _tick++;
        if (_hoverPendingIdx < 0 || _tick < _hoverDeadline) return;
        var idx = _hoverPendingIdx;
        _hoverPendingIdx = -1;
        ShowPreview(idx);
    }

    // Force an immediate repaint after a mutation (the list itself is read live via Rows).
    private void Repaint() => _window?.MarkDirty();

    private OutfitSlot? RowAt(int i) { var r = Rows; return i < r.Count ? r[i] : null; }

    private void OnSaveCurrent()
    {
        if (SaveCurrentOutfit()) Repaint();
    }

    // Turn the row's name into an editable field, seeded with the current name.
    private void EnterEdit(int idx)
    {
        if (RowAt(idx) is not { } slot) return;
        ClearConfirm();                 // rename and confirm are mutually exclusive
        _editingIdx = idx;
        _editBuffer = slot.Name;
        _window?.MarkDirty();
    }

    private bool IsConfirming(int idx) => _confirmIdx == idx && _confirmKind != ConfirmNone;

    // Arm the inline confirm for a destructive action on this row (cancels any rename in progress).
    private void ArmConfirm(int idx, int kind)
    {
        if (RowAt(idx) is null) return;
        _editingIdx = -1;
        _confirmIdx = idx;
        _confirmKind = kind;
        _window?.MarkDirty();
    }

    private void ClearConfirm()
    {
        _confirmIdx = -1;
        _confirmKind = ConfirmNone;
        _window?.MarkDirty();
    }

    // Confirm ✓ — run the armed action, then clear the confirm bar.
    private void ConfirmYes(int idx)
    {
        var kind = _confirmKind;
        ClearConfirm();
        if (kind == ConfirmUpdate) DoUpdate(idx);
        else if (kind == ConfirmDelete) DoDelete(idx);
    }

    // Persist the edited name (from the live _editBuffer) and return the row to a label. Called by the
    // Save button AND by the input's Enter/blur Submit — both go through here so they can't disagree.
    private void CommitRename(int idx)
    {
        var name = _editBuffer?.Trim() ?? "";
        if (name.Length > 0 && RowAt(idx) is not null && ResolveKeyOrSkip("rename") is { } key
            && _store.Rename(key, idx, name))
        {
            Persist();
        }
        _editingIdx = -1;
        Repaint();
    }

    private bool IsEditing(int idx) => _editingIdx == idx;

    private void OnApplyRow(int idx)
    {
        if (RowAt(idx) is not { } slot) return;
        if (!_services.Wardrobe.IsAvailable)
        {
            Toast(NoticeTipType.RedBar, _loc.T("wardrobe.toast.apiNotReady"));
            return;
        }
        ApplyOutfit(slot);
    }

    // Re-capture the outfit the player is WEARING now into this slot (keeps its name + position, and
    // refreshes its dyes and weapon skin). Same guards as Save current outfit. Runs on confirm ✓.
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
        if (ResolveKeyOrSkip("update") is not { } key) return;
        if (_store.Update(key, idx, captured))
        {
            Persist();
            DiagUpdated(slot, key);   // slot IS the updated entry (Update overwrites in place)
            Toast(NoticeTipType.GreenBar, _loc.TFormat("wardrobe.toast.updated", slot.Name));
            Repaint();
        }
    }

    private void OnMoveUp(int idx)   { if (ResolveKeyOrSkip("move") is { } key && _store.Move(key, idx, -1)) { Persist(); Repaint(); } }
    private void OnMoveDown(int idx) { if (ResolveKeyOrSkip("move") is { } key && _store.Move(key, idx, +1)) { Persist(); Repaint(); } }

    // Runs on confirm ✓ (which has already cleared the confirm bar).
    private void DoDelete(int idx)
    {
        if (ResolveKeyOrSkip("delete") is { } key && _store.Delete(key, idx)) { Persist(); Repaint(); }
    }
}
