using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// Overlay partial: the row-action icons, the pooled list rows and the window root. Split out of
/// <c>Plugin.Overlay.cs</c> for the 500-LoC file gate — a pure move.
/// </summary>
public sealed partial class Plugin
{
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
