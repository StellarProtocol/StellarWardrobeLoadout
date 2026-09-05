using System.Collections.Generic;
using Stellar.WardrobeLoadout;
using Xunit;

namespace Stellar.WardrobeLoadout.Tests;

/// <summary>Behavioural pins for the two decisions the overlay/apply path delegates to
/// <see cref="WardrobeRules"/>. Both originate in the Discord "Wardrobe Plugin enhancements" thread
/// (2026-09-03/04).</summary>
public sealed class WardrobeRulesTests
{
    private static OutfitSlot WithSkin(int professionId, int skinId) => new()
    {
        Name = "Midnight",
        Regions = new Dictionary<int, int> { [701] = 55 },
        WeaponProfessionId = professionId,
        WeaponSkinId = skinId,
    };

    private static OutfitSlot NoSkin() => new()
    {
        Name = "Legacy",
        Regions = new Dictionary<int, int> { [701] = 55 },
    };

    // Origin: Discord "Wardrobe Plugin enhancements" thread, 2026-09-03/04 — NEVER send the weapon skin
    // after a failed outfit apply. The two systems share the game's single apply-in-flight slot, so a
    // rejected outfit switch (combat lock, ownership) must leave the player wearing exactly what they had:
    // swapping their weapon look while the outfit stayed put would be a visible half-applied outfit. The
    // second axis is the 1.0.0 rollback case — an outfit with no stored skin sends nothing either way.
    [Theory]
    [InlineData(true,  true,  true)]    // outfit applied + skin stored  → send it
    [InlineData(false, true,  false)]   // outfit FAILED  + skin stored  → send NOTHING
    [InlineData(true,  false, false)]   // outfit applied + no skin      → leave the worn skin alone
    [InlineData(false, false, false)]   // outfit FAILED  + no skin      → nothing to do
    public void Weapon_skin_is_sent_only_after_a_successful_outfit_apply(
        bool outfitSucceeded, bool hasSkin, bool expected)
    {
        var slot = hasSkin ? WithSkin(9, 4021) : NoSkin();
        Assert.Equal(expected, WardrobeRules.ShouldSendWeaponSkin(outfitSucceeded, slot));
    }

    // A stored skinId of 0 is a REAL value (that class's default weapon look, which the game restores);
    // only professionId 0 means "this outfit carries no skin". Pinned separately because the two zeros
    // read alike at a glance.
    [Fact]
    public void Weapon_skin_is_sent_for_a_stored_default_look()
        => Assert.True(WardrobeRules.ShouldSendWeaponSkin(true, WithSkin(5, 0)));

    // Origin: Discord 2026-09-04, "can't scroll past the 48th outfit". The fix replaced the eager 48-row
    // pool with a VirtualListElement, which positions rows on a FIXED pitch — so the pitch has to match the
    // row's natural height exactly or rows overlap (too small) or gap (too large). 33 px at FontScale 1.0
    // was MEASURED from the UI-sandbox render (tools/ui-sandbox, story wardrobeloadout-window-ugui: the two
    // selected rows' tint bands sit at y=227..257 and y=293..323 — 31 px tall, 66 px apart = 2 × 33).
    // The endpoints cover the theme font-scale slider's full 0.8..1.4 range; the rounding is the framework's
    // own (Math.Round = to-even, matching Mathf.RoundToInt in WindowBuilder.Scaled), so 11 × 1.4 = 15.4 → 15.
    [Theory]
    [InlineData(1.0f, 33f)]    // 11      → 11 + 12 + 8 + 2
    [InlineData(0.8f, 31f)]    // 8.8     → 9  + 12 + 8 + 2   (slider minimum)
    [InlineData(1.4f, 37f)]    // 15.4    → 15 + 12 + 8 + 2   (slider maximum, rounds DOWN)
    public void RowHeightFor_matches_the_measured_row_pitch(float fontScale, float expected)
        => Assert.Equal(expected, WardrobeRules.RowHeightFor(fontScale));
}
