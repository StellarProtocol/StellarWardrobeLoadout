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

    private void InitOverlay()
    {
        RefreshRows();

        _window = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "wardrobeloadout.window",
                Title:       _loc.T("wardrobe.window.title"),
                // Column budget: 28 badge + 150 name + 64 pieces + 64 apply + 48 top + 56 delete + 5×6
                // gaps = 440, + GlassMenu body padding (24) + scrollbar inset (9) + margin.
                DefaultRect: new WindowRect(20f, 120f, 520f, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            {
                StartVisible = false, Closable = true, Draggable = true,
                Anchor = WindowAnchor.TopRight,
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            },
            BuildRoot(),
            OnClose: () => _window.SetVisiblePersist(false)));

        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            _loc.T("wardrobe.window.title"), IconPng: null, IconKey: null,
            OnOpen: ToggleOverlay)
        {
            ShouldShow = () => _services.ClientState.Phase == GamePhase.World,
            TitleProvider = () => _loc.T("wardrobe.window.title"),
        });

        _services.ClientState.Login += RefreshRows;
    }

    private void DisposeOverlay()
    {
        _services.ClientState.Login -= RefreshRows;
        try { _launcherEntry?.Dispose(); } catch { /* disposal must not throw */ }
        try { _window?.Remove(); } catch { /* disposal must not throw */ }
    }

    // Toggle called by the window-toggle hotkey (declared in Plugin.cs) and the launcher tile.
    private void ToggleOverlay()
    {
        if (_window is null) return;
        _window.SetVisiblePersist(!_window.IsShown);
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

    private void OnRenameRow(int idx, string newName)
    {
        if (RowAt(idx) is null || string.IsNullOrWhiteSpace(newName)) return;
        if (_store.Rename(CharacterKey, idx, newName.Trim())) { Persist(); RefreshRows(); }
    }

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

            var name = new CellElement(new InputElement(
                () => RowAt(idx)?.Name ?? "",
                v => OnRenameRow(idx, v), Width: 150f), Width: 150f);

            var pieces = new CellElement(new TextElement(
                () => RowAt(idx) is { } s ? _loc.TFormat("wardrobe.window.pieces", Worn(s.Regions)) : "",
                Muted, NoWrap: true), Width: 64f);

            var apply = new CellElement(new ButtonElement(
                () => _loc.T("wardrobe.window.apply"),
                () => OnApplyRow(idx),
                Enabled: () => RowAt(idx) is not null, Width: 60f), Width: 64f);

            var top = new CellElement(new ButtonElement(
                () => _loc.T("wardrobe.window.moveTop"),
                () => OnMoveTopRow(idx),
                Enabled: () => idx > 0 && RowAt(idx) is not null, Width: 44f), Width: 48f);

            var del = new CellElement(new ButtonElement(
                () => _loc.T("wardrobe.window.delete"),
                () => OnDeleteRow(idx),
                Enabled: () => RowAt(idx) is not null, Width: 52f), Width: 56f);

            pool[idx] = new RowElement(new HudElement[] { badge, name, pieces, apply, top, del }, Gap: 6f);
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
            Then: new ScrollElement(new ListElement(() => _rows.Count, pool, Columns: 1), Height: 300f),
            Else: new TextElement(() => _loc.T("wardrobe.window.empty"), Muted));

        return new ColumnElement(new HudElement[]
        {
            help, new SeparatorElement(), saveButton, new SeparatorElement(), list,
        }, Gap: 8f);
    }
}
