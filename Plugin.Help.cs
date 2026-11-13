using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

// The round "?" in the window header and the house help popover it opens (ported from StellarMaestroPlugin
// Plugin.Help.cs / Plugin.Help.Tip.cs, trimmed to the one help text this window has). The toolkit has no tooltips, so the
// paragraph that used to sit at the top of the window lives behind the "?":
//   [32 px accent tile with a white bold "?"] [bold "Wardrobe" / muted "WARDROBE · HELP"] … [round ✕]
//   then the help text in one raised tile, 360 wide.
// No framework title bar (the header carries the title and a close); the whole frame drags. Anchored under the clicked
// "?" (screen px → canvas units), re-clicking the "?" closes it.
public sealed partial class Plugin
{
    private IWindowControl _tipWindow = null!;
    private const float TipWidth = 360f;
    private const float TipIconSize = 32f;
    private const float HelpSize = 24f;
    private readonly HoverFlag _helpHover = new();
    private readonly HoverFlag _tipCloseHover = new();
    private readonly ContrastCache _helpText = new();
    private WindowRect _tipRect;     // where the tip should sit (re-applied for a few frames after opening)
    private int _tipRepositionLeft;

    private void RegisterTipWindow()
    {
        _tipWindow = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "wardrobeloadout.tip",
                Title:       "Wardrobe help",   // fallback only (no title bar is drawn); localized below
                DefaultRect: new WindowRect(20f, 120f, TipWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            {
                StartVisible = false, Closable = true, Draggable = true, ShowTitleBar = false,
                TitleProvider = () => _loc.T("wardrobe.help.label"),
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            },
            BuildTipRoot(),
            OnClose: CloseTip));
    }

    private bool TipOpen => _tipWindow is { IsShown: true };

    private void CloseTip()
    {
        _tipRepositionLeft = 0;
        try { _tipWindow?.SetVisible(false); } catch { /* never throw */ }
    }

    // The "?" click: re-clicking the open tip closes it; otherwise anchor it under the "?", opening to the LEFT of it.
    private void ToggleTip(WindowRect r)
    {
        if (TipOpen) { CloseTip(); return; }
        // OnClickWithRect reports SCREEN PIXELS; SetRect takes CANVAS units — scale first, or the tip drifts off its "?"
        // whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 1 ? (float)fw.CanvasWidth / fw.ScreenWidth : 1f;
        r = new WindowRect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
        float canvasH = fw.CanvasHeight > 1 ? fw.CanvasHeight : fw.ScreenHeight;
        float x = MathF.Max(10f, r.X + r.Width - TipWidth);
        float y = MathF.Min(r.Y + r.Height + 4f, canvasH - 40f);
        _tipRect = new WindowRect(x, y, TipWidth, 0f);
        _tipWindow.SetVisible(true);   // opens from hidden → remounts on top (no BringToFront needed, Rule 5)
        _tipWindow.SetRect(_tipRect);
        // The first-ever open mounts the window and applies its DefaultRect AFTER this SetRect → re-assert the anchored
        // rect for the next few frames (one-shot Posts, not a standing per-frame subscription).
        _tipRepositionLeft = 4;
        _services.Framework.Post(TipReposition);
    }

    private void TipReposition()
    {
        if (_tipRepositionLeft <= 0) return;
        _tipRepositionLeft--;
        if (TipOpen) _tipWindow.SetRect(_tipRect);
        if (_tipRepositionLeft > 0) _services.Framework.Post(TipReposition);
    }

    // The round "?" (24 × 24, R1/R4): Neutral over the window, lighter on hover, accent-tinted while its tip is open.
    private HudElement HelpButton() => new BubbleElement(new SelectableElement(
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, HelpSize),
            new TextElement(() => "?", Color: () => _helpText.Get(TextColor(), HelpFill(), PillMinContrast), Width: HelpSize,
                Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: () => { })                       // the work is in OnClickWithRect (needs the button's rect)
        {
            Bare = true,
            OnHover = over => _helpHover.On = over,
            OnClickWithRect = ToggleTip,
        })
    {
        FillColorFn = () => Fade(TipOpen ? Over(AccentColor() with { A = 0.35f }, HelpFill())
                                 : _helpHover.On ? HoverFill(HelpFill()) : HelpFill()),
        CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
    };

    // The "?" sits on the window background (not a tile): the house Neutral built over the window.
    private ColorRgba HelpFill() => Over(Menu.MenuAccent with { A = NeutralAccentMix },
                                         Over(Menu.MenuMuted with { A = NeutralMix }, PanelColor()));

    private HudElement BuildTipRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            TipIcon(),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => _loc.T("wardrobe.window.title"), Color: () => TextColor(), NoWrap: true, FontSize: 14)
                    { Bold = true, Ellipsize = true },
                new TextElement(TipSubtitle(), Color: () => MutedColor(), NoWrap: true, FontSize: 11)
                    { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            TipCloseButton(),
        }, Gap: 10f),
        new BubbleElement(new TextElement(() => _loc.T("wardrobe.help.body"), Color: () => TextColor()))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        },
    }, Gap: 10f);

    // "WARDROBE · HELP", upper-cased once per language.
    private Func<string> TipSubtitle()
    {
        string? title = null, help = null, value = "";
        return () =>
        {
            var t = _loc.T("wardrobe.window.title");
            var h = _loc.T("wardrobe.help.label");
            if (!ReferenceEquals(t, title) || !ReferenceEquals(h, help))
            {
                title = t; help = h;
                value = (t + " · " + h).ToUpperInvariant();
            }
            return value;
        };
    }

    // 32 px accent tile with a white bold "?" (radius 7 keeps the sliced tile's min size under the 32 px side).
    private HudElement TipIcon() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, TipIconSize),
            new TextElement(() => "?", Color: () => White, Width: TipIconSize, Align: TextAlign.Center, NoWrap: true,
                FontSize: 18) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Fade(AccentColor()), CornerRadius = 7f, PaddingX = 0f, PaddingY = 0f,
    }, Width: TipIconSize);

    // Icon-only ✕ that closes the tip: a round 24 px hover wash around a Bare selectable; muted glyph at rest, text
    // colour on hover. The ✕ is the generated glyph (bold "✕" text can draw blank, KB §6).
    private HudElement TipCloseButton() => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
        {
            new SpacerElement(5f, 24f),
            Glyph(CloseIcon, () => _tipCloseHover.On ? TextColor() : MutedColor()),
            new SpacerElement(5f, 24f),
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: CloseTip) { Bare = true, OnHover = over => _tipCloseHover.On = over })
    {
        FillColorFn = () => _tipCloseHover.On ? Fade(Menu.MenuText with { A = HoverMix }) : Transparent,
        CornerRadius = RoundRadius(24f), PaddingX = 0f, PaddingY = 0f,
    };
}
