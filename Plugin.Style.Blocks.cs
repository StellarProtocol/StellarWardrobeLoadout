using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

// More house-kit building blocks (Stellar Design Language §4; reference StellarMaestroPlugin Plugin.Style.Blocks.cs):
// the glyph icons, the accent icon tile, the round icon buttons, the section panel and the fixed-height line.
public sealed partial class Plugin
{
    // ── glyph icons ───────────────────────────────────────────────────────────────────────────────────────────────
    // White flat glyphs (tools/gen_icons.py → Icons/<name>-<px>.png), tinted at draw time with ImageElement.Tint so they
    // follow the theme. House icon rule: ONE pre-drawn, pixel-hinted bitmap per on-screen size, never a master scaled
    // down — so each glyph ships at its canvas size × the usual UI scales (0.75 at 1080p … 2.0), and the window shows the
    // one nearest to `units × the live canvas→screen scale` (Live images: re-picked when the scale changes, e.g. a
    // resolution switch). Bytes are read once per size and the SAME array handed back (ImageElement compares by reference).
    private sealed class IconSet
    {
        private readonly string _name;
        public readonly int Units;          // glyph height in canvas units (what the window lays out)
        private readonly int[] _sizes;      // pre-drawn pixel heights (ascending)
        private readonly byte[]?[] _pngs;
        private readonly bool[] _loaded;
        private float _scale = -1f;
        private byte[]? _picked;

        public IconSet(string name, int units, params int[] sizes)
        {
            _name = name; Units = units; _sizes = sizes;
            _pngs = new byte[]?[sizes.Length]; _loaded = new bool[sizes.Length];
        }

        // The bitmap for the current canvas→screen `scale`; cached until the scale changes (no work per poll).
        public byte[]? Pick(float scale)
        {
            if (scale == _scale) return _picked;
            _scale = scale;
            float want = Units * scale;
            int best = 0;
            for (int i = 1; i < _sizes.Length; i++)
                if (MathF.Abs(_sizes[i] - want) < MathF.Abs(_sizes[best] - want)) best = i;
            if (!_loaded[best])
            {
                _loaded[best] = true;   // latch even on a miss: a missing resource draws nothing, never re-reads
                _pngs[best] = LoadEmbedded($"Stellar.WardrobeLoadout.Icons.{_name}-{_sizes[best]}.png");
            }
            return _picked = _pngs[best] ?? _picked;
        }
    }

    // Sizes = round(units × {0.75, 1, 1.25, 1.5, 2}) ∪ {16, 32} — keep in sync with tools/gen_icons.py.
    private static readonly IconSet HangerIcon  = new("hanger", 24, 16, 18, 24, 30, 32, 36, 48);
    private static readonly IconSet PencilIcon  = new("pencil", 16, 12, 16, 20, 24, 32);
    private static readonly IconSet RefreshIcon = new("refresh", 16, 12, 16, 20, 24, 32);
    private static readonly IconSet CheckIcon   = new("check", 16, 12, 16, 20, 24, 32);
    private static readonly IconSet CloseIcon   = new("close", 14, 10, 14, 16, 18, 21, 28, 32);
    private static readonly IconSet GripIcon    = new("grip", 14, 10, 14, 16, 18, 21, 28, 32);   // 6 × 14 units
    private const int GripW = 6;

    // Canvas units → screen pixels (1.0 at 1440p, 0.75 at 1080p). Refreshed by the 10 Hz tick while the window shows.
    private float _uiScale = 1f;

    private void RefreshUiScale()
    {
        var fw = _services.Framework;
        _uiScale = fw.CanvasWidth > 1 && fw.ScreenWidth > 0 ? (float)fw.ScreenWidth / fw.CanvasWidth : 1f;
    }

    private ImageElement Glyph(IconSet icon, Func<ColorRgba?>? tint = null, int width = 0)
        => Glyph(() => icon.Pick(_uiScale), icon.Units, tint, width);

    // A glyph whose PICTURE is live too (e.g. pencil ↔ ✓ in one cell): the Func must hand back cached arrays only.
    private static ImageElement Glyph(Func<byte[]?> png, int units, Func<ColorRgba?>? tint = null, int width = 0)
        => new(png, width > 0 ? width : units, units) { Live = true, Tint = tint };

    // Launcher tile icon (clothes hanger) — the framework tints it.
    private static readonly byte[]? LauncherIcon = LoadEmbedded("Stellar.WardrobeLoadout.wardrobe-icon.png");

    private static byte[]? LoadEmbedded(string name)
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

    // ── accent icon tile (ToolHeader) ─────────────────────────────────────────────────────────────────────────────
    // A 40 px tile in the LIVE theme accent with the white hanger glyph (24 px) centred: never baked colour, so the tile
    // follows the theme (house "Icon tile", option A). Radius 9 at 40 px. The padding IS the margin → exactly 40 square.
    private const float ToolIconSize = 40f;

    private HudElement HeaderIconTile()
    {
        float pad = (ToolIconSize - HangerIcon.Units) / 2f;
        return new CellElement(new BubbleElement(
            new RowElement(new HudElement[] { Glyph(HangerIcon) }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = 9f, PaddingX = pad, PaddingY = pad,
        }, Width: ToolIconSize);
    }

    // ── round icon buttons (rename / update / delete in a row) ────────────────────────────────────────────────────
    // 24 × 24 circle (radius 11, R1) around a Bare selectable: transparent at rest, `wash` on hover (Text@0.10, or the
    // red remove wash for ✕), so the hover follows the circle (R2). Pooled rows: the hover flag remembers WHICH outfit
    // it was set on, so a row that slides under a still pointer (after a delete) doesn't inherit the hover.
    private const float IconButtonSize = 24f;

    private sealed class RowHover
    {
        private object? _item;
        private bool _on;

        public void Set(object? item, bool on) { _item = item; _on = on; }
        public bool Get(object? item) => _on && ReferenceEquals(item, _item);
    }

    private HudElement IconButton(Func<byte[]?> png, int units, Func<object?> item, RowHover hover,
        Func<ColorRgba?> tint, Func<ColorRgba> wash, Func<bool> enabled, Action onClick, Action<bool> onHover)
    {
        float pad = (IconButtonSize - units) / 2f;
        return new CellElement(new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(pad, IconButtonSize),
                Glyph(png, units, tint),
                new SpacerElement(pad, IconButtonSize),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (enabled()) onClick(); })
            { Bare = true, OnHover = over => { hover.Set(item(), over); onHover(over); } })
        {
            FillColorFn = () => enabled() && hover.Get(item()) ? Fade(wash()) : Transparent,
            CornerRadius = RoundRadius(IconButtonSize), PaddingX = 0f, PaddingY = 0f,
        }, Width: IconButtonSize);
    }

    private ColorRgba IconHoverWash()  => Over(Menu.MenuText with { A = HoverMix }, TileColor());
    private ColorRgba RemoveHoverWash() => Over(Menu.Danger with { A = ChipWashAlpha }, TileColor());

    // ── section panel ─────────────────────────────────────────────────────────────────────────────────────────────
    // A rounded panel (background darkened, live): small bold UPPERCASE caption + a right-aligned hint slot whose text
    // AND colour are live (the OUTFITS hint names the hovered action in the text colour — no tooltips exist, R12), then
    // the children. The hint sits in a weighted cell so a long translation ellipsizes instead of widening the panel.
    // `trailing` (optional) = a control at the right end of the caption row (the PREVIEW Weapon chip).
    // The caption row is pinned to CaptionRowHeight in EVERY panel (a 0-width strut, FixedLine): the row with the 24 px
    // chip and the text-only row are then exactly as tall, so the two side-by-side panels stay the same height and
    // their contents start on the same line.
    private const float CaptionRowHeight = 24f;   // = the Weapon chip height (Plugin.Overlay.Weapon.cs)

    private HudElement SectionPanel(Func<string> caption, Func<string> hint, Func<ColorRgba> hintColor,
        params HudElement[] children) => CaptionedPanel(caption, hint, hintColor, null, children);

    private HudElement CaptionedPanel(Func<string> caption, Func<string> hint, Func<ColorRgba> hintColor,
        HudElement? trailing, HudElement[] children)
    {
        var captionRow = new HudElement[trailing is null ? 2 : 3];
        captionRow[0] = new TextElement(Upper(caption), Color: () => MutedColor(), NoWrap: true, FontSize: 12) { Bold = true };
        captionRow[1] = new CellElement(new TextElement(hint, Color: () => hintColor(), Align: TextAlign.Right, NoWrap: true)
            { Ellipsize = true }, Weight: 1f);
        if (trailing is not null) captionRow[2] = trailing;
        var items = new HudElement[children.Length + 1];
        items[0] = FixedLine(CaptionRowHeight, new RowElement(captionRow, Gap: 6f));
        Array.Copy(children, 0, items, 1, children.Length);
        return new BubbleElement(new ColumnElement(items, Gap: 6f))
        {
            FillColorFn = () => Fade(PanelColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        };
    }

    // Upper-cases a localized caption once per language (the source string is a cached catalog entry, so a reference
    // compare spots a language switch) instead of allocating a new string on every poll.
    private static Func<string> Upper(Func<string> src)
    {
        string? last = null, upper = "";
        return () =>
        {
            var s = src();
            if (!ReferenceEquals(s, last)) { last = s; upper = s.ToUpperInvariant(); }
            return upper;
        };
    }

    // ONE always-present line at a fixed height: a 0-width strut pins the row so bold / empty / feedback text never
    // changes the window height; the content cell ellipsizes.
    private static HudElement FixedLine(float height, HudElement content) => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, height),
        new CellElement(content, Weight: 1f),
    }, Gap: 0.01f);
}
