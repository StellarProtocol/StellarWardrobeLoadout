using System.Collections.Generic;
using Xunit;

namespace Stellar.WardrobeLoadout.Tests;

/// <summary>Pins the Dyeing Scheme restore (owner-approved 2026-11-27): the colour readback parse, the Lua literals sent
/// to the game, the success gate, the run-result parse, the toast decision, and the store / JSON round trips of
/// <see cref="OutfitSlot.DyeColors"/>. Knowledge Base <c>Fashion-Dye-Areas.md</c> "Dyeing Scheme".</summary>
public sealed class DyeSchemeTests
{
    private static Dictionary<int, Dictionary<int, int[]>> Colors() => new()
    {
        [7037026] = new() { [5] = new[] { 0, 0, 100 }, [1] = new[] { 120, 50, 60 } },
        [7015150] = new(),
    };

    // ---- colour read ----------------------------------------------------------------------------------------------

    [Fact]
    public void ParseColors_decodes_areas_and_default_coloured_pieces()
    {
        var c = DyeSchemeRules.ParseColors("ok|7037026:1=120,50,60/5=0,0,100;7015150:")!;
        Assert.Equal(new[] { 120, 50, 60 }, c[7037026][1]);
        Assert.Equal(new[] { 0, 0, 100 }, c[7037026][5]);
        Assert.Empty(c[7015150]);
    }

    [Fact]
    public void ParseColors_accepts_integral_floats_and_drops_only_the_malformed_piece()
    {
        var c = DyeSchemeRules.ParseColors("ok|1:1=120.0,50,60;2:1=1.5,2,3;3:1=nil,2,3;4:1=9,9,9")!;
        Assert.Equal(new[] { 120, 50, 60 }, c[1][1]);
        Assert.False(c.ContainsKey(2));   // non-integral → would never compare equal to the game's ints
        Assert.False(c.ContainsKey(3));
        Assert.True(c.ContainsKey(4));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ok|")]
    [InlineData("7037026:1=1,2,3")]
    public void ParseColors_is_null_when_the_read_failed_or_found_nothing(string? raw)
        => Assert.Null(DyeSchemeRules.ParseColors(raw));

    [Fact]
    public void FormatFidList_is_distinct_sorted_non_zero()
    {
        var regions = new Dictionary<int, int> { [701] = 7037026, [702] = 0, [703] = 7015150, [704] = 7015150 };
        Assert.Equal("{7015150,7037026}", DyeSchemeRules.FormatFidList(regions));
        Assert.Null(DyeSchemeRules.FormatFidList(new Dictionary<int, int> { [701] = 0 }));
    }

    // ---- wear -----------------------------------------------------------------------------------------------------

    [Fact]
    public void FormatPieces_emits_the_GetServerFashionAllColor_shape_sorted()
    {
        Assert.Equal("{{7015150,{}},{7037026,{[1]={h=120,s=50,v=60},[5]={h=0,s=0,v=100}}}}",
            DyeSchemeRules.FormatPieces(Colors()));
    }

    [Fact]
    public void FormatPieces_skips_null_maps_and_bad_colours()
    {
        var c = new Dictionary<int, Dictionary<int, int[]>>
        {
            [1] = null!,
            [2] = new() { [1] = new[] { 1, 2 }, [2] = new[] { 3, 4, 5 } },
        };
        Assert.Equal("{{2,{[2]={h=3,s=4,v=5}}}}", DyeSchemeRules.FormatPieces(c));
        Assert.Null(DyeSchemeRules.FormatPieces(new Dictionary<int, Dictionary<int, int[]>> { [1] = null! }));
    }

    [Fact]
    public void Sends_only_after_success_and_only_with_recorded_colours()
    {
        var with = new OutfitSlot { DyeColors = Colors() };
        Assert.True(DyeSchemeRules.ShouldSendDyeSchemes(true, with));
        Assert.False(DyeSchemeRules.ShouldSendDyeSchemes(false, with));
        Assert.False(DyeSchemeRules.ShouldSendDyeSchemes(true, new OutfitSlot()));   // pre-feature outfit: old behaviour
        Assert.False(DyeSchemeRules.ShouldSendDyeSchemes(true, new OutfitSlot { DyeColors = new() }));
    }

    // ---- result ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ParseResult_decodes_every_piece_kind()
    {
        var o = DyeSchemeRules.ParseResult("4|done|1=same;2=ok:3;3=err:2:3413;4=none;5=x:attempt to index a nil value", 4)!;
        Assert.Equal("done", o.Status);
        Assert.Equal(new[] { DyePieceKind.Same, DyePieceKind.Applied, DyePieceKind.Failed, DyePieceKind.NoMatch, DyePieceKind.Error },
            o.Pieces.ConvertAll(p => p.Kind));
        Assert.Equal(3, o.Pieces[1].SlotId);
        Assert.Equal("3413", o.Pieces[2].Detail);
        Assert.Equal("attempt to index a nil value", o.Pieces[4].Detail);
        Assert.Equal(3, o.Unrestored);
        Assert.True(o.ShouldToast);
        Assert.Equal("fid=1 -> same, fid=2 -> slot 3 ok, fid=3 -> slot 2 err 3413, fid=4 -> no match, fid=5 -> error attempt to index a nil value",
            o.Summary());
    }

    [Fact]
    public void No_toast_when_everything_was_restored_or_the_feature_is_off()
    {
        Assert.False(DyeSchemeRules.ParseResult("1|done|1=same;2=ok:1", 1)!.ShouldToast);
        var off = DyeSchemeRules.ParseResult("1|off|", 1)!;
        Assert.Equal("off", off.Status);
        Assert.False(off.ShouldToast);
        var fail = DyeSchemeRules.ParseResult("1|fail|boom", 1)!;
        Assert.Equal("failed: boom", fail.Summary());
        Assert.False(fail.ShouldToast);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nofn")]
    [InlineData("3|done|1=same")]   // another run's result
    public void ParseResult_is_null_while_pending_or_for_another_run(string? raw)
        => Assert.Null(DyeSchemeRules.ParseResult(raw, 4));

    // ---- persistence ----------------------------------------------------------------------------------------------

    [Fact]
    public void DyeColors_round_trips_through_json_and_is_absent_from_older_saves()
    {
        var root = new Dictionary<string, List<OutfitSlot>> { ["c"] = new() { new OutfitSlot { Name = "a", DyeColors = Colors() } } };
        var back = OutfitPersistence.Deserialize(OutfitPersistence.Serialize(root), out var corrupt)["c"][0];
        Assert.False(corrupt);
        Assert.Equal(new[] { 120, 50, 60 }, back.DyeColors![7037026][1]);
        Assert.Empty(back.DyeColors[7015150]);
        Assert.Null(JsonSerializer.Deserialize<OutfitSlot>("{\"Name\":\"old\"}")!.DyeColors);
    }

    [Fact]
    public void Update_takes_the_captured_colours_including_null()
    {
        var store = new WardrobeStore(new Dictionary<string, List<OutfitSlot>>());
        store.Add("c", new OutfitSlot { Name = "x", DyeColors = Colors() });
        store.Update("c", 0, new OutfitSlot { DyeColors = null });
        Assert.Null(store.Get("c")[0].DyeColors);
        store.Update("c", 0, new OutfitSlot { DyeColors = Colors() });
        Assert.Equal(2, store.Get("c")[0].DyeColors!.Count);
    }

    [Fact]
    public void Name_migration_deep_copies_the_colours()
    {
        var src = new OutfitSlot { Name = "x", DyeColors = Colors() };
        var store = new WardrobeStore(new Dictionary<string, List<OutfitSlot>> { ["Name"] = new() { src } });
        Assert.True(store.MigrateNameToCharId("Name", "42"));
        var copy = store.Get("42")[0];
        Assert.NotSame(src.DyeColors, copy.DyeColors);
        Assert.NotSame(src.DyeColors![7037026][1], copy.DyeColors![7037026][1]);
        Assert.Equal(src.DyeColors[7037026][1], copy.DyeColors[7037026][1]);
    }
}
