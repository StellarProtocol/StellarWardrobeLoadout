using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Stellar.Abstractions.Domain;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// Overlay partial: every live STRING the window shows, cached so a poll never builds one (Plugin-Performance-Rules §2):
/// the header status line, the hotkey badges (the actual bound key), the per-row "N pcs" / row numbers, the OUTFITS
/// hint slot and the footer. Each is rebuilt only when its inputs (or the language) change.
/// </summary>
public sealed partial class Plugin
{
    // ── header status (ONE builder, Stellar-Design-Language principle 4) ──────────────────────────────────────────
    //   Switching to “X”…                 (accent, bold)       while an apply is in flight
    //   Wearing “X”  · N outfits          (green bold + muted) when a saved outfit is what's worn
    //   N outfits / No outfits yet        (muted)              otherwise
    private const int StatusPlain = 0, StatusWearing = 1, StatusSwitching = 2;
    private int _statusKind;
    private int _statusSig = int.MinValue;
    private string _statusMain = "", _statusTail = "";

    private void RefreshStatus()
    {
        var slot = _switchingSlot ?? _wornSlot;
        int kind = _switchingSlot != null ? StatusSwitching : _wornSlot != null ? StatusWearing : StatusPlain;
        int count = Rows.Count;
        var lang = _loc.T("wardrobe.status.count");   // catalog lookup (no allocation); changes reference with the language
        int sig = HashCode.Combine(kind, slot is null ? 0 : RuntimeHelpers.GetHashCode(slot), slot?.Name, count,
            RuntimeHelpers.GetHashCode(lang));
        if (sig == _statusSig) return;
        _statusSig = sig;
        _statusKind = kind;
        switch (kind)
        {
            case StatusSwitching:
                _statusMain = _loc.TFormat("wardrobe.status.switching", ShortName(slot!.Name));
                _statusTail = "";
                break;
            case StatusWearing:
                _statusMain = _loc.TFormat("wardrobe.status.wearing", ShortName(slot!.Name));
                _statusTail = " · " + CountText(count);
                break;
            default:
                _statusMain = "";
                _statusTail = CountText(count);
                break;
        }
    }

    private string StatusMain() { RefreshStatus(); return _statusMain; }
    private string StatusTail() { RefreshStatus(); return _statusTail; }
    private ColorRgba StatusMainColor() => _statusKind == StatusSwitching ? AccentColor() : SuccessColor();

    // "N outfits" / "1 outfit" / "No outfits yet" — shared by the header and the save / delete feedback lines.
    private string CountText(int count) => count == 0 ? _loc.T("wardrobe.status.none")
        : count == 1 ? _loc.T("wardrobe.status.countOne")
        : _loc.TFormat("wardrobe.status.count", count);

    // ── hotkey badges (approved option A: the REAL bound key) ─────────────────────────────────────────────────────
    // Rows 1–8 show the slot hotkey's current binding ("F1", "Ctrl+2") or "H<n>" while unbound, as an accent chip; rows
    // 9+ show a plain muted number. There is no binding-changed event, so the labels are re-read when the window opens
    // and once a second while it is shown, and rebuilt only for a slot whose binding changed (or on a language change).
    private readonly string[] _badgeLabel = new string[HotkeySlotCount];
    private readonly KeyBinding?[] _badgeBinding = new KeyBinding?[HotkeySlotCount];
    private string? _badgeLang;
    private float _nextBadgeRefresh;
    private const float BadgeRefreshSec = 1f;

    private void RefreshBadges()
    {
        _nextBadgeRefresh = _services.Framework.TimeNow + BadgeRefreshSec;
        var fmt = _loc.T("wardrobe.window.hotkeyBadge");
        bool langChanged = !ReferenceEquals(fmt, _badgeLang);
        _badgeLang = fmt;
        bool changed = false;
        for (int i = 0; i < HotkeySlotCount; i++)
        {
            KeyBinding? b;
            try { b = _actions[i].CurrentBinding; } catch { b = null; }
            if (!langChanged && _badgeLabel[i] != null && Nullable.Equals(b, _badgeBinding[i])) continue;
            _badgeBinding[i] = b;
            _badgeLabel[i] = b is { } kb
                ? WardrobeRules.ShortKeyLabel(kb.Key.ToString(), (kb.Modifiers & ModifierKeys.Ctrl) != 0,
                    (kb.Modifiers & ModifierKeys.Shift) != 0, (kb.Modifiers & ModifierKeys.Alt) != 0)
                : _loc.TFormat("wardrobe.window.hotkeyBadge", i + 1);
            changed = true;
        }
        if (changed) Repaint();
    }

    private string BadgeText(int idx)
        => RowAt(idx) is null ? "" : idx < HotkeySlotCount ? _badgeLabel[idx] ?? "" : NumberLabel(idx + 1);

    // A longer key name shrinks to stay inside the fixed 30 px badge (R10: a label change never moves the row); past
    // that it ellipsizes.
    private int BadgeFontSize(int idx)
    {
        int len = BadgeText(idx).Length;
        return len <= 3 ? 11 : len == 4 ? 10 : len == 5 ? 9 : 8;
    }

    // "9", "10", … cached once each (a row Func must not format a number per poll).
    private static readonly List<string> NumberLabels = new();

    private static string NumberLabel(int n)
    {
        while (NumberLabels.Count <= n) NumberLabels.Add(NumberLabels.Count.ToString());
        return NumberLabels[n];
    }

    // "N pcs" per piece count, rebuilt on a language change.
    private readonly string?[] _piecesLabel = new string?[48];
    private string? _piecesLang;

    private string PiecesText(OutfitSlot slot)
    {
        var fmt = _loc.T("wardrobe.window.pieces");
        if (!ReferenceEquals(fmt, _piecesLang)) { _piecesLang = fmt; Array.Clear(_piecesLabel, 0, _piecesLabel.Length); }
        int n = Worn(slot.Regions);
        if (n >= _piecesLabel.Length) return _loc.TFormat("wardrobe.window.pieces", n);
        return _piecesLabel[n] ??= _loc.TFormat("wardrobe.window.pieces", n);
    }

    // ── OUTFITS caption hint slot (R12: no tooltips — the caption names the hovered action) ───────────────────────
    private const int HintNone = 0, HintWear = 1, HintRename = 2, HintUpdate = 3, HintDelete = 4,
        HintSaveName = 5, HintCancel = 6;
    private int _hint;

    // `kind` = what the hovered control does now; on leave, the hint clears if it shows any of the control's kinds
    // (`a` / `b`: a control whose action flips with the row state, e.g. Rename ↔ Save the name).
    private void SetHint(int kind, bool over, int a = -1, int b = -1)
    {
        int next = over ? kind : _hint == kind || _hint == a || _hint == b ? HintNone : _hint;
        if (next == _hint) return;
        _hint = next;
        Repaint();
    }

    private string OutfitsHint() => _hint switch
    {
        HintWear     => _loc.T("wardrobe.hint.wear"),
        HintRename   => _loc.T("wardrobe.hint.rename"),
        HintUpdate   => _loc.T("wardrobe.hint.update"),
        HintDelete   => _loc.T("wardrobe.hint.delete"),
        HintSaveName => _loc.T("wardrobe.hint.saveName"),
        HintCancel   => _loc.T("wardrobe.hint.cancel"),
        _            => Rows.Count > 0 ? _loc.T("wardrobe.hint.list") : "",
    };

    private ColorRgba OutfitsHintColor() => _hint != HintNone ? TextColor() : MutedColor();

    // ── footer line ───────────────────────────────────────────────────────────────────────────────────────────────
    // Feedback (green ✓ success / red failure, ~3 s) else a muted hint: the rename keys while renaming, otherwise where
    // the hotkeys are set.
    private bool FooterFeedback => _footerUntil > 0f;
    private string FooterHint() => _editingIdx >= 0 ? _loc.T("wardrobe.footer.rename") : _loc.T("wardrobe.footer.hotkeys");
}
