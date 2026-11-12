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
        // No exact match → the stateless outfit beats a saved state that differs from the current one.
        Assert.Same(legacy, Pick(rows, AllShown()));
    }

    [Fact]
    public void Last_applied_breaks_a_tie_then_list_order()
    {
        var a = Slot("A", NakedHidden());
        var b = Slot("B", NakedHidden());
        var rows = new List<OutfitSlot> { a, b };

        Assert.Same(a, Pick(rows, NakedHidden()));
        Assert.Same(b, Pick(rows, NakedHidden(), lastApplied: b));
        // Neither matches the current state (a hat toggled by hand since) → still the last applied one.
        Assert.Same(b, Pick(rows, AllShown(), lastApplied: b));
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

        Assert.Same(vampy, Pick(rows, NakedHidden(), lastApplied: other));
        Assert.Null(Pick(new List<OutfitSlot> { other }, NakedHidden()));
        Assert.Equal(1, WardrobeRules.CountWornCandidates(rows, Pieces, WardrobeRules.CountNonZero(Pieces)));
    }
}
