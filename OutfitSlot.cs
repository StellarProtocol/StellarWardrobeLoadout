using System.Collections.Generic;

namespace Stellar.WardrobeLoadout;

/// <summary>One saved wardrobe outfit: a display name, the worn region→fashionId map
/// (0 = empty slot), and when it was saved. Plain settable properties so it round-trips through
/// <c>System.Text.Json</c> when persisted in the plugin config.</summary>
public sealed class OutfitSlot
{
    /// <summary>User-facing name (e.g. "Midnight Requiem").</summary>
    public string Name { get; set; } = "";

    /// <summary>Region (<c>FashionRegion</c> code) → fashionId; 0 = empty slot. Complete 14-region map.</summary>
    public Dictionary<int, int> Regions { get; set; } = new();

    /// <summary>Captured dye colours per region: region → flattened RGB triples (r0,g0,b0,r1,g1,b1,…),
    /// each channel 0..1, from <c>IEntityDetail.GetFashion</c> at save time (the source the Entity Inspector
    /// uses). A region absent here previews in the fashion's default colour.</summary>
    public Dictionary<int, float[]> Dyes { get; set; } = new();

    /// <summary>Unix-ms timestamp the outfit was saved (for newest-first ordering / display).</summary>
    public long SavedAtMs { get; set; }
}
