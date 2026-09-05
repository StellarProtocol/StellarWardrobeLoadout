using System;
using System.Collections.Generic;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// The plugin's behavioural decisions, extracted as pure functions so they can be pinned by unit
/// tests. Pure BCL — no framework types — because the test project compiles these sources directly
/// (there is no Unity/IL2CPP surface in a test run).
/// </summary>
public static class WardrobeRules
{
    // Mirrors Stellar.Abstractions' WardrobeRegions.WeaponSkinPreview. Repeated as a literal so this
    // file stays pure BCL for the test project; the plugin itself passes the framework constant around.
    private const int WeaponSkinPreviewRegion = 731;

    /// <summary>Whether an outfit's saved weapon skin should be sent after the outfit switch resolved.
    /// True only when the outfit apply SUCCEEDED and the outfit actually carries a skin: a failed outfit
    /// apply must never be followed by a weapon-skin RPC (the player keeps the look they had), and an
    /// outfit saved before 1.1.0 carries no skin, so applying it leaves the worn skin alone.</summary>
    /// <param name="outfitSucceeded">True when the outfit switch returned success.</param>
    /// <param name="slot">The outfit being applied.</param>
    public static bool ShouldSendWeaponSkin(bool outfitSucceeded, OutfitSlot slot)
        => outfitSucceeded && slot.HasWeaponSkin();

    /// <summary>The outfit map to hand <c>IWardrobePreview.Show</c>: the slot's regions, plus its weapon skin
    /// under the preview-only key 731 when that skin belongs to the class the player is CURRENTLY on.
    /// <para>Owner rule, 2026-09-05: <i>"The 3D preview does not show the weapon skin. &lt;-- it should show if
    /// class match"</i>. Weapon skins are per-class (every <c>WeaponSkinTable</c> row is <c>ProfessionId</c>-scoped
    /// and the id encodes it), so another class's skin cannot render on this model — the key is then omitted and
    /// the weapon is left exactly as the model shows it, which is what the game's own weapon-skin tab does for a
    /// non-current class. A stored skin id of 0 means "the class's default look" and is passed through as 0; the
    /// framework resolves it the same way applying the outfit would, so the preview cannot disagree with the
    /// result. An unknown current class (0 — not in world) also omits the key.</para></summary>
    /// <param name="slot">The outfit being previewed.</param>
    /// <param name="currentProfessionId">The class the player is on right now, or 0 when unknown.</param>
    public static Dictionary<int, int> PreviewOutfit(OutfitSlot slot, int currentProfessionId)
    {
        var outfit = new Dictionary<int, int>(slot.Regions);
        if (currentProfessionId > 0 && slot.HasWeaponSkin() && slot.WeaponProfessionId == currentProfessionId)
            outfit[WeaponSkinPreviewRegion] = slot.WeaponSkinId;
        return outfit;
    }

    /// <summary>The uniform pooled-row height the overlay's virtual list positions rows on, for a given
    /// theme <paramref name="fontScale"/>. The tallest thing in a row is an icon chip, whose height is the
    /// framework's button floor <c>Scaled(11) + 12</c> (the same <c>Round()</c> the framework's
    /// <c>WindowBuilder.Scaled</c> applies); the <c>SelectableElement</c> wrapper adds its 4 + 4 vertical
    /// padding, and 2 px reproduces the inter-row gap the previous eager list had. 33 px at scale 1.0;
    /// the theme slider's 0.8..1.4 range gives 31..37.</summary>
    /// <param name="fontScale">The theme's font scale (slider range 0.8..1.4).</param>
    public static float RowHeightFor(float fontScale)
        => (float)Math.Round(11 * fontScale) + 12f + 8f + 2f;
}
