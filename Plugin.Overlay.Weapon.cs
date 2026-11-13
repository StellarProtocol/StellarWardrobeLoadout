using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// Overlay partial: the PREVIEW panel's Weapon chip (amendment approved 2026-11-13) — draws or hides the weapon on the
/// preview model. "Weapon: On" shows the outfit's saved weapon skin when it carries one for the current class, else the
/// weapon the player has on now (the framework's rule). Default ON; a plugin SETTING (config section "wardrobe"), not
/// outfit data.
/// </summary>
public sealed partial class Plugin
{
    private const string PreviewWeaponKey = "previewWeapon";

    // House "clickable on/off chip" (KB Stellar-Design-Language.md), 120 × 24 here: the label box is the chip minus the
    // padding either side. ONE width for both states so nothing jumps on a toggle. Sized from the GAME font, not a
    // Windows-font measurement: at 84 px "Weapon: Off" (~70 px in Arial) cut to "Weapon:…" in game. Budget bold 12 at
    // ≈7.5 px/char Latin, 12.5 CJK, 8 Thai; the longest label is id "Senjata: Off" (12 × 7.5 = 90) + 2 × 10 padding
    // = 110, + ~10 spare = 120. The PREVIEW caption (~280 px, "PREVIEW" ≈ 70) takes up to ~150.
    private const float WeaponChipW = 120f;
    private const float WeaponChipH = 24f;
    private const float WeaponChipPadX = 10f;
    // Hover = the wash deepened inside the round shape (R2), like the Worn pill; the off chip's rest wash = a faint
    // muted pill so it still reads as pressable (rule 22: pressable = raised fill).
    private const float WeaponChipOffWash = 0.15f;
    private const float WeaponChipHoverAdd = 0.16f;

    // Loaded lazily on first use (InitOverlay runs from the ctor, after _cfg is set): read once, then the field is the
    // truth; the config is written only on a click.
    private bool _previewWeapon = true;
    private bool _previewWeaponLoaded;
    private bool _weaponChipHover;

    private void LoadPreviewWeaponSetting()
    {
        if (_previewWeaponLoaded) return;
        _previewWeaponLoaded = true;
        try { _previewWeapon = _cfg.Get(PreviewWeaponKey, true); } catch { _previewWeapon = true; }
    }

    // Click: flip, persist, and push it to the live preview. The framework applies ShowWeapon to the SHOWN model at
    // once (it re-asserts its weapon-renderer exclusion set — no model rebuild), so no Show call is needed; a new Show
    // here would rebuild the model and drop it for a few frames. The next Show re-asserts the value anyway.
    private void OnToggleWeapon()
    {
        _previewWeapon = !_previewWeapon;
        try { _cfg.Set(PreviewWeaponKey, _previewWeapon); _cfg.Save(); }
        catch (Exception ex) { _services.Log.Warning($"[WardrobeLoadout] saving the preview weapon setting failed: {ex.Message}"); }
        try { _services.WardrobePreview.ShowWeapon = _previewWeapon; } catch { /* never throw from a click */ }
        Repaint();
    }

    // ONE fixed-width cell → a Conditional swapping two COMPLETE chips (never a Conditional inside a bubble's measured
    // child — the re-hug gotcha), both sharing one hover flag, so the chip is 120 × 24 in both states and nothing moves
    // on a toggle. On = bold Success on its 22% wash; Off = bold muted on a faint muted wash. Both labels are bold 12:
    // the plain (non-bold) path is a fixed 14 px on Menu windows, and "Weapon: Off" at 14 px would size unlike the bold On label.
    private HudElement WeaponChip()
    {
        var hover = new HoverFlag();
        float Wash(float a) => a + (hover.On ? WeaponChipHoverAdd : 0f);
        return new CellElement(new ConditionalElement(() => _previewWeapon,
            Then: WeaponChipShell(() => _loc.T("wardrobe.preview.weaponOn"), SuccessColor,
                () => Over(SuccessColor() with { A = Wash(ChipWashAlpha) }, PanelColor()), hover),
            Else: WeaponChipShell(() => _loc.T("wardrobe.preview.weaponOff"), MutedColor,
                () => Over(MutedColor() with { A = Wash(WeaponChipOffWash) }, PanelColor()), hover)),
            Width: WeaponChipW);
    }

    private HudElement WeaponChipShell(Func<string> label, Func<ColorRgba> text, Func<ColorRgba> fill, HoverFlag hover)
        => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(WeaponChipPadX, WeaponChipH),   // padding + the 24 px height strut
                DropBold(new TextElement(label, Color: () => text(), Width: WeaponChipW - 2f * WeaponChipPadX,
                    Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true, Ellipsize = true }),
                new SpacerElement(WeaponChipPadX, WeaponChipH),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: OnToggleWeapon)
            { Bare = true, OnHover = over => { hover.On = over; SetWeaponChipHover(over); } })
        {
            // MaxWidth pins the width: a MaxWidth-0 bubble under a Conditional caps itself at the 100 px default
            // (KB WindowBuilder-Patterns, Dungeon Confirm 6b19310).
            FillColorFn = () => Fade(fill()), CornerRadius = RoundRadius(WeaponChipH), PaddingX = 0f, PaddingY = 0f,
            MaxWidth = WeaponChipW,
        };

    // Hovering the chip names what it does in the footer (no tooltips exist, R12); leaving restores the footer.
    private void SetWeaponChipHover(bool over)
    {
        if (_weaponChipHover == over) return;
        _weaponChipHover = over;
        Repaint();
    }
}
