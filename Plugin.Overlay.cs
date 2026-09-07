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
/// current character's outfits, even before the character name resolves at boot.
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

    // The displayed list is read LIVE from the store each frame (keyed by the current character), NOT
    // cached — so it always reflects the right character's outfits even before the character name resolves
    // at boot (the old cache read the empty "default" bucket at construction → list looked empty until a
    // save forced a refresh). A store read is a cheap dict lookup; the overlay only renders when open.
    private IReadOnlyList<OutfitSlot> Rows => _store.Get(CharacterKey);

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

        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            _loc.T("wardrobe.window.title"), IconPng: LauncherIcon, IconKey: null,
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
        // The weapon skin rides the preview map under key 731 — but only when it belongs to the class we're on
        // now (owner rule 2026-09-05); a skin is per-class, so another class's cannot render on this model.
        var currentProfessionId = _services.Wardrobe.GetWornWeaponSkin()?.ProfessionId ?? 0;
        var previewOutfit = WardrobeRules.PreviewOutfit(slot, currentProfessionId);
        _services.WardrobePreview.Show(_services.CombatSnapshot.LocalEntityId, previewOutfit, BuildPreviewDyes(slot));
        DiagPreview(slot, previewOutfit);
        _previewIdx = idx;
        _window?.MarkDirty();
    }

    // Choose the dye source for a preview: exact per-area (new saves) → legacy flat treated as positional
    // areas (older saves) → live worn dyes (outfits saved before any dye capture). Returns region → area → RGB.
    private IReadOnlyDictionary<int, IReadOnlyDictionary<int, float[]>> BuildPreviewDyes(OutfitSlot slot)
    {
        if (slot.DyeAreas.Count > 0) return ToAreaMap(slot.DyeAreas);
        if (slot.Dyes.Count > 0) return FlatToAreaMap(slot.Dyes);
        return ToAreaMap(CaptureDyeAreas());
    }

    private static IReadOnlyDictionary<int, IReadOnlyDictionary<int, float[]>> ToAreaMap(Dictionary<int, Dictionary<int, float[]>> src)
    {
        var map = new Dictionary<int, IReadOnlyDictionary<int, float[]>>(src.Count);
        foreach (var kv in src) map[kv.Key] = kv.Value;
        return map;
    }

    // Legacy flat triples → positional areas 1,2,3,… (approximate — the pre-fix behaviour, kept so outfits
    // saved before per-area capture still show colour until re-saved).
    private static IReadOnlyDictionary<int, IReadOnlyDictionary<int, float[]>> FlatToAreaMap(Dictionary<int, float[]> flat)
    {
        var map = new Dictionary<int, IReadOnlyDictionary<int, float[]>>(flat.Count);
        foreach (var kv in flat)
        {
            var f = kv.Value;
            var areas = new Dictionary<int, float[]>();
            for (int i = 0, a = 1; i + 2 < f.Length; i += 3, a++) areas[a] = new[] { f[i], f[i + 1], f[i + 2] };
            map[kv.Key] = areas;
        }
        return map;
    }

    private void HidePreview()
    {
        _hoverPendingIdx = -1;
        _previewIdx = -1;
        try { _services.WardrobePreview.Hide(); } catch { /* never throw */ }
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
        if (name.Length > 0 && RowAt(idx) is not null && _store.Rename(CharacterKey, idx, name))
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
        if (_store.Update(CharacterKey, idx, captured))
        {
            Persist();
            DiagUpdated(slot);   // slot IS the updated entry (Update overwrites in place)
            Toast(NoticeTipType.GreenBar, _loc.TFormat("wardrobe.toast.updated", slot.Name));
            Repaint();
        }
    }

    private void OnMoveUp(int idx)   { if (_store.Move(CharacterKey, idx, -1)) { Persist(); Repaint(); } }
    private void OnMoveDown(int idx) { if (_store.Move(CharacterKey, idx, +1)) { Persist(); Repaint(); } }

    // Runs on confirm ✓ (which has already cleared the confirm bar).
    private void DoDelete(int idx)
    {
        if (_store.Delete(CharacterKey, idx)) { Persist(); Repaint(); }
    }

    private ColorRgba? Muted() => _services.Theme.Colors.MenuMuted;

    // Row-action icons — one bold, uniform image set (the overlay font renders most of these poorly at this
    // size). Loaded once from embedded resources; the framework tints each with the theme text colour.
    // Launcher tile icon (clothes hanger) — replaces the generic puzzle-piece fallback.
    private static readonly byte[]? LauncherIcon = LoadEmbeddedIcon("Stellar.WardrobeLoadout.wardrobe-icon.png");

    private static byte[]? Icon(string n) => LoadEmbeddedIcon($"Stellar.WardrobeLoadout.Icons.{n}.png");
    private static readonly byte[]? EditIcon = Icon("edit");
    private static readonly byte[]? SaveIcon = Icon("save");        // also the confirm ✓
    private static readonly byte[]? RefreshIcon = Icon("refresh");
    private static readonly byte[]? UpIcon = Icon("up");
    private static readonly byte[]? DownIcon = Icon("down");
    private static readonly byte[]? ApplyIcon = Icon("apply");
    private static readonly byte[]? TrashIcon = Icon("trash");
    private static readonly byte[]? CancelIcon = Icon("cancel");    // the confirm ✗

    private static byte[]? LoadEmbeddedIcon(string name)
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream(name);
            if (s is null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // Compact toolbar-chip icon button, icon-only. Outline style = a defined accent border with a
    // near-transparent interior, so the (light) icon sits on the DARK row for high contrast — a light Glass
    // fill washes the icon out on a dark theme ("blended in"). Empty label → the framework centres the PNG.
    private static ButtonElement IconChip(byte[]? png, Action onClick, Func<bool>? enabled = null)
        => new(() => "", onClick, Enabled: enabled, Style: MenuButtonStyle.Outline, Width: 30f, Icon: () => png);

    // Placeholder for a hidden end-cap reorder arrow (top row has no up, last row no down) — keeps the
    // column aligned without showing an inert button.
    private static TextElement Empty() => new(() => "");

    // A row action is available when the row exists, it is NOT the row currently being renamed (so a
    // reorder / update / delete / apply can't race an in-progress rename edit), and NO confirm is armed on
    // ANY row. That last term matters because the confirm is armed against a LOGICAL INDEX: arm Delete on
    // row 5, then press ▼ on row 4, and rows 4 and 5 swap — the pending ✓ would delete the outfit that
    // moved into slot 5, i.e. the wrong one. Freezing every row's actions while a confirm is pending keeps
    // "the row you armed" and "the row that gets hit" the same row. (Edit is NOT gated on this: EnterEdit
    // clears the armed confirm before it starts a rename.)
    private bool RowActionable(int idx)
        => RowAt(idx) is not null && !IsEditing(idx) && _confirmKind == ConfirmNone;

    // Uniform pooled-row height the VirtualListElement positions rows on. The tallest thing in a row is an
    // icon chip, whose height is the framework's button floor Scaled(11)+12 (= 23 at FontScale 1.0, the
    // scale is Round()ed exactly as WindowBuilder.Scaled does); the SelectableElement wrapper adds its
    // 4+4 vertical padding, and 2 px reproduces the inter-row gap the eager ListElement had → 33 px at
    // scale 1.0 (sandbox-measured, see Stories.WardrobeLoadout). FontScale 0.8..1.4 → 31..37 px.
    // The arithmetic lives in WardrobeRules so it is unit-pinned (WardrobeRulesTests).
    private float RowHeight => WardrobeRules.RowHeightFor(_services.Theme.Text.FontScale);

    // The recycled row widgets. Each is built ONCE against its pool slot; the logical outfit it shows is
    // resolved per refresh through Idx().
    private HudElement[] BuildRowPool()
    {
        var pool = new HudElement[PoolRows];
        for (var i = 0; i < PoolRows; i++)
        {
            var slot = i;   // the POOL slot — fixed for the lifetime of the widget
            pool[slot] = BuildRow(() => _windowFirst + slot);
        }
        return pool;
    }

    // One pooled row. Idx() = the LOGICAL outfit index this widget renders on the current refresh
    // (_windowFirst + its pool slot) — EVERY closure below must go through it, never a captured constant,
    // or the row would act on whichever outfit it happened to show when the window was built.
    private HudElement BuildRow(Func<int> Idx)
    {
        // Badge column: the first HotkeySlotCount rows show their hotkey (H1..H8); every row past that
        // shows a plain 1-based position number (9, 10, 11, …) in the SAME column so every outfit is
        // numbered for reference (requested — makes a long list easier to scan). Empty past the real list.
        var badge = new CellElement(new TextElement(
            () => RowAt(Idx()) is null ? ""
                : Idx() < HotkeySlotCount ? _loc.TFormat("wardrobe.window.hotkeyBadge", Idx() + 1)
                : _loc.TFormat("wardrobe.window.rowBadge", Idx() + 1),
            Muted, NoWrap: true), Width: 28f);

        // Name: a LABEL by default; the row being edited swaps to an input field (same width). Wider
        // than before — the icon buttons freed the space, so long outfit names read fully.
        var name = new CellElement(new ConditionalElement(
            () => IsEditing(Idx()),
            Then: new InputElement(() => _editBuffer, _ => CommitRename(Idx()), Width: 178f, OnChange: s => _editBuffer = s),
            Else: new TextElement(() => RowAt(Idx())?.Name ?? "", NoWrap: true)), Width: 180f);

        var pieces = new CellElement(new TextElement(
            () => RowAt(Idx()) is { } s ? _loc.TFormat("wardrobe.window.pieces", Worn(s.Regions)) : "",
            Muted, NoWrap: true), Width: 40f);

        var row = new RowElement(new HudElement[] { badge, name, pieces, BuildActions(Idx) }, Gap: 6f);
        // Wrap in a Selectable so hovering the row loads its 3D preview (OnHover) and the previewed
        // row highlights (Selected). Row-click is a no-op — the per-cell buttons own the actions.
        return new SelectableElement(row, OnClick: () => { }, Selected: () => _previewIdx == Idx())
        {
            OnHover = on => OnRowHover(Idx(), on),
        };
    }

    // The row's right-hand action area: six icon chips (rename, update, up, down, apply, delete), swapped
    // for the inline confirm bar while this row awaits a yes/no on a destructive action.
    private HudElement BuildActions(Func<int> Idx)
    {
        // Edit ↔ Save (rename): pencil / check icons.
        var editSave = new CellElement(new ConditionalElement(
            () => IsEditing(Idx()),
            Then: IconChip(SaveIcon, () => CommitRename(Idx())),
            Else: IconChip(EditIcon, () => EnterEdit(Idx()), () => RowAt(Idx()) is not null)), Width: 32f);

        // Update (refresh) — re-capture the worn outfit into this slot; ARMS a confirm. Disabled while renaming.
        var update = new CellElement(
            IconChip(RefreshIcon, () => ArmConfirm(Idx(), ConfirmUpdate), () => RowActionable(Idx())), Width: 32f);

        // Reorder (triangles) — HIDDEN at the ends (no up on the first row, no down on the last), disabled while renaming.
        var up = new CellElement(new ConditionalElement(
            () => Idx() > 0,
            Then: IconChip(UpIcon, () => OnMoveUp(Idx()), () => RowActionable(Idx())),
            Else: Empty()), Width: 32f);
        var down = new CellElement(new ConditionalElement(
            () => Idx() < Rows.Count - 1,
            Then: IconChip(DownIcon, () => OnMoveDown(Idx()), () => RowActionable(Idx())),
            Else: Empty()), Width: 32f);

        // Apply (play) / Delete (trash → ARMS a confirm) — disabled while renaming.
        var apply = new CellElement(IconChip(ApplyIcon, () => OnApplyRow(Idx()), () => RowActionable(Idx())), Width: 32f);
        var del = new CellElement(IconChip(TrashIcon, () => ArmConfirm(Idx(), ConfirmDelete), () => RowActionable(Idx())), Width: 32f);

        var iconRow = new RowElement(new HudElement[] { editSave, update, up, down, apply, del }, Gap: 6f);

        // Inline confirm bar (no modal in the overlay): "Overwrite? / Delete?" + confirm ✓ + cancel ✗,
        // replacing the icon row for the row awaiting confirmation. The outfit name stays visible alongside.
        var confirmBar = new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(
                () => _confirmKind == ConfirmUpdate ? _loc.T("wardrobe.window.confirmUpdate") : _loc.T("wardrobe.window.confirmDelete"),
                NoWrap: true), Width: 78f),
            new CellElement(IconChip(SaveIcon,   () => ConfirmYes(Idx())), Width: 32f),
            new CellElement(IconChip(CancelIcon, () => ClearConfirm()),    Width: 32f),
        }, Gap: 6f);

        return new CellElement(new ConditionalElement(
            () => IsConfirming(Idx()), Then: confirmBar, Else: iconRow), Width: 224f);
    }

    private HudElement BuildRoot()
    {
        var pool = BuildRowPool();

        var help = new TextElement(() => _loc.T("wardrobe.window.help"), Muted);

        var saveButton = new ButtonElement(
            () => _loc.T("wardrobe.window.saveCurrent"), OnSaveCurrent, Width: 200f);

        // Scroll-windowed: the pool recycles over Rows.Count logical outfits, so the list reaches ALL of
        // them (the eager ListElement showed at most pool-size rows). The virtual list brings its own
        // scroll viewport — no ScrollElement wrapper. RowHeight is read once at build (the record takes a
        // float); a FontScale change re-skins text but keeps this pitch until the window is rebuilt.
        var list = new ConditionalElement(
            () => Rows.Count > 0,
            Then: new VirtualListElement(() => Rows.Count, RowHeight, pool, first => _windowFirst = first, Height: 460f),
            Else: new TextElement(() => _loc.T("wardrobe.window.empty"), Muted));

        // 3D preview pane (right): shows the hovered outfit on a live self model. Drag rotates, scroll
        // zooms, Shift+drag pans — same interaction model as the Entity Inspector's portrait. Transparent
        // backdrop so only the character draws (no dark box), matching the inspector.
        var previewPane = new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T("wardrobe.window.previewLabel"), Muted),
            new RenderTextureHostElement(
                () => _services.WardrobePreview.Texture, PreviewW, PreviewH,
                OnDrag: (dx, dy) => _services.WardrobePreview.Orbit(dx, dy),
                OnScroll: d => _services.WardrobePreview.Zoom(d),
                OnPan: (dx, dy) => _services.WardrobePreview.Pan(dx, dy),
                OnViewportResize: (w, h) => _services.WardrobePreview.SetViewport(w, h),
                TransparentBackground: true),
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
