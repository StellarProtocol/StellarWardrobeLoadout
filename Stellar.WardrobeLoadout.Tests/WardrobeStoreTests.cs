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
    public void Move_reorders_up_and_down()
    {
        var store = new WardrobeStore();
        store.Add("Aria", Slot("a"));
        store.Add("Aria", Slot("b"));
        store.Add("Aria", Slot("c"));

        Assert.True(store.Move("Aria", 2, -1));    // "c" up one
        Assert.Equal(new[] { "a", "c", "b" }, Names(store.Get("Aria")));
        Assert.True(store.Move("Aria", 0, +1));    // "a" down one
        Assert.Equal(new[] { "c", "a", "b" }, Names(store.Get("Aria")));
        Assert.False(store.Move("Aria", 0, -1));   // already first — no-op
        Assert.False(store.Move("Aria", 2, +1));   // already last — no-op
        Assert.False(store.Move("Aria", 9, -1));   // out of range
    }

    [Fact]
    public void Update_recaptures_in_place_keeping_name_and_position()
    {
        var store = new WardrobeStore();
        store.Add("Aria", Slot("keep-me", (701, 55)));
        store.Add("Aria", Slot("other", (702, 10)));
        store.Get("Aria")[0].Dyes[701] = new[] { 0.1f, 0.2f, 0.3f };   // legacy flat present

        var captured = new OutfitSlot
        {
            Name = "ignored — the captured name never overwrites the stored one",
            Regions = new Dictionary<int, int> { [701] = 99, [703] = 77 },
            DyeAreas = new Dictionary<int, Dictionary<int, float[]>>
            {
                [701] = new() { [3] = new[] { 0.5f, 0.25f, 0.75f } },
            },
            WeaponProfessionId = 9,
            WeaponSkinId = 4021,
            SavedAtMs = 2000,
        };
        Assert.True(store.Update("Aria", 0, captured));

        var slot = store.Get("Aria")[0];
        Assert.Equal("keep-me", slot.Name);                 // name preserved
        Assert.Equal(99, slot.Regions[701]);                // outfit overwritten
        Assert.Equal(77, slot.Regions[703]);
        Assert.Equal(2000, slot.SavedAtMs);
        Assert.Equal(0.25f, slot.DyeAreas[701][3][1], 5);   // per-area dyes stored
        Assert.Empty(slot.Dyes);                            // legacy flat cleared
        // The re-capture carries the weapon skin too (Discord "Wardrobe Plugin enhancements" thread,
        // 2026-09-03/04): updating a slot must refresh its skin, not leave the outfit's old one behind.
        Assert.Equal(9, slot.WeaponProfessionId);
        Assert.Equal(4021, slot.WeaponSkinId);
        Assert.True(slot.HasWeaponSkin());
        Assert.Equal("other", store.Get("Aria")[1].Name);   // position preserved
        Assert.False(store.Update("Aria", 9, captured));    // out of range
    }

    // Origin: Discord "Wardrobe Plugin enhancements" thread, 2026-09-03/04 — outfits saved by 1.0.0 carry
    // no weapon fields at all. Reading one must mean "this outfit carries no weapon skin" (applying it
    // leaves the player's skin alone), NOT "reset the weapon skin to the class default" (which 0/0 with a
    // non-zero profession would mean). Rollback-safety pin: a newer config must never lose an older one's
    // outfit data either, so the regions are asserted intact.
    [Fact]
    public void Legacy_json_without_weapon_fields_reads_as_no_weapon_skin()
    {
        const string legacy = """
        {
          "Aria": [
            {
              "Name": "Midnight",
              "Regions": { "701": 55, "711": 12 },
              "Dyes": {},
              "DyeAreas": {},
              "SavedAtMs": 1700000000000
            }
          ]
        }
        """;

        var root = JsonSerializer.Deserialize<Dictionary<string, List<OutfitSlot>>>(legacy);
        var slot = new WardrobeStore(root).Get("Aria")[0];

        Assert.Equal("Midnight", slot.Name);
        Assert.Equal(55, slot.Regions[701]);              // 1.0.0 outfit data intact
        Assert.Equal(12, slot.Regions[711]);
        Assert.Equal(1700000000000, slot.SavedAtMs);
        Assert.Equal(0, slot.WeaponProfessionId);         // absent field → "no weapon skin stored"
        Assert.Equal(0, slot.WeaponSkinId);
        Assert.False(slot.HasWeaponSkin());               // → apply leaves the worn skin alone
    }

    // Origin: Discord "Wardrobe Plugin enhancements" thread, 2026-09-03/04 — the saved weapon skin has to
    // survive the config round-trip, including skinId 0 (a real value: that class's DEFAULT weapon look,
    // which the game restores) as distinct from professionId 0 (= no skin stored at all).
    [Fact]
    public void Weapon_skin_round_trips_through_json()
    {
        var store = new WardrobeStore();
        var withSkin = Slot("Midnight", (701, 55));
        withSkin.WeaponProfessionId = 9;
        withSkin.WeaponSkinId = 4021;
        var defaultLook = Slot("Plain", (701, 60));
        defaultLook.WeaponProfessionId = 5;
        defaultLook.WeaponSkinId = 0;                     // class default look, still a stored skin
        store.Add("Aria", withSkin);
        store.Add("Aria", defaultLook);
        store.Add("Aria", Slot("NoSkin", (701, 61)));     // nothing captured → 0/0

        var json = JsonSerializer.Serialize(store.Root);
        var reloaded = new WardrobeStore(JsonSerializer.Deserialize<Dictionary<string, List<OutfitSlot>>>(json));

        var a = reloaded.Get("Aria")[0];
        Assert.Equal(9, a.WeaponProfessionId);
        Assert.Equal(4021, a.WeaponSkinId);
        Assert.True(a.HasWeaponSkin());

        var b = reloaded.Get("Aria")[1];
        Assert.Equal(5, b.WeaponProfessionId);
        Assert.Equal(0, b.WeaponSkinId);
        Assert.True(b.HasWeaponSkin());                   // skinId 0 is a value, not an absence

        Assert.False(reloaded.Get("Aria")[2].HasWeaponSkin());
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

    [Fact]
    public void MigrateNameKeyToCharId_CopiesCurrentCharacterOutfitsOnce()
    {
        var store = new WardrobeStore();
        store.Add("HatsuneMiku", new OutfitSlot { Name = "A" });   // legacy name-keyed
        var migrated = store.MigrateNameToCharId(name: "HatsuneMiku", charId: "635404");
        Assert.True(migrated);
        Assert.Single(store.Get("635404"));                        // copied under the char-id key
        Assert.Single(store.Get("HatsuneMiku"));                   // legacy entry KEPT (rollback)
        // idempotent: a char-id entry already present → no re-copy
        Assert.False(store.MigrateNameToCharId("HatsuneMiku", "635404"));
    }
}
