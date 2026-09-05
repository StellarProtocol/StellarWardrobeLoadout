using System.Collections.Generic;
using System.Text;
using Xunit;

namespace Stellar.WardrobeLoadout.Tests;

/// <summary>
/// Pins the outfit storage move from the shared plugin config to the plugin's own data store
/// (<c>outfits.json</c>) — owner ruling 2026-09-05 ("each plugin can create their own plugindata …
/// user old config should be migrated there"). The migration tests are named for that ruling; they
/// exist to keep the rollback contract (config copy left frozen, plugindata always wins) from being
/// silently re-decided later.
/// </summary>
public sealed class OutfitPersistenceTests
{
    private static Dictionary<string, List<OutfitSlot>> Root(params (string chr, int outfits)[] chars)
    {
        var root = new Dictionary<string, List<OutfitSlot>>();
        foreach (var (chr, outfits) in chars)
        {
            var list = new List<OutfitSlot>();
            for (var i = 0; i < outfits; i++) list.Add(new OutfitSlot { Name = $"{chr}-{i}" });
            root[chr] = list;
        }
        return root;
    }

    // ---- serialize / deserialize -------------------------------------------------------------

    [Fact]
    public void Round_trip_preserves_regions_dyes_dyeareas_weapon_and_timestamp()
    {
        var slot = new OutfitSlot
        {
            Name = "Midnight Requiem",
            Regions = new Dictionary<int, int> { [701] = 7010034, [702] = 0, [723] = 7230042 },
            Dyes = new Dictionary<int, float[]> { [711] = new[] { 0.5f, 0.25f, 0.125f } },
            DyeAreas = new Dictionary<int, Dictionary<int, float[]>>
            {
                [701] = new() { [3] = new[] { 0.30695337f, 0.1968f, 0.41f }, [1] = new[] { 0.1f, 0.1f, 0.1f } },
            },
            WeaponProfessionId = 5,
            WeaponSkinId = 5001234,
            SavedAtMs = 1787600000000L,
        };
        var root = new Dictionary<string, List<OutfitSlot>> { ["Revette"] = new() { slot } };

        var decoded = OutfitPersistence.Deserialize(OutfitPersistence.Serialize(root), out var corrupt);

        Assert.False(corrupt);
        var got = Assert.Single(decoded["Revette"]);
        Assert.Equal("Midnight Requiem", got.Name);
        Assert.Equal(7010034, got.Regions[701]);
        Assert.Equal(0, got.Regions[702]);
        Assert.Equal(7230042, got.Regions[723]);
        Assert.Equal(new[] { 0.5f, 0.25f, 0.125f }, got.Dyes[711]);
        Assert.Equal(new[] { 0.30695337f, 0.1968f, 0.41f }, got.DyeAreas[701][3]);
        Assert.Equal(new[] { 0.1f, 0.1f, 0.1f }, got.DyeAreas[701][1]);
        Assert.Equal(5, got.WeaponProfessionId);
        Assert.Equal(5001234, got.WeaponSkinId);
        Assert.Equal(1787600000000L, got.SavedAtMs);
    }

    [Fact]
    public void Round_trip_preserves_character_scoping_and_slot_order()
    {
        var root = Root(("Revette", 3), ("Ribery", 1));

        var decoded = OutfitPersistence.Deserialize(OutfitPersistence.Serialize(root), out _);

        Assert.Equal(3, decoded["Revette"].Count);
        Assert.Equal(new[] { "Revette-0", "Revette-1", "Revette-2" }, decoded["Revette"].ConvertAll(s => s.Name));
        Assert.Equal("Ribery-0", Assert.Single(decoded["Ribery"]).Name);
    }

    [Fact]
    public void Serialized_output_is_compact_utf8_with_no_newline_or_indent()
    {
        var bytes = OutfitPersistence.Serialize(Root(("Revette", 2)));
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("\n", text);
        Assert.DoesNotContain("\r", text);
        Assert.DoesNotContain(": ", text);   // WriteIndented also spaces after the key separator
        Assert.StartsWith("{\"Revette\":[{", text);
    }

    [Fact]
    public void Serialized_output_keeps_non_ascii_names_unescaped()
    {
        var root = new Dictionary<string, List<OutfitSlot>>
        {
            ["レヴェット"] = new() { new OutfitSlot { Name = "ชุดกลางคืน" } },
        };

        var text = Encoding.UTF8.GetString(OutfitPersistence.Serialize(root));

        Assert.Contains("ชุดกลางคืน", text);
        Assert.DoesNotContain("\\u", text);
    }

    // ---- tolerant decode ---------------------------------------------------------------------

    [Fact]
    public void Absent_file_decodes_to_an_empty_root_and_is_not_corrupt()
    {
        var decoded = OutfitPersistence.Deserialize(null, out var corrupt);

        Assert.False(corrupt);
        Assert.Empty(decoded);
    }

    [Fact]
    public void Empty_file_decodes_to_an_empty_root_and_is_not_corrupt()
    {
        var decoded = OutfitPersistence.Deserialize(System.Array.Empty<byte>(), out var corrupt);

        Assert.False(corrupt);
        Assert.Empty(decoded);
    }

    [Fact]
    public void Garbage_decodes_to_an_empty_root_and_flags_corrupt()
    {
        var decoded = OutfitPersistence.Deserialize(Encoding.UTF8.GetBytes("{not json"), out var corrupt);

        Assert.True(corrupt);
        Assert.Empty(decoded);
    }

    [Fact]
    public void Truncated_json_flags_corrupt_instead_of_throwing()
    {
        var bytes = OutfitPersistence.Serialize(Root(("Revette", 2)));
        var truncated = new byte[bytes.Length / 2];
        System.Array.Copy(bytes, truncated, truncated.Length);

        var decoded = OutfitPersistence.Deserialize(truncated, out var corrupt);

        Assert.True(corrupt);
        Assert.Empty(decoded);
    }

    [Fact]
    public void Literal_null_document_flags_corrupt()
    {
        var decoded = OutfitPersistence.Deserialize(Encoding.UTF8.GetBytes("null"), out var corrupt);

        Assert.True(corrupt);
        Assert.Empty(decoded);
    }

    [Fact]
    public void Null_members_are_normalized_away_so_a_decoded_slot_is_safe_to_read()
    {
        var json = "{\"Revette\":[null,{\"Name\":null,\"Regions\":null,\"Dyes\":null,\"DyeAreas\":null}],\"Ghost\":null}";

        var decoded = OutfitPersistence.Deserialize(Encoding.UTF8.GetBytes(json), out var corrupt);

        Assert.False(corrupt);
        Assert.False(decoded.ContainsKey("Ghost"));
        var slot = Assert.Single(decoded["Revette"]);
        Assert.Equal("", slot.Name);
        Assert.Empty(slot.Regions);
        Assert.Empty(slot.Dyes);
        Assert.Empty(slot.DyeAreas);
    }

    // ---- HasOutfits / CountOutfits -----------------------------------------------------------

    [Fact]
    public void HasOutfits_is_false_for_null_empty_and_all_empty_lists()
    {
        Assert.False(OutfitPersistence.HasOutfits(null));
        Assert.False(OutfitPersistence.HasOutfits(new Dictionary<string, List<OutfitSlot>>()));
        Assert.False(OutfitPersistence.HasOutfits(Root(("Revette", 0), ("Ribery", 0))));
    }

    [Fact]
    public void HasOutfits_and_CountOutfits_see_every_character()
    {
        var root = Root(("Revette", 53), ("Ribery", 0), ("Aria", 2));

        Assert.True(OutfitPersistence.HasOutfits(root));
        Assert.Equal(55, OutfitPersistence.CountOutfits(root));
    }

    // ---- migration decision (owner ruling 2026-09-05, plugindata) ----------------------------

    [Fact]
    public void Decide_uses_plugindata_even_when_the_frozen_config_still_has_outfits_owner_2026_09_05_plugindata()
    {
        Assert.Equal(MigrationDecision.UsePluginData, OutfitPersistence.Decide(pluginDataExists: true, configHasOutfits: true));
    }

    [Fact]
    public void Decide_uses_plugindata_when_it_is_the_only_source_owner_2026_09_05_plugindata()
    {
        Assert.Equal(MigrationDecision.UsePluginData, OutfitPersistence.Decide(pluginDataExists: true, configHasOutfits: false));
    }

    [Fact]
    public void Decide_migrates_when_only_the_config_has_outfits_owner_2026_09_05_plugindata()
    {
        Assert.Equal(MigrationDecision.MigrateFromConfig, OutfitPersistence.Decide(pluginDataExists: false, configHasOutfits: true));
    }

    [Fact]
    public void Decide_starts_empty_when_neither_source_has_outfits_owner_2026_09_05_plugindata()
    {
        Assert.Equal(MigrationDecision.StartEmpty, OutfitPersistence.Decide(pluginDataExists: false, configHasOutfits: false));
    }

    [Fact]
    public void Migrating_the_config_value_preserves_every_outfit_verbatim_owner_2026_09_05_plugindata()
    {
        // What Plugin.LoadOutfits() does on the MigrateFromConfig branch: normalize the config value,
        // serialize it to plugindata, and hand the same root to the store.
        var fromConfig = Root(("Revette", 53));
        fromConfig["Revette"][0].WeaponProfessionId = 9;
        fromConfig["Revette"][0].WeaponSkinId = 9000123;

        var migrated = OutfitPersistence.Normalize(fromConfig);
        var reloaded = OutfitPersistence.Deserialize(OutfitPersistence.Serialize(migrated), out var corrupt);

        Assert.False(corrupt);
        Assert.Equal(53, OutfitPersistence.CountOutfits(reloaded));
        Assert.Single(reloaded);
        Assert.Equal(9, reloaded["Revette"][0].WeaponProfessionId);
        Assert.Equal(9000123, reloaded["Revette"][0].WeaponSkinId);
        Assert.Equal("Revette-52", reloaded["Revette"][52].Name);
    }
}
