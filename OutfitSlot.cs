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

    /// <summary>LEGACY captured dye colours per region: region → flattened RGB triples (r0,g0,b0,r1,g1,b1,…),
    /// each channel 0..1. Written by outfits saved before per-area capture existed; read as a positional
    /// fallback when <see cref="DyeAreas"/> is empty. New saves populate <see cref="DyeAreas"/> instead.</summary>
    public Dictionary<int, float[]> Dyes { get; set; } = new();

    /// <summary>Captured dye colours per region AND per area: region → (<c>EFashionColorAreaType</c> area
    /// 1..16 → RGB triple [r,g,b], each channel 0..1), from <c>IEntityDetail.GetFashion</c> at save time.
    /// Carries the real area of each colour so multi-area pieces preview correctly. A region absent here
    /// (older saves) falls back to <see cref="Dyes"/>; a region absent from both previews in default colour.</summary>
    public Dictionary<int, Dictionary<int, float[]>> DyeAreas { get; set; } = new();

    /// <summary>Unix-ms timestamp the outfit was saved (for newest-first ordering / display).</summary>
    public long SavedAtMs { get; set; }
}
