using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

// House style (Stellar Design Language, Knowledge Base\Stellar-Design-Language.md) — the palette and the pill / panel
// kit the Wardrobe window builds from. Ported from the reference kits (StellarMaestroPlugin Plugin.Style*.cs, the
// recessed disabled look from ChatTools e349c56) and trimmed to what this window uses. Needs SDK >= 2.30.0 (theme status
// colours, ImageElement.Tint, BubbleElement outlines).
//
// Toolkit limits behind some choices (WindowBuilder-Patterns.md):
//   • Plain TextElement ignores FontSize on Menu windows (fixed 14px) — only the styled path (Bold/Emphasis) honours it,
//     so small captions and pill labels are Bold+FontSize while hints stay at the fixed plain size.
//   • Row/Column Gap 0 means the DEFAULT gap (8/12), never zero — every container here passes an explicit Gap.
//   • A Bubble hugs its child's height, so every round control pins its height with a 0-width SpacerElement strut.
public sealed partial class Plugin
{
    // ── palette: LIVE from the current framework theme ────────────────────────────────────────────────────────────
    // Derived from _services.Theme.Colors on every call and handed to elements as Funcs, so a theme switch repaints on
    // the next refresh. Status green / red / amber come from the theme too (IThemeStatusColors, fw 2.30.0).
    private static readonly ColorRgba PanelShade = new(0f, 0f, 0f, 0.20f);   // panel = background darkened
    private const float TileTextMix      = 0.08f;   // raised tile = panel + 8% MenuText …
    private const float TileAccentMix    = 0.06f;   // … + a 6% accent tint (teal on teal, pink on pink)
    private const float NeutralMix       = 0.22f;   // neutral pill = tile + 22% MenuMuted …
    private const float NeutralAccentMix = 0.08f;   // … + an 8% accent tint, like the tiles
    private const float PillMinContrast  = 4.5f;    // tinted fills (Neutral, Caution, chips) keep 4.5:1 labels
    private const float SolidMinContrast = 3.0f;    // white on a SOLID fill (Primary, Danger) keeps white down to 3:1
    private const float ChipWashAlpha    = 0.22f;   // coloured chip = bold coloured text on a 22% wash of its colour
    private const float DangerFillAlpha  = 0.85f;   // the house Danger pill
    private const float HoverMix         = 0.10f;   // hover = 10% MenuText over the fill
    private static readonly ColorRgba SolidDarkLabel = new(0.12f, 0.12f, 0.12f);   // #1F1F1F: label when white < 3:1
    private static readonly ColorRgba White       = new(1f, 1f, 1f);
    private static readonly ColorRgba Transparent = new(0f, 0f, 0f, 0f);   // NOT null: a null FillColorFn = the default accent blob

    private IThemeColors Menu => _services.Theme.Colors;

    private ColorRgba TextColor()   => Menu.MenuText;
    private ColorRgba MutedColor()  => Menu.MenuMuted;
    private ColorRgba AccentColor() => Menu.MenuAccent;
    private ColorRgba SuccessColor() => Menu.Success;
    private ColorRgba DangerColor()  => Menu.Danger;
    private ColorRgba PanelColor()  => Over(PanelShade, Menu.MenuBackground);
    private ColorRgba TileColor()   => Over(Menu.MenuAccent with { A = TileAccentMix },
                                            Over(Menu.MenuText with { A = TileTextMix }, PanelColor()));
    // The small pills sit on a raised row tile, the header pill on the window: Neutral is built over the tile (the
    // only place it is used here: the inline Cancel).
    private ColorRgba NeutralFill() => Over(Menu.MenuAccent with { A = NeutralAccentMix },
                                            Over(Menu.MenuMuted with { A = NeutralMix }, TileColor()));
    private ColorRgba PrimaryFill() => Menu.MenuAccent;
    private ColorRgba DangerFill()  => Over(Menu.Danger with { A = DangerFillAlpha }, TileColor());
    // Caution: a TINTED amber pill (the amber chip wash over the row) — one step below the solid red Danger.
    private ColorRgba CautionFill() => Over(Menu.Caution with { A = ChipWashAlpha }, TileColor());
    private ColorRgba HoverFill(ColorRgba fill) => Over(Menu.MenuText with { A = HoverMix }, fill);

    // Solid-fill label rule (KB Stellar-Design-Language, fw 8df7f29): WHITE when white reaches 3:1 on the fill, otherwise
    // near-black — never a "darkened white" mid grey, which bold thin text can't carry.
    private static ColorRgba SolidLabel(ColorRgba fill)
        => ContrastRatio(1f, Luminance(fill)) >= SolidMinContrast ? White : SolidDarkLabel;

    // SolidLabel per call site, recomputed only when the fill changes (a theme switch), not on every poll.
    private sealed class SolidLabelCache
    {
        private ColorRgba _fill, _out;
        private bool _valid;

        public ColorRgba Get(ColorRgba fill)
        {
            if (_valid && fill.Equals(_fill)) return _out;
            _fill = fill; _out = SolidLabel(fill); _valid = true;
            return _out;
        }
    }

    // ── window opacity: EVERY coloured fill follows the window opacity, uniformly ──────────────────────────────────
    // The framework fades ONLY the GlassMenu frame Image (Themes → Window Opacity); every bubble fill here is an opaque
    // pre-blended colour, so at a low opacity the panels and pills would stay solid on a see-through frame. Every fill
    // therefore goes through Fade with the frame's factor; only text and pictures stay solid (house rule 2026-11-07).
    // Composed OPAQUE first, alpha last. A cast + property read per poll, no allocation.
    private float WindowOpacity()
    {
        float? v;
        try { v = (_services.NamedTheme as IChromeStyle)?.WindowOpacity; } catch { v = null; }
        return v is { } f ? Math.Clamp(f, 0.3f, 1f) : 1f;
    }

    private ColorRgba Fade(ColorRgba c) => c with { A = c.A * WindowOpacity() };

    // ── recessed disabled look (house rule 22) ────────────────────────────────────────────────────────────────────
    // A disabled pill is RECESSED: a sunken fill darker than its surface (black @28 %), a faint label, no ring, same size
    // and radius. Mirrors the framework's internal StellarStyle.DisabledFill/DisabledLabel (ChatTools e349c56). The fill
    // is drawn translucent (the renderer blends it over the real surface), so it already fades with the window; the
    // pre-blended Over(shade, surface) is used ONLY for the label contrast.
    private static readonly ColorRgba DisabledShade = new(0f, 0f, 0f, 0.28f);
    private const float DisabledLabelAlpha = 0.32f;        // text @32 % …
    private const float DisabledLabelMinContrast = 2.0f;   // … raised until 2:1 on the sunken fill (Light themes)

    // Per-call-site cache: the label only changes with the theme, so the contrast loop runs once per colour change.
    private sealed class DisabledLabelCache { public ColorRgba Text, Surface, Label; public bool Valid; }

    private ColorRgba DisabledLabel(ColorRgba surface, DisabledLabelCache cache)
    {
        var text = Menu.MenuText;
        if (cache.Valid && text.Equals(cache.Text) && surface.Equals(cache.Surface)) return cache.Label;
        var bg = Over(DisabledShade, surface);
        float lb = Luminance(bg), a = DisabledLabelAlpha;
        while (a < 1f && ContrastRatio(Luminance(Over(text with { A = a }, bg)), lb) < DisabledLabelMinContrast)
            a = MathF.Min(1f, a + 0.04f);
        cache.Text = text; cache.Surface = surface; cache.Label = text with { A = a }; cache.Valid = true;
        return cache.Label;
    }

    // ── contrast maths (ChatTools Plugin.Tabs.cs → MultiTool / Maestro Plugin.Style.cs) ───────────────────────────
    // EnsureContrast walks up to 12 steps of pow() maths; labels poll it every refresh with inputs that only change with
    // the theme, so each call site keeps its last answer in a ContrastCache.
    private sealed class ContrastCache
    {
        private ColorRgba _fg, _bg, _out;
        private bool _valid;

        public ColorRgba Get(ColorRgba fg, ColorRgba bg, float ratio)
        {
            if (_valid && fg.Equals(_fg) && bg.Equals(_bg)) return _out;
            _fg = fg; _bg = bg; _out = EnsureContrast(fg, bg, ratio); _valid = true;
            return _out;
        }
    }

    // Nudges fg toward white (dark bg) or black (light bg) in 15% steps until it reaches minRatio against the opaque bg.
    private static ColorRgba EnsureContrast(ColorRgba fg, ColorRgba bg, float minRatio)
    {
        float lb = Luminance(bg);
        var target = lb < 0.179f ? White : new ColorRgba(0f, 0f, 0f);
        for (int i = 0; i < 12 && ContrastRatio(Luminance(fg), lb) < minRatio; i++) fg = Mix(fg, target, 0.15f);
        return fg with { A = 1f };
    }

    private static float ContrastRatio(float la, float lb) => (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);
    private static float Luminance(ColorRgba c) => 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);
    private static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    // a → b by t (0 = a, 1 = b), opaque.
    private static ColorRgba Mix(ColorRgba a, ColorRgba b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    // Alpha-composite fg (its A = coverage) over an opaque bg.
    private static ColorRgba Over(ColorRgba fg, ColorRgba bg) => Mix(bg, fg with { A = 1f }, fg.A);

    // ── shape ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // The corner radius for a ROUND (capsule / circle) bubble of side `side`: side / 2 − 1, NOT side / 2. The bubble's
    // 9-slice background reports a MIN size of its summed sprite borders (radius + 1 each side), so radius side/2 drew
    // every round control 2 px taller than its content (Maestro, measured in-game 2026-11-06).
    private static float RoundRadius(float side) => side / 2f - 1f;
    private const float TileRadius = 6f;   // panels, tiles

    // Bold (TMP) labels sit ~1.5 px high in a centred line box; a 2×drop spacer above moves the cap band to the middle
    // (Maestro BoldLabelDrop, measured in-game 2026-11-06).
    private const float BoldLabelDrop = 1f;

    private static HudElement DropBold(HudElement label) => new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, 2f * BoldLabelDrop),
        label,
    }, Gap: 0.01f);

    // Hover that follows the shape: a Bare selectable (no square wash of its own) flips this flag and the surrounding
    // round Bubble's FillColorFn shows the hover.
    private sealed class HoverFlag { public bool On; }

    // ── pill buttons ──────────────────────────────────────────────────────────────────────────────────────────────
    // ONE build for every labelled button: a fill-tinted Bubble around a Bare SelectableElement, `height` tall (the struts
    // either side of a bold label). The label box is FIXED (`labelW` ≥ the longest label in any locale, house rule 10) so a
    // language switch never resizes the pill. `surface` = what the pill sits on, for the recessed label contrast.
    // `enabled` (optional): a disabled pill keeps its geometry, swallows the click and is RECESSED (rule 22).
    // `onHover` (optional): reports hover enter/leave (the OUTFITS caption hint names the hovered action).
    // `onRefused` (optional): runs instead of `onClick` when a click lands while disabled (the confirm misclick guard).
    // `hoverFill` (optional): the hovered fill; default = the house hover mix over `fill`.
    private HudElement PillButton(Func<string> label, Func<ColorRgba> fill, Func<ColorRgba> text, Action onClick,
        float labelW, float height, int fontSize, float padX, Func<ColorRgba> surface, Func<bool>? enabled = null,
        Action<bool>? onHover = null, Action? onRefused = null, Func<ColorRgba>? hoverFill = null)
    {
        var hover = new HoverFlag();
        var disabled = new DisabledLabelCache();
        bool En() => enabled == null || enabled();
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(padX, height),
                DropBold(new TextElement(label, Color: () => En() ? text() : DisabledLabel(surface(), disabled),
                    Width: labelW, Align: TextAlign.Center, NoWrap: true, FontSize: fontSize) { Bold = true, Ellipsize = true }),
                new SpacerElement(padX, height),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (En()) onClick(); else onRefused?.Invoke(); })
            { Bare = true, OnHover = over => { hover.On = over; onHover?.Invoke(over); } })
        {
            // The recessed fill is translucent (blends over the real surface, so it fades with the window already); the
            // raised fills are opaque and faded here.
            FillColorFn = () => !En() ? DisabledShade
                              : Fade(!hover.On ? fill() : hoverFill != null ? hoverFill() : HoverFill(fill())),
            CornerRadius = RoundRadius(height), PaddingX = 0f, PaddingY = 0f,
        };
    }

    // The header's main action (28 px, bold 13): "Save current outfit", 168 wide.
    private const float HeaderPillH = 28f;
    private const float HeaderPillW = 168f;
    private const float HeaderPillPadX = 12f;

    private HudElement PrimaryHeaderPill(Func<string> label, Action onClick)
    {
        var text = new SolidLabelCache();
        return PillButton(label, PrimaryFill, () => text.Get(PrimaryFill()), onClick,
            HeaderPillW - 2f * HeaderPillPadX, HeaderPillH, 13, HeaderPillPadX, () => Menu.MenuBackground);
    }

    // The in-row pills (22 px, bold 12): Wear (58 wide, Primary) and the confirm pair (66 wide, Danger / Caution /
    // Neutral). They sit on the raised row tile.
    private const float RowPillH = 22f;
    private const float RowPillPadX = 4f;

    private HudElement RowPill(Func<string> label, Func<ColorRgba> fill, Func<ColorRgba> text, Action onClick,
        float width, Func<bool>? enabled = null, Action<bool>? onHover = null, Action? onRefused = null,
        Func<ColorRgba>? hoverFill = null)
        => PillButton(label, fill, text, onClick, width - 2f * RowPillPadX, RowPillH, 12, RowPillPadX, TileColor,
            enabled, onHover, onRefused, hoverFill);

    private HudElement PrimaryRowPill(Func<string> label, Action onClick, float width, Func<bool> enabled,
        Action<bool> onHover)
    {
        var text = new SolidLabelCache();
        return RowPill(label, PrimaryFill, () => text.Get(PrimaryFill()), onClick, width, enabled, onHover);
    }

    private HudElement DangerRowPill(Func<string> label, Action onClick, float width, Func<bool>? enabled = null,
        Action? onRefused = null)
    {
        var text = new SolidLabelCache();
        return RowPill(label, DangerFill, () => text.Get(DangerFill()), onClick, width, enabled, null, onRefused);
    }

    private HudElement CautionRowPill(Func<string> label, Action onClick, float width, Func<bool>? enabled = null,
        Action? onRefused = null)
    {
        var text = new ContrastCache();
        return RowPill(label, CautionFill, () => text.Get(Menu.Caution, CautionFill(), PillMinContrast), onClick, width,
            enabled, null, onRefused);
    }

    private HudElement NeutralRowPill(Func<string> label, Action onClick, float width)
    {
        var text = new ContrastCache();
        return RowPill(label, NeutralFill, () => text.Get(TextColor(), NeutralFill(), PillMinContrast), onClick, width);
    }

    // The green "Worn" pill: bold Success text on a 22% wash of it (the old status-chip look, same 58 × 22), but
    // PRESSABLE (rule 22): the wash is its raised fill, hover deepens the wash inside the round shape (R2), and it
    // recesses like Wear while disabled. The click re-applies the worn outfit (the caller passes Wear's handler).
    private const float SuccessHoverWashAlpha = 0.38f;

    private HudElement SuccessRowPill(Func<string> label, Action onClick, float width, Func<bool> enabled,
        Action<bool> onHover)
        => RowPill(label, () => Over(SuccessColor() with { A = ChipWashAlpha }, TileColor()), SuccessColor, onClick, width,
            enabled, onHover, hoverFill: () => Over(SuccessColor() with { A = SuccessHoverWashAlpha }, TileColor()));
}
