using System.Collections.Generic;
using System.Text.Json;
using Stellar.WardrobeLoadout;
using Xunit;

namespace Stellar.WardrobeLoadout.Tests;

public sealed class WardrobeStoreTests
{
    private static OutfitSlot Slot(string name, params (int region, int id)[] pieces)
    {
        var regions = new Dictionary<int, int>();
        foreach (var (region, id) in pieces) regions[region] = id;
        return new OutfitSlot { Name = name, Regions = regions, SavedAtMs = 1000 };
    }

    [Fact]
    public void Add_then_Get_returns_the_slot_with_region_values()
    {
        var store = new WardrobeStore();
        store.Add("Aria", Slot("Midnight", (701, 55), (702, 0)));

        var slots = store.Get("Aria");
        Assert.Single(slots);
        Assert.Equal("Midnight", slots[0].Name);
        Assert.Equal(55, slots[0].Regions[701]);
        Assert.Equal(0, slots[0].Regions[702]);
    }

    [Fact]
    public void Get_is_empty_for_an_unknown_character()
    {
        var store = new WardrobeStore();
        Assert.Empty(store.Get("Nobody"));
    }

    [Fact]
    public void Characters_are_isolated()
    {
        var store = new WardrobeStore();
        store.Add("Aria", Slot("A"));
        store.Add("Bram", Slot("B1"));
        store.Add("Bram", Slot("B2"));

        Assert.Single(store.Get("Aria"));
        Assert.Equal(2, store.Get("Bram").Count);
        Assert.Equal("A", store.Get("Aria")[0].Name);
    }

    [Fact]
    public void Delete_removes_by_index()
    {
        var store = new WardrobeStore();
        store.Add("Aria", Slot("first"));
        store.Add("Aria", Slot("second"));

        Assert.True(store.Delete("Aria", 0));
        Assert.Single(store.Get("Aria"));
        Assert.Equal("second", store.Get("Aria")[0].Name);
        Assert.False(store.Delete("Aria", 5));   // out of range
    }

    [Fact]
    public void Rename_changes_the_name()
    {
        var store = new WardrobeStore();
        store.Add("Aria", Slot("old"));
        Assert.True(store.Rename("Aria", 0, "new"));
        Assert.Equal("new", store.Get("Aria")[0].Name);
        Assert.False(store.Rename("Aria", 9, "x"));
    }

    [Fact]
    public void MoveToTop_reorders_to_slot_one()
    {
        var store = new WardrobeStore();
        store.Add("Aria", Slot("a"));
        store.Add("Aria", Slot("b"));
        store.Add("Aria", Slot("c"));

        Assert.True(store.MoveToTop("Aria", 2));   // move "c" to front
        Assert.Equal(new[] { "c", "a", "b" }, Names(store.Get("Aria")));
        Assert.False(store.MoveToTop("Aria", 0));   // already first
    }

    [Fact]
    public void Holds_an_unlimited_number_of_outfits()
    {
        var store = new WardrobeStore();
        for (var i = 0; i < 25; i++) store.Add("Aria", Slot($"o{i}"));
        Assert.Equal(25, store.Get("Aria").Count);
    }

    [Fact]
    public void Root_round_trips_through_system_text_json()
    {
        var store = new WardrobeStore();
        store.Add("Aria", Slot("Midnight", (701, 55), (711, 12), (723, 0)));
        store.Add("Bram", Slot("Casual", (702, 88)));

        var json = JsonSerializer.Serialize(store.Root);
        var root = JsonSerializer.Deserialize<Dictionary<string, List<OutfitSlot>>>(json);
        var reloaded = new WardrobeStore(root);

        Assert.Equal(55, reloaded.Get("Aria")[0].Regions[701]);
        Assert.Equal(12, reloaded.Get("Aria")[0].Regions[711]);
        Assert.Equal(0, reloaded.Get("Aria")[0].Regions[723]);
        Assert.Equal("Casual", reloaded.Get("Bram")[0].Name);
    }

    private static string[] Names(System.Collections.Generic.IReadOnlyList<OutfitSlot> slots)
    {
        var names = new string[slots.Count];
        for (var i = 0; i < slots.Count; i++) names[i] = slots[i].Name;
        return names;
    }
}
