using System.Collections.Generic;
using Stellar.WardrobeLoadout;
using Xunit;

namespace Stellar.WardrobeLoadout.Tests;

/// <summary>Pins <see cref="WardrobeRules.PickWorn"/> — which saved outfit the window marks Worn when several share the
/// same pieces. Origin: user report 2026-11-13, "Vampy" and "Naked" hold identical pieces and differ only in show/hide;
/// wearing Naked showed Worn = Vampy because the first piece match won.</summary>
public sealed class WornPickTests
{
    private static readonly Dictionary<int, int> Pieces = new() { [701] = 55, [713] = 77, [717] = 99 };

    private static Dictionary<int, bool> AllShown()
    {
        var map = new Dictionary<int, bool>();
        foreach (var r in WardrobeRules.HideableRegions) map[r] = false;
        return map;
    }

    private static Dictionary<int, bool> NakedHidden()
    {
        var map = AllShown();
        map[701] = true;
        map[713] = true;
        map[717] = true;
        return map;
    }

    private static OutfitSlot Slot(string name, Dictionary<int, bool>? hidden, Dictionary<int, int>? regions = null)
        => new() { Name = name, Regions = new Dictionary<int, int>(regions ?? Pieces), Hidden = hidden };

    private static OutfitSlot? Pick(IReadOnlyList<OutfitSlot> rows, IReadOnlyDictionary<int, bool>? current,
        OutfitSlot? lastApplied = null)
        => WardrobeRules.PickWorn(rows, Pieces, WardrobeRules.CountNonZero(Pieces), current, lastApplied);

    [Fact]
    public void Same_pieces_pick_the_outfit_whose_show_hide_matches_the_current_state()
    {
        var vampy = Slot("Vampy", AllShown());
        var naked = Slot("Naked", NakedHidden());
        var rows = new List<OutfitSlot> { vampy, naked };

        Assert.Same(naked, Pick(rows, NakedHidden()));
        Assert.Same(vampy, Pick(rows, AllShown()));
    }

    [Fact]
    public void Missing_regions_count_as_shown_and_only_hideable_regions_are_compared()
    {
        var naked = Slot("Naked", new Dictionary<int, bool> { [701] = true, [713] = true, [717] = true, [731] = true });
        var vampy = Slot("Vampy", new Dictionary<int, bool>());   // nothing listed = everything shown
        var rows = new List<OutfitSlot> { vampy, naked };

        Assert.Same(naked, Pick(rows, NakedHidden()));
        Assert.Same(vampy, Pick(rows, AllShown()));
    }

    [Fact]
    public void Null_hidden_matches_on_pieces_but_ranks_below_an_exact_show_hide_match()
    {
        var legacy = Slot("Legacy", null);
        var naked = Slot("Naked", NakedHidden());
        var rows = new List<OutfitSlot> { legacy, naked };

        Assert.Same(naked, Pick(rows, NakedHidden()));
        // No exact match → the stateless outfit (can't be checked) is Worn; Naked's differing state rules it out.
        Assert.Same(legacy, Pick(rows, AllShown()));
    }

    [Fact]
    public void Null_hidden_is_still_worn_on_pieces_alone()
    {
        var legacy = Slot("Legacy", null);
        var rows = new List<OutfitSlot> { legacy };

        Assert.Same(legacy, Pick(rows, NakedHidden()));
        Assert.Same(legacy, Pick(rows, AllShown()));
        Assert.Same(legacy, Pick(rows, current: null));
        Assert.False(WardrobeRules.NeedsHiddenState(rows, Pieces, WardrobeRules.CountNonZero(Pieces)));
    }

    // Owner rule: wear a slot, then change show/hide in game → that slot is a modified look, not Worn.
    [Fact]
    public void Pieces_match_but_show_hide_changed_means_no_worn()
    {
        var naked = Slot("Naked", NakedHidden());
        var rows = new List<OutfitSlot> { naked };

        Assert.Null(Pick(rows, AllShown(), lastApplied: naked));
        var changed = NakedHidden();
        changed[713] = false;   // the hat shown again by hand
        Assert.Null(Pick(rows, changed, lastApplied: naked));
        Assert.True(WardrobeRules.NeedsHiddenState(rows, Pieces, WardrobeRules.CountNonZero(Pieces)));
    }

    [Fact]
    public void Every_candidate_has_a_differing_saved_state_means_no_worn()
    {
        var vampy = Slot("Vampy", AllShown());
        var naked = Slot("Naked", NakedHidden());
        var half = AllShown();
        half[713] = true;   // only the hat hidden — neither saved state

        Assert.Null(Pick(new List<OutfitSlot> { vampy, naked }, half, lastApplied: naked));
    }

    [Fact]
    public void Pieces_changed_so_nothing_matches_means_no_worn()
    {
        var naked = Slot("Naked", NakedHidden(), new Dictionary<int, int> { [701] = 1, [713] = 77, [717] = 99 });
        var rows = new List<OutfitSlot> { naked };

        Assert.Null(Pick(rows, NakedHidden(), lastApplied: naked));
        Assert.Null(Pick(rows, current: null, lastApplied: naked));
    }

    // Worn is about which clothes are on and shown: a re-dye or another weapon skin keeps the outfit marked.
    [Fact]
    public void Dyes_and_weapon_skin_are_not_compared()
    {
        var naked = Slot("Naked", NakedHidden());
        naked.WeaponProfessionId = 9;
        naked.WeaponSkinId = 4021;
        naked.DyeAreas[701] = new Dictionary<int, float[]> { [1] = new[] { 1f, 0f, 0f } };

        Assert.Same(naked, Pick(new List<OutfitSlot> { naked }, NakedHidden()));
    }

    [Fact]
    public void Last_applied_breaks_a_tie_then_list_order()
    {
        var a = Slot("A", NakedHidden());
        var b = Slot("B", NakedHidden());
        var rows = new List<OutfitSlot> { a, b };

        Assert.Same(a, Pick(rows, NakedHidden()));
        Assert.Same(b, Pick(rows, NakedHidden(), lastApplied: b));
        // Stateless outfits tie too.
        var c = Slot("C", null);
        var d = Slot("D", null);
        Assert.Same(d, Pick(new List<OutfitSlot> { c, d }, AllShown(), lastApplied: d));
    }

    [Fact]
    public void Last_applied_never_beats_a_better_show_hide_match()
    {
        var vampy = Slot("Vampy", AllShown());
        var naked = Slot("Naked", NakedHidden());
        var rows = new List<OutfitSlot> { vampy, naked };

        Assert.Same(naked, Pick(rows, NakedHidden(), lastApplied: vampy));
    }

    [Fact]
    public void Unreadable_state_falls_back_to_pieces_then_last_applied_then_list_order()
    {
        var vampy = Slot("Vampy", AllShown());
        var naked = Slot("Naked", NakedHidden());
        var rows = new List<OutfitSlot> { vampy, naked };

        Assert.Same(vampy, Pick(rows, current: null));
        Assert.Same(naked, Pick(rows, current: null, lastApplied: naked));
    }

    [Fact]
    public void Pieces_must_still_match()
    {
        var other = Slot("Other", NakedHidden(), new Dictionary<int, int> { [701] = 1 });
        var vampy = Slot("Vampy", AllShown());
        var rows = new List<OutfitSlot> { other, vampy };

        Assert.Same(vampy, Pick(rows, AllShown(), lastApplied: other));
        Assert.Null(Pick(new List<OutfitSlot> { other }, NakedHidden()));
        Assert.False(WardrobeRules.NeedsHiddenState(new List<OutfitSlot> { other }, Pieces,
            WardrobeRules.CountNonZero(Pieces)));
    }
}
