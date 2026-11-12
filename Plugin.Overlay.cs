using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// The wardrobe window (house style, approved prototype 2026-11-13 <c>_scratch/prototypes/wardrobe-redesign-APPROVED</c>):
/// a ToolHeader (hanger tile · "Wardrobe" · live status · Save current outfit · "?"), then an OUTFITS panel (the
/// drag-reorderable list) beside a PREVIEW panel (the hovered outfit on a live model), then a one-line footer for
/// feedback / hints. Storage + apply logic live in <c>Plugin.cs</c> / <see cref="WardrobeStore"/>; this partial owns the
/// window, its state and the 10 Hz tick. Rows: <c>Plugin.Overlay.Rows.cs</c>; root layout: <c>.Root.cs</c>; actions and
/// feedback: <c>.Actions.cs</c>; status / badge / label caches: <c>.Status.cs</c>; preview: <c>.Preview.cs</c>.
///
/// <para>The list is a <see cref="VirtualListElement"/>: a small pool of row widgets is recycled over the logical outfit
/// list as the user scrolls, so the number of outfits it can show is UNBOUNDED. Each pooled row therefore renders a
/// DIFFERENT outfit from refresh to refresh — every row closure resolves its logical index through <c>Idx()</c>
/// (= <c>_windowFirst</c> + pool slot), never a captured constant. Per-outfit visual state (preview ring, flash, worn,
/// hover) is keyed by the OUTFIT (object identity), not the index, so it follows the outfit through a reorder.</para>
/// </summary>
public sealed partial class Plugin
{
    // Window geometry (approved prototype): 800 wide, OUTFITS panel 488, PREVIEW panel takes the rest (280 = the 260 px
    // model + the panel's 2 × 10 padding). The list shows 17 rows at the 28 px pitch and reserves that height, so the
    // window never grows or shrinks with the number of outfits (R14).
    private const float WindowWidth = 800f;
    private const float OutfitsPanelWidth = 488f;
    private const int VisibleRows = 17;
    private const float ListTileHeight = 26f;
    private const float ListRowGap = 2f;
    private const float ListRowPitch = ListTileHeight + ListRowGap;                  // the VirtualList RowHeight
    private const float ListHeight = VisibleRows * ListRowPitch - ListRowGap;        // 474: ends flush on a tile
    private const int PreviewW = 260;
    private const float PreviewInfoHeight = 20f;   // the bold outfit-name line under the model; mirrored under the list
    // Under the name: the muted details line ("10 pieces · 9 hidden · weapon skin"), then the muted gesture hint. The
    // details got their own full-width line (2026-11-13: beside the name they squeezed it to "Outfi…"). The model stays
    // 424 tall (owner: grow the window, not shrink the model): details line + its gap = exactly one row pitch (22 + 6 =
    // 28), so the OUTFITS list gains one row (16 → 17) and both panels stay the same height.
    private const float PreviewHintHeight = 16f;
    private const float PreviewHintGap = 6f;       // = SectionPanel's column gap (Plugin.Style.Blocks.cs) — keep in step
    private const float PreviewDetailsHeight = ListRowPitch - PreviewHintGap;   // 22
    private const int PreviewH = (int)(ListHeight - PreviewHintGap - PreviewHintHeight
                                       - PreviewHintGap - PreviewDetailsHeight);   // 474 − 22 − 28 = 424

    // Pool size = rows that fit the viewport (17) plus margin for partial rows at both edges. NOT a cap on the outfit
    // count: the VirtualListElement recycles these over the whole list. (1.0.0 used a fixed 48-row EAGER pool, which
    // silently hid outfit 49 onward — a pool constant must never bound what the user can reach again.)
    private const int PoolRows = 20;

    // First logical outfit index the pool currently renders — written by the VirtualListElement's OnWindow BEFORE any
    // row Func is pulled, so a row's Idx() is correct for the frame being drawn.
    private int _windowFirst;

    private IWindowControl _window = null!;
    private IDisposable? _launcherEntry;
    private IDisposable? _tickScope;

    // The displayed list is read LIVE from the store (keyed by the char id), NOT cached — empty (never a name/"default"
    // fallback) until CharacterKey resolves. A store read is a cheap dict lookup on a cached key string.
    private IReadOnlyList<OutfitSlot> Rows => CharacterKey is { } key ? _store.Get(key) : Array.Empty<OutfitSlot>();

    private OutfitSlot? RowAt(int i) { var r = Rows; return i >= 0 && i < r.Count ? r[i] : null; }

    // Inline rename edit state. The name + pieces cells of THAT row become one input (Enter or ✓ commits, ✕ cancels).
    // _editBuffer is kept live by the input's OnChange so ✓ can read the typed value without an Enter first, and so
    // scrolling the row out of view (its pooled field is recycled) loses nothing. _focusSeq bumps on every rename start:
    // the field's FocusRequest takes keyboard focus so the user can type at once.
    private int _editingIdx = -1;
    private string _editBuffer = "";
    private int _focusSeq;

    // Inline confirm state for the destructive actions (update overwrites the outfit; delete removes it). The row swaps to
    // a question + two small pills. Mutually exclusive with the rename edit. 0 = none, 1 = update, 2 = delete.
    private const int ConfirmNone = 0, ConfirmUpdate = 1, ConfirmDelete = 2;
    private int _confirmIdx = -1;
    private int _confirmKind = ConfirmNone;

    // Misclick guard: the Delete / Replace pill lands exactly where that row's Wear / pencil were, so a quick second
    // click after ✕ / update would confirm at once (likely how an outfit got lost). The pill ignores clicks (and shows
    // recessed) for ConfirmGuardSec after arming; _confirmGuardPaint = the tick still owes the one repaint that
    // raises it again.
    private const float ConfirmGuardSec = 0.5f;
    private float _confirmArmedAt;
    private bool _confirmGuardPaint;

    // While a rename or a confirm is open, every OTHER row's actions and the drag reorder are paused: the confirm is
    // armed against a LOGICAL INDEX, so a move (or another delete) underneath it would make it hit a different outfit.
    private bool Busy => _editingIdx >= 0 || _confirmKind != ConfirmNone;

    // Hover-to-preview state. Hovering a row arms a debounced 3D preview (re-dressing the model loads each cosmetic's
    // assets, so a short settle avoids thrash when sweeping the mouse); _previewSlot is the outfit currently shown (it
    // also gets the accent ring). Debounce = IFramework.TimeNow deadline, checked by the 10 Hz tick (≈140–240 ms).
    private const float HoverDebounceSec = 0.14f;
    private OutfitSlot? _hoverPending;
    private float _hoverDeadline;
    private OutfitSlot? _previewSlot;

    // The saved outfit that equals what the player is wearing (green "Worn" chip + header status), or null. Checked at
    // ~2 Hz and ONLY while the window is shown (Plugin-Performance-Rules: skip work while hidden).
    private OutfitSlot? _wornSlot;
    private float _nextWornCheck;
    private const float WornCheckSec = 0.5f;

    // The outfit this plugin last applied successfully — the Worn tie-break among outfits with the same pieces. Held by
    // object identity, so it survives a reorder; cleared when that outfit is deleted and on logout.
    private OutfitSlot? _lastApplied;

    // The outfit an apply is in flight for (header "Switching to …", all Wear pills recessed). Set on the main thread
    // when the switch starts, cleared via IFramework.Post when it resolves (the apply completes off the main thread).
    private OutfitSlot? _switchingSlot;

    private void InitOverlay()
    {
        _window = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "wardrobeloadout.window",
                Title:       "Wardrobe",   // fallback only; TitleProvider carries the live-localized title
                DefaultRect: new WindowRect(20f, 120f, WindowWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            {
                StartVisible = false, Closable = true, Draggable = true,
                Anchor = WindowAnchor.TopRight,
                TitleProvider = () => _loc.T("wardrobe.window.title"),
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
                // Built ahead of the first open (structure at the title screen, values once in the world). Every value
                // lambda here only reads the plugin's own store/caches, the theme and the preview texture handle — safe
                // whenever the world is up. Re-filled when the character or the outfit count changes while hidden.
                Prebuild = true,
                PrebuildSignature = () => Rows.Count * 397 ^ (int)_services.PlayerState.CharId,
            },
            BuildRoot(),
            OnClose: () => { _window.SetVisiblePersist(false); OnWindowHidden(); }));

        RegisterTipWindow();

        // Title stays the fixed literal "Wardrobe" — the stable pin-identity key (ILauncher.cs:49-50) — so a pinned tile
        // survives a language change; TitleProvider carries the live-localized display.
        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            "Wardrobe", IconPng: LauncherIcon, IconKey: null,
            OnOpen: ToggleOverlay)
        {
            ShouldShow = () => _services.ClientState.Phase == GamePhase.World,
            TitleProvider = () => _loc.T("wardrobe.window.title"),
        });

        _services.WardrobePreview.SetViewport(PreviewW, PreviewH);
        // 10 Hz, not per frame: hover debounce, the worn check, badge labels and feedback expiry are all human-speed.
        _tickScope = _services.Framework.Every(TimeSpan.FromMilliseconds(100), OnTick);
        _services.ClientState.Logout += OnLogout;
    }

    private void DisposeOverlay()
    {
        try { _services.ClientState.Logout -= OnLogout; } catch { /* disposal must not throw */ }
        try { _tickScope?.Dispose(); } catch { /* disposal must not throw */ }
        HidePreview();
        try { _launcherEntry?.Dispose(); } catch { /* disposal must not throw */ }
        try { _tipWindow?.Remove(); } catch { /* disposal must not throw */ }
        try { _window?.Remove(); } catch { /* disposal must not throw */ }
    }

    // Toggle called by the window-toggle hotkey (declared in Plugin.cs) and the launcher tile.
    private void ToggleOverlay()
    {
        if (_window is null) return;
        var show = !_window.IsShown;
        _window.SetVisiblePersist(show);
        if (show) OnWindowShown();
        else OnWindowHidden();
    }

    // Fresh data for the first frame: the bound keys, the worn match and the icon scale are only polled while shown.
    private void OnWindowShown()
    {
        RefreshUiScale();
        RefreshBadges();
        _nextWornCheck = 0f;
        RefreshWorn();
    }

    // Closing drops the preview model, the help tip and any half-finished rename / confirm (a confirm armed against an
    // index must not survive into a later session of the window).
    private void OnWindowHidden()
    {
        HidePreview();
        CloseTip();
        _editingIdx = -1;
        _confirmIdx = -1;
        _confirmKind = ConfirmNone;
    }

    // Logout: hide synchronously (no Post — Update is world-gated, and the phase stays World through the logout
    // transition, so ShouldRender alone would leave the window up on the loading screen). SetVisible, not
    // SetVisiblePersist — the same logout close every house tool window does (Stellar-Design-Language §5).
    private void OnLogout()
    {
        try
        {
            _window?.SetVisible(false);
            OnWindowHidden();
            _switchingSlot = null;
            _wornSlot = null;
            _lastApplied = null;
            _hideRaw = null;
            _hideCurrent = null;
            _nextHideRead = 0f;
        }
        catch { /* never throw from an event */ }
    }

    // The 10 Hz tick (IFramework.Every). The name→char-id migration check runs ALWAYS (there is no char-changed event);
    // everything else only while the window is shown.
    private void OnTick()
    {
        TryMigrateNameToCharId();
        if (_window is not { IsShown: true }) return;

        float now = _services.Framework.TimeNow;
        RefreshUiScale();
        if (_hoverPending is { } pending && now >= _hoverDeadline)
        {
            _hoverPending = null;
            ShowPreview(pending);
        }
        if (now >= _nextWornCheck) RefreshWorn();
        if (now >= _nextBadgeRefresh) RefreshBadges();
        if (_confirmGuardPaint && ConfirmGuardOpen()) { _confirmGuardPaint = false; Repaint(); }
        ExpireFeedback(now);
    }

    // Row hover-enter arms a debounced preview; hover-leave keeps the last preview (no flicker moving between rows).
    // Icon / pill hovers on a row call this too, so pointing at a row's buttons previews it as well.
    private void OnRowHover(int idx, bool entered)
    {
        if (!entered || RowAt(idx) is not { } slot) return;
        _hoverPending = slot;
        _hoverDeadline = _services.Framework.TimeNow + HoverDebounceSec;
    }

    // Which saved outfit is being worn (WardrobeRules.MatchesWorn: same non-zero pieces). One GetWornOutfit read per
    // check, the worn side counted once, the saved maps walked allocation-free; repaint only when the match changes.
    // Show/hide counts too (WardrobeRules.PickWorn): an outfit whose saved state differs from the current one is a
    // modified look, not Worn; an exact match beats an outfit saved without a state; ties → last applied, list order.
    // The game's setting is read (cached, ≤1 Hz) only when a piece-matching outfit has a saved state to check.
    private void RefreshWorn()
    {
        _nextWornCheck = _services.Framework.TimeNow + WornCheckSec;
        OutfitSlot? match = null;
        var rows = Rows;
        if (rows.Count > 0 && _services.Wardrobe.IsAvailable && _services.Wardrobe.GetWornOutfit() is { } worn)
        {
            int nonZero = WardrobeRules.CountNonZero(worn);
            var current = WardrobeRules.NeedsHiddenState(rows, worn, nonZero) ? CurrentHiddenState() : null;
            match = WardrobeRules.PickWorn(rows, worn, nonZero, current, _lastApplied);
        }
        if (ReferenceEquals(match, _wornSlot)) return;
        _wornSlot = match;
        Repaint();
    }

    // Force an immediate repaint after a mutation (the list itself is read live via Rows).
    private void Repaint() => _window?.MarkDirty();
}
