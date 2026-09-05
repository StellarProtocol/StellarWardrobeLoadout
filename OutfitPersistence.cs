using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Stellar.WardrobeLoadout;

/// <summary>Where the saved outfits come from at startup — the outcome of
/// <see cref="OutfitPersistence.Decide"/>.</summary>
public enum MigrationDecision
{
    /// <summary>The plugin's own data store already holds a readable outfits file; use it and ignore
    /// the (frozen) legacy config copy entirely.</summary>
    UsePluginData,

    /// <summary>No usable plugindata file, but the legacy config section still carries outfits: copy
    /// them into plugindata once. The config key is LEFT IN PLACE.</summary>
    MigrateFromConfig,

    /// <summary>Neither source holds anything — a first run, or a user who never saved an outfit.</summary>
    StartEmpty,
}

/// <summary>
/// Serialization + the startup source decision for the wardrobe's saved outfits.
///
/// <para><b>Where outfits live (owner ruling 2026-09-05).</b> Outfits are user DATA, so they live in
/// the plugin's OWN per-plugin data store — <c>&lt;game_mini&gt;/stellar/plugindata/
/// stellar.wardrobeloadout.data/outfits.json</c> (the framework's <c>IPluginDataStore</c>)
/// — not in the shared <c>stellar.wardrobeloadout.config.json</c>. That file is a settings file: every
/// <c>Save()</c> rewrites the whole document and echoes through the framework's config file watcher, and
/// the owner's copy had grown to 55 KB of outfit JSON. The plugindata store writes atomically
/// (temp + rename), synchronously, and sits in a directory no watcher and no plugin-DLL scan looks at.</para>
///
/// <para><b>Rollback safety (process rules § 6).</b> The migration COPIES; it never deletes. The old
/// <c>wardrobe.outfits</c> config key is left exactly as it was and is never written again, so a rollback
/// to a pre-migration build still finds every outfit the user had at migration time. Returning to a
/// migrated build then ignores that frozen copy, because plugindata now exists and always wins.</para>
///
/// <para>Pure BCL (no framework types) so the unit-test project can compile this file directly.</para>
/// </summary>
public static class OutfitPersistence
{
    /// <summary>Plugindata file name holding the whole <c>characterKey → outfits</c> root.</summary>
    public const string FileName = "outfits.json";

    /// <summary>Where unreadable bytes are parked before the live file is rewritten — user bytes we
    /// could not parse are never destroyed, only moved aside.</summary>
    public const string CorruptFileName = "outfits.corrupt.json";

    // Compact (no indent, no newlines) — this is a machine file, and the owner's root is ~55 KB.
    // Relaxed escaping keeps non-ASCII outfit names (JA/TH/ID) readable instead of \uXXXX-expanded.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serialize the <c>characterKey → outfits</c> root to compact UTF-8 JSON. Same shape as the
    /// value the legacy config key held, so a migrated file is byte-comparable with the old config value.</summary>
    /// <param name="root">The store's backing map.</param>
    /// <returns>UTF-8 JSON bytes.</returns>
    public static byte[] Serialize(IReadOnlyDictionary<string, List<OutfitSlot>> root)
        => JsonSerializer.SerializeToUtf8Bytes(root, Options);

    /// <summary>Decode a stored root, tolerantly: absent/empty input yields an empty root and
    /// <paramref name="corrupt"/> false; unparseable input yields an empty root and
    /// <paramref name="corrupt"/> true so the caller can log it and park the bytes. Never throws.</summary>
    /// <param name="data">The bytes read from the data store, or null when the file is absent.</param>
    /// <param name="corrupt">True when <paramref name="data"/> held something that could not be decoded.</param>
    /// <returns>The decoded root, normalized; never null.</returns>
    public static Dictionary<string, List<OutfitSlot>> Deserialize(byte[]? data, out bool corrupt)
    {
        corrupt = false;
        if (data is null || data.Length == 0) return new Dictionary<string, List<OutfitSlot>>();
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, List<OutfitSlot>>>(data, Options);
            if (root is null)
            {
                corrupt = true;   // literal "null" — not a usable root
                return new Dictionary<string, List<OutfitSlot>>();
            }
            return Normalize(root);
        }
        catch (JsonException)
        {
            corrupt = true;
            return new Dictionary<string, List<OutfitSlot>>();
        }
        catch (NotSupportedException)
        {
            corrupt = true;
            return new Dictionary<string, List<OutfitSlot>>();
        }
    }

    /// <summary>Which source the outfits come from this session. Plugindata ALWAYS wins when it holds a
    /// readable file — even if the frozen legacy config still carries outfits — so a rollback-and-return
    /// round trip never resurrects stale copies over newer work.</summary>
    /// <param name="pluginDataExists">True when a readable outfits file was decoded from plugindata.</param>
    /// <param name="configHasOutfits">True when the legacy config key still holds at least one outfit.</param>
    /// <returns>The source to use.</returns>
    public static MigrationDecision Decide(bool pluginDataExists, bool configHasOutfits)
        => pluginDataExists ? MigrationDecision.UsePluginData
         : configHasOutfits ? MigrationDecision.MigrateFromConfig
         : MigrationDecision.StartEmpty;

    /// <summary>True when <paramref name="root"/> holds at least one outfit for at least one character.
    /// A root of empty lists is NOT outfits — migrating it would write a file that says nothing.</summary>
    /// <param name="root">A candidate root (may be null).</param>
    /// <returns>Whether anything worth migrating is present.</returns>
    public static bool HasOutfits(IReadOnlyDictionary<string, List<OutfitSlot>>? root)
    {
        if (root is null) return false;
        foreach (var kv in root)
        {
            if (kv.Value is { Count: > 0 }) return true;
        }
        return false;
    }

    /// <summary>Total number of outfits across every character in <paramref name="root"/>.</summary>
    /// <param name="root">A root (may be null).</param>
    /// <returns>The outfit count.</returns>
    public static int CountOutfits(IReadOnlyDictionary<string, List<OutfitSlot>>? root)
    {
        if (root is null) return 0;
        var total = 0;
        foreach (var kv in root)
        {
            if (kv.Value is not null) total += kv.Value.Count;
        }
        return total;
    }

    /// <summary>Drop null characters/outfits and replace null collections inside an outfit with empty
    /// ones, so every decoded slot is safe to read without null checks. Applied to both decode paths
    /// (plugindata and the legacy config value) so they cannot diverge.</summary>
    /// <param name="root">A decoded root (may be null).</param>
    /// <returns>A fresh, normalized root; never null.</returns>
    public static Dictionary<string, List<OutfitSlot>> Normalize(IReadOnlyDictionary<string, List<OutfitSlot>>? root)
    {
        var clean = new Dictionary<string, List<OutfitSlot>>(root?.Count ?? 0, StringComparer.Ordinal);
        if (root is null) return clean;
        foreach (var kv in root)
        {
            if (kv.Key is null || kv.Value is null) continue;
            var list = new List<OutfitSlot>(kv.Value.Count);
            foreach (var slot in kv.Value)
            {
                if (slot is null) continue;
                slot.Name ??= "";
                slot.Regions ??= new Dictionary<int, int>();
                slot.Dyes ??= new Dictionary<int, float[]>();
                slot.DyeAreas ??= new Dictionary<int, Dictionary<int, float[]>>();
                list.Add(slot);
            }
            clean[kv.Key] = list;
        }
        return clean;
    }
}
