using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// The wardrobe overlay: a "Save current outfit" button plus a scrollable list of the current
/// character's saved outfits — each row shows its hotkey badge (H1..H8 for the first 8), an editable
/// name, its piece count, and Apply / Top (move-to-first) / Delete controls. Storage + apply logic
/// live in <see cref="Plugin"/> (<c>Plugin.cs</c> / <see cref="WardrobeStore"/>); this partial is
/// presentation only. The displayed list is cached in <see cref="_rows"/> and refreshed on every
/// mutation and on login (character switch) — never re-read inside a poll-diffed row func.
/// </summary>
public sealed partial class Plugin
{
    // Over-provisioned above any realistic saved-outfit count; rows are built ONCE and read live via
    // RowAt(idx). The list is unlimited in storage, but the visible pool caps at this many.
    private const int RowPoolSize = 48;

    private IWindowControl _window = null!;
    private IDisposable? _launcherEntry;
    private IReadOnlyList<OutfitSlot> _rows = Array.Empty<OutfitSlot>();

    // Inline rename edit state. The name is a LABEL by default; clicking Edit turns THAT row into an
    // input field (Save/Enter commits it and returns to a label). _editBuffer is kept live by the
    // input's OnChange so the Save button can read the typed value without an Enter first (the framework
    // InputElement fires Submit only on Enter/blur — see Stellar.Infrastructure LayoutPanel).
    private int _editingIdx = -1;
    private string _editBuffer = "";

    // Hover-to-preview state. Hovering a row arms a debounced 3D preview (re-dressing the model loads each
    // cosmetic's assets, so a short settle avoids thrash when sweeping the mouse). _previewIdx is the outfit
    // currently shown (also highlights that row).
    private const int PreviewW = 240;
    private const int PreviewH = 460;
    private const long HoverDebounceTicks = 8;   // ~130 ms before the hovered outfit loads
    private long _tick;
    private int _hoverPendingIdx = -1;
    private long _hoverDeadline;
    private int _previewIdx = -1;

    private void InitOverlay()
    {
        RefreshRows();

        _window = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "wardrobeloadout.window",
                Title:       _loc.T("wardrobe.window.title"),
                // 540 list column (454 row budget + scroll inset) + 8 gap + 248 preview pane + GlassMenu
                // body padding (24) + margin.
                DefaultRect: new WindowRect(20f, 120f, 840f, 0f),
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

        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            _loc.T("wardrobe.window.title"), IconPng: null, IconKey: null,
            OnOpen: ToggleOverlay)
        {
            ShouldShow = () => _services.ClientState.Phase == GamePhase.World,
            TitleProvider = () => _loc.T("wardrobe.window.title"),
        });

        _services.ClientState.Login += RefreshRows;
        _services.WardrobePreview.SetViewport(PreviewW, PreviewH);
        _services.Framework.Update += OnPreviewTick;
    }

    private void DisposeOverlay()
    {
        _services.Framework.Update -= OnPreviewTick;
        _services.ClientState.Login -= RefreshRows;
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

    // Debounce tick (framework Update). When a hovered row has settled, load its 3D preview.
    private void OnPreviewTick(float dt)
    {
        _tick++;
        if (_hoverPendingIdx < 0 || _tick < _hoverDeadline) return;
        var idx = _hoverPendingIdx;
        _hoverPendingIdx = -1;
        ShowPreview(idx);
    }

    private void ShowPreview(int idx)
    {
        if (idx == _previewIdx) return;   // already showing this outfit
        if (RowAt(idx) is not { } slot) return;
        _services.WardrobePreview.Show(_services.CombatSnapshot.LocalEntityId, slot.Regions);
        _previewIdx = idx;
        _window?.MarkDirty();
    }

    private void HidePreview()
    {
        _hoverPendingIdx = -1;
        _previewIdx = -1;
        try { _services.WardrobePreview.Hide(); } catch { /* never throw */ }
    }

    // Re-read the current character's outfits into the display cache, then repaint. Called after every
    // mutation (save/rename/delete/reorder) and on login (character switch).
    private void RefreshRows()
    {
        _rows = _store.Get(CharacterKey);
        _window?.MarkDirty();
    }

    private OutfitSlot? RowAt(int i) => i < _rows.Count ? _rows[i] : null;

    private void OnSaveCurrent()
    {
        if (SaveCurrentOutfit()) RefreshRows();
    }

    // Turn the row's name into an editable field, seeded with the current name.
    private void EnterEdit(int idx)
    {
        if (RowAt(idx) is not { } slot) return;
        _editingIdx = idx;
        _editBuffer = slot.Name;
        _window?.MarkDirty();
    }

    // Persist the edited name (from the live _editBuffer) and return the row to a label. Called by the
    // Save button AND by the input's Enter/blur Submit — both go through here so they can't disagree.
    private void CommitRename(int idx)
    {
        var name = _editBuffer?.Trim() ?? "";
        if (name.Length > 0 && RowAt(idx) is not null && _store.Rename(CharacterKey, idx, name))
        {
            Persist();
        }
        _editingIdx = -1;
        RefreshRows();
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

    private void OnMoveTopRow(int idx)
    {
        if (_store.MoveToTop(CharacterKey, idx)) { Persist(); RefreshRows(); }
    }

    private void OnDeleteRow(int idx)
    {
        if (_store.Delete(CharacterKey, idx)) { Persist(); RefreshRows(); }
    }

    private ColorRgba? Muted() => _services.Theme.Colors.MenuMuted;

    private HudElement[] BuildRowPool()
    {
        var pool = new HudElement[RowPoolSize];
        for (var i = 0; i < RowPoolSize; i++)
        {
            var idx = i;   // capture per row

            var badge = new CellElement(new TextElement(
                () => idx < HotkeySlotCount && RowAt(idx) is not null ? _loc.TFormat("wardrobe.window.hotkeyBadge", idx + 1) : "",
                Muted, NoWrap: true), Width: 28f);

            // Name: a LABEL by default; the row being edited swaps to an input field (same width).
            var name = new CellElement(new ConditionalElement(
                () => IsEditing(idx),
                Then: new InputElement(() => _editBuffer, _ => CommitRename(idx), Width: 130f, OnChange: s => _editBuffer = s),
                Else: new TextElement(() => RowAt(idx)?.Name ?? "", NoWrap: true)), Width: 130f);

            // Edit ↔ Save toggle for that row.
            var editSave = new CellElement(new ConditionalElement(
                () => IsEditing(idx),
                Then: new ButtonElement(() => _loc.T("wardrobe.window.save"), () => CommitRename(idx), Width: 44f),
                Else: new ButtonElement(() => _loc.T("wardrobe.window.edit"), () => EnterEdit(idx),
                    Enabled: () => RowAt(idx) is not null, Width: 44f)), Width: 48f);

            var pieces = new CellElement(new TextElement(
                () => RowAt(idx) is { } s ? _loc.TFormat("wardrobe.window.pieces", Worn(s.Regions)) : "",
                Muted, NoWrap: true), Width: 56f);

            // Apply / Top / Delete are disabled while THIS row is being renamed (so a reorder/delete
            // can't race the edit — the bug where moving a row reverted the new name).
            var apply = new CellElement(new ButtonElement(
                () => _loc.T("wardrobe.window.apply"),
                () => OnApplyRow(idx),
                Enabled: () => RowAt(idx) is not null && !IsEditing(idx), Width: 56f), Width: 60f);

            var top = new CellElement(new ButtonElement(
                () => _loc.T("wardrobe.window.moveTop"),
                () => OnMoveTopRow(idx),
                Enabled: () => idx > 0 && RowAt(idx) is not null && !IsEditing(idx), Width: 40f), Width: 44f);

            var del = new CellElement(new ButtonElement(
                () => _loc.T("wardrobe.window.delete"),
                () => OnDeleteRow(idx),
                Enabled: () => RowAt(idx) is not null && !IsEditing(idx), Width: 48f), Width: 52f);

            var row = new RowElement(new HudElement[] { badge, name, editSave, pieces, apply, top, del }, Gap: 6f);
            // Wrap in a Selectable so hovering the row loads its 3D preview (OnHover) and the previewed
            // row highlights (Selected). Row-click is a no-op — the per-cell buttons own the actions.
            pool[idx] = new SelectableElement(row, OnClick: () => { },
                Selected: () => _previewIdx == idx, OnHover: on => OnRowHover(idx, on));
        }
        return pool;
    }

    private HudElement BuildRoot()
    {
        var pool = BuildRowPool();

        var help = new TextElement(() => _loc.T("wardrobe.window.help"), Muted);

        var saveButton = new ButtonElement(
            () => _loc.T("wardrobe.window.saveCurrent"), OnSaveCurrent, Width: 200f);

        var list = new ConditionalElement(
            () => _rows.Count > 0,
            Then: new ScrollElement(new ListElement(() => _rows.Count, pool, Columns: 1), Height: 460f),
            Else: new TextElement(() => _loc.T("wardrobe.window.empty"), Muted));

        // 3D preview pane (right): shows the hovered outfit on a live self model; drag to rotate.
        var previewPane = new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T("wardrobe.window.previewLabel"), Muted),
            new RenderTextureHostElement(
                () => _services.WardrobePreview.Texture, PreviewW, PreviewH,
                OnDrag: (dx, dy) => _services.WardrobePreview.Orbit(dx, dy),
                OnViewportResize: (w, h) => _services.WardrobePreview.SetViewport(w, h)),
        }, Gap: 4f);

        var listAndPreview = new RowElement(new HudElement[]
        {
            new CellElement(list, Width: 540f),
            new CellElement(previewPane, Width: PreviewW + 8f),
        }, Gap: 8f);

        return new ColumnElement(new HudElement[]
        {
            help, new SeparatorElement(), saveButton, new SeparatorElement(), listAndPreview,
        }, Gap: 8f);
    }
}
