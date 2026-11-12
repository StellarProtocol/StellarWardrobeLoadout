using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// Overlay partial: the window root (approved prototype) —
/// <code>
/// [hanger tile] Wardrobe                              [ Save current outfit ] (?)
///               Wearing “X” · N outfits
/// ┌ OUTFITS ─────────── hint ┐ ┌ PREVIEW ── drag to turn · scroll to zoom ┐
/// │ 16 rows, drag to reorder │ │ live model (260 × 446)                     │
/// │                          │ │ name                      7 pieces · …    │
/// └──────────────────────────┘ └────────────────────────────────────────────┘
/// footer: feedback line, else the hotkey hint
/// </code>
/// Every height is fixed (list 16 rows, model = list height, 20 px info line mirrored under the list, 18 px footer), so
/// the window never grows or shrinks with the outfit count, a status change or a feedback line (R14).
/// </summary>
public sealed partial class Plugin
{
    private const float FooterHeight = 18f;
    private const int StatusNameMax = 32;   // a very long outfit name is cut in the header status, never widens it

    private readonly ContrastCache _footerBad = new();

    private HudElement BuildRoot() => new ColumnElement(new HudElement[]
    {
        BuildHeader(),
        new RowElement(new HudElement[]
        {
            new CellElement(BuildOutfitsPanel(), Width: OutfitsPanelWidth),
            new CellElement(BuildPreviewPanel(), Weight: 1f),
        }, Gap: 8f),
        FixedLine(FooterHeight, new ConditionalElement(() => FooterFeedback,
            Then: new TextElement(() => _footerText,
                Color: () => _footerOk ? SuccessColor() : _footerBad.Get(RemoveGlyphColor(), Menu.MenuBackground, PillMinContrast),
                NoWrap: true, FontSize: 12) { Bold = true, Ellipsize = true },
            Else: new TextElement(FooterHint, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true })),
    }, Gap: 8f);

    // ToolHeader: 40 px accent tile · bold title over the live status line · the Save pill · the round "?".
    private HudElement BuildHeader() => new RowElement(new HudElement[]
    {
        HeaderIconTile(),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T("wardrobe.window.title"), Color: () => TextColor(), Emphasis: true, NoWrap: true),
            // Bold coloured part ("Wearing “X”" / "Switching to “X”…") + the muted rest; one shared builder.
            new RowElement(new HudElement[]
            {
                new TextElement(StatusMain, Color: () => StatusMainColor(), NoWrap: true, FontSize: 13) { Bold = true },
                new TextElement(StatusTail, Color: () => MutedColor(), NoWrap: true),
            }, Gap: 0.01f),
        }, Gap: 2f), Weight: 1f),
        PrimaryHeaderPill(() => _loc.T("wardrobe.window.saveCurrent"), OnSaveCurrent),
        HelpButton(),
    }, Gap: 10f);

    // OUTFITS: the drag-reorderable virtual list (16 rows reserved), or the empty state in the same fixed height. The
    // spacer under it mirrors the PREVIEW panel's info line, so both panels are the same height.
    private HudElement BuildOutfitsPanel() => SectionPanel(
        () => _loc.T("wardrobe.panel.outfits"), OutfitsHint, OutfitsHintColor,
        new ConditionalElement(() => Rows.Count > 0,
            Then: new VirtualListElement(() => Rows.Count, ListRowPitch, BuildRowPool(), first => _windowFirst = first,
                Height: ListHeight)
            {
                // Drag a row to move it (< 6 px = a normal click; Esc or a drop outside cancels; the list auto-scrolls
                // at its edges). Remove-then-insert ITEM indices → WardrobeStore.MoveTo, persisted. Ignored while a
                // rename / confirm is open (OnReorder).
                OnReorder = OnReorder,
                FixedHeight = true,
            },
            Else: EmptyState()),
        new SpacerElement(0f, PreviewInfoHeight));

    // Empty: a quiet box in the list's place. ⚠️ The prototype draws it DASHED; BubbleElement only has a solid 1 px
    // outline, so the box uses a faint solid ring (toolkit limit, reported).
    private HudElement EmptyState() => new ScrollElement(new BubbleElement(new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, 64f),
        new TextElement(() => _loc.T("wardrobe.empty.title"), Color: () => TextColor(), Align: TextAlign.Center,
            FontSize: 13) { Bold = true },
        new TextElement(() => _loc.T("wardrobe.empty.body"), Color: () => MutedColor(), Align: TextAlign.Center),
        new SpacerElement(0f, 64f),
    }, Gap: 4f))
    {
        FillColorFn = () => Transparent,
        OutlineColorFn = () => Fade(MutedColor() with { A = 0.45f }),
        CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
    }, Height: ListHeight) { FixedHeight = true };

    // PREVIEW: the hovered outfit on a live self model (transparent backdrop, as the Entity Inspector portrait): drag
    // rotates, scroll zooms, Shift+drag pans. Under it: bold outfit name + muted "N pieces · weapon skin".
    private HudElement BuildPreviewPanel() => SectionPanel(
        () => _loc.T("wardrobe.panel.preview"), () => _loc.T("wardrobe.hint.preview"), MutedColor,
        new RenderTextureHostElement(
            () => _services.WardrobePreview.Texture, PreviewW, PreviewH,
            OnDrag: (dx, dy) => _services.WardrobePreview.Orbit(dx, dy),
            OnScroll: d => _services.WardrobePreview.Zoom(d),
            OnPan: (dx, dy) => _services.WardrobePreview.Pan(dx, dy),
            OnViewportResize: (w, h) => _services.WardrobePreview.SetViewport(w, h),
            TransparentBackground: true),
        FixedLine(PreviewInfoHeight, new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _previewName, Color: () => TextColor(), NoWrap: true, FontSize: 13)
                { Bold = true, Ellipsize = true }, Weight: 1f),
            new TextElement(() => _previewInfo, Color: () => MutedColor(), NoWrap: true),
        }, Gap: 6f)));

    // The header status cuts a very long outfit name (the status line has no width cap of its own).
    private static string ShortName(string name)
        => name.Length <= StatusNameMax ? name : name.Substring(0, StatusNameMax - 1) + "…";
}
