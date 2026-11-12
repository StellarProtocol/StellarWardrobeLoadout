using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace Stellar.WardrobeLoadout.Tests;

/// <summary>Pins the per-outfit show/hide state (owner-approved 2026-11-13): parsing the game's global setting string
/// (<c>settingMap[201]</c>), the Lua table written back on apply, the success gate, the preview filter, the "N hidden"
/// count, and the persistence / store round trips. Knowledge Base <c>Fashion-Show-Hide-State.md</c>.</summary>
public sealed class HideStateTests
{
    // The string read back in game on 2026-11-13 (validated).
    private const string InGame = "721=2|722=2|723=2|717=2|718=1|711=2|712=2|713=1|714=2|715=2|716=1|701=2|702=1|703=1|";

    private static string Readback(string raw) => WardrobeRules.HideReadbackMarker + raw;

    // ---- parse ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_decodes_the_in_game_string_with_every_region_explicit()
    {
        var hidden = WardrobeRules.ParseHidden(Readback(InGame))!;
        Assert.Equal(WardrobeRules.HideableRegions.OrderBy(r => r), hidden.Keys.OrderBy(r => r));
        foreach (var r in new[] { 721, 722, 723, 717, 711, 712, 714, 715, 701 }) Assert.True(hidden[r], $"region {r}");
        foreach (var r in new[] { 718, 713, 716, 702, 703 }) Assert.False(hidden[r], $"region {r}");
    }

    [Fact]
    public void Parse_treats_a_missing_region_as_shown_and_an_empty_setting_as_all_shown()
    {
        var some = WardrobeRules.ParseHidden(Readback("713=2|"))!;
        Assert.True(some[713]);
        Assert.Equal(13, some.Count(kv => !kv.Value));

        var none = WardrobeRules.ParseHidden(Readback(""))!;   // never toggled: a successful read, nothing hidden
        Assert.Equal(14, none.Count);
        Assert.All(none.Values, Assert.False);
    }

    [Fact]
    public void Parse_ignores_weapon_skin_unknown_regions_and_garbage()
    {
        var hidden = WardrobeRules.ParseHidden(Readback("731=2|999=2|abc=2|713=x|=2|714|715=2=|712=2"))!;
        Assert.Equal(14, hidden.Count);
        Assert.False(hidden.ContainsKey(731));
        Assert.False(hidden.ContainsKey(999));
        Assert.False(hidden[713]);    // "x" is not a state
        Assert.False(hidden[714]);    // no '='
        Assert.True(hidden[712]);
        Assert.Equal(new[] { 712 }, hidden.Where(kv => kv.Value).Select(kv => kv.Key));   // "715=2=" is malformed too
    }

    [Fact]
    public void Parse_returns_null_when_the_read_failed()
    {
        Assert.Null(WardrobeRules.ParseHidden(null));      // global never set (chunk errored)
        Assert.Null(WardrobeRules.ParseHidden(""));
        Assert.Null(WardrobeRules.ParseHidden(InGame));    // no marker: not our readback
    }

    // ---- format --------------------------------------------------------------------------------------------------

    [Fact]
    public void Format_writes_all_14_regions_explicitly_missing_ones_as_shown()
    {
        var table = WardrobeRules.FormatHideTable(new Dictionary<int, bool> { [713] = true, [717] = false, [731] = true });
        Assert.Equal(
            "{[701]=false,[702]=false,[703]=false,[711]=false,[712]=false,[713]=true,[714]=false,[715]=false," +
            "[716]=false,[717]=false,[718]=false,[721]=false,[722]=false,[723]=false}", table);
        Assert.DoesNotContain("731", table);
    }

    [Fact]
    public void Format_round_trips_a_parsed_state()
    {
        var hidden = WardrobeRules.ParseHidden(Readback(InGame))!;
        var table = WardrobeRules.FormatHideTable(hidden);
        foreach (var kv in hidden) Assert.Contains($"[{kv.Key}]={(kv.Value ? "true" : "false")}", table);
    }

    // ---- apply gate, preview, count ------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, true)]      // switched + state saved → write it
    [InlineData(false, true, false)]    // switch FAILED → never touch visibility
    [InlineData(true, false, false)]    // outfit saved before the feature → leave show/hide alone
    [InlineData(false, false, false)]
    public void Hidden_state_is_sent_only_after_a_successful_switch_and_only_when_saved(bool ok, bool has, bool expected)
    {
        var slot = new OutfitSlot { Hidden = has ? new Dictionary<int, bool> { [713] = true } : null };
        Assert.Equal(expected, WardrobeRules.ShouldSendHidden(ok, slot));
    }

    [Fact]
    public void Preview_leaves_hidden_pieces_off_and_keeps_every_piece_for_a_legacy_outfit()
    {
        var regions = new Dictionary<int, int> { [701] = 55, [713] = 66, [717] = 77 };
        var slot = new OutfitSlot
        {
            Regions = regions,
            Hidden = new Dictionary<int, bool> { [713] = true, [717] = false, [701] = false },
        };
        var preview = WardrobeRules.PreviewOutfit(slot, 0);
        Assert.Equal(new[] { 701, 717 }, preview.Keys.OrderBy(k => k));
        Assert.Equal(3, slot.Regions.Count);                     // the saved outfit itself is untouched

        var legacy = new OutfitSlot { Regions = regions };       // Hidden == null
        Assert.Equal(3, WardrobeRules.PreviewOutfit(legacy, 0).Count);
    }

    [Fact]
    public void Hidden_count_counts_only_hidden_regions_that_hold_a_piece()
    {
        var slot = new OutfitSlot
        {
            Regions = new Dictionary<int, int> { [701] = 55, [713] = 66, [714] = 0 },
            Hidden = new Dictionary<int, bool> { [701] = true, [713] = true, [714] = true, [721] = true, [702] = false },
        };
        Assert.Equal(2, WardrobeRules.CountHiddenPieces(slot));   // 714 empty, 721 not worn
        Assert.Equal(0, WardrobeRules.CountHiddenPieces(new OutfitSlot { Regions = slot.Regions }));
    }

    // ---- persistence / store -------------------------------------------------------------------------------------

    [Fact]
    public void Hidden_round_trips_through_persistence()
    {
        var root = new Dictionary<string, List<OutfitSlot>>
        {
            ["1"] = new() { new OutfitSlot { Name = "a", Hidden = WardrobeRules.ParseHidden(Readback(InGame)) } },
        };
        var got = OutfitPersistence.Deserialize(OutfitPersistence.Serialize(root), out var corrupt)["1"][0];
        Assert.False(corrupt);
        Assert.NotNull(got.Hidden);
        Assert.Equal(14, got.Hidden!.Count);
        Assert.True(got.Hidden[701]);
        Assert.False(got.Hidden[713]);
    }

    [Fact]
    public void Old_json_without_the_field_loads_as_null_and_null_survives_a_round_trip()
    {
        var old = Encoding.UTF8.GetBytes("{\"1\":[{\"Name\":\"old\",\"Regions\":{\"701\":55},\"WeaponProfessionId\":0}]}");
        var slot = OutfitPersistence.Deserialize(old, out var corrupt)["1"][0];
        Assert.False(corrupt);
        Assert.Null(slot.Hidden);

        var again = OutfitPersistence.Deserialize(
            OutfitPersistence.Serialize(new Dictionary<string, List<OutfitSlot>> { ["1"] = new() { slot } }), out _);
        Assert.Null(again["1"][0].Hidden);
    }

    [Fact]
    public void Update_takes_the_captured_hidden_state_including_null()
    {
        var store = new WardrobeStore();
        store.Add("1", new OutfitSlot { Name = "x", Regions = new() { [701] = 1 } });

        Assert.True(store.Update("1", 0, new OutfitSlot { Regions = new() { [701] = 2 }, Hidden = new() { [713] = true } }));
        Assert.True(store.Get("1")[0].Hidden![713]);

        Assert.True(store.Update("1", 0, new OutfitSlot { Regions = new() { [701] = 3 }, Hidden = null }));
        Assert.Null(store.Get("1")[0].Hidden);   // unreadable at capture → applying leaves show/hide alone
    }
}
