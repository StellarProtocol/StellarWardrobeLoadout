using System;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// The plugin's two behavioural decisions, extracted as pure functions so they can be pinned by unit
/// tests. Pure BCL — no framework types — because the test project compiles these sources directly
/// (there is no Unity/IL2CPP surface in a test run).
/// </summary>
public static class WardrobeRules
{
    /// <summary>Whether an outfit's saved weapon skin should be sent after the outfit switch resolved.
    /// True only when the outfit apply SUCCEEDED and the outfit actually carries a skin: a failed outfit
    /// apply must never be followed by a weapon-skin RPC (the player keeps the look they had), and an
    /// outfit saved before 1.1.0 carries no skin, so applying it leaves the worn skin alone.</summary>
    /// <param name="outfitSucceeded">True when the outfit switch returned success.</param>
    /// <param name="slot">The outfit being applied.</param>
    public static bool ShouldSendWeaponSkin(bool outfitSucceeded, OutfitSlot slot)
        => outfitSucceeded && slot.HasWeaponSkin();

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
