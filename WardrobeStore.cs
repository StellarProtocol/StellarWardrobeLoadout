using System;
using System.Collections.Generic;
using System.Linq;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// Per-character store of named wardrobe outfits. Wraps a <c>characterKey → List&lt;OutfitSlot&gt;</c>
/// root that the plugin persists to its config section verbatim (via <c>System.Text.Json</c>). Pure
/// BCL — no framework dependency — so the mutation logic is unit-testable in isolation.
///
/// <para>Ordering IS the slot order: index 0 is "slot 1" (hotkey H1), and so on; the overlay exposes
/// rename / update / delete / reorder (up-down) so the user controls which outfits get the first-8
/// hotkeys.</para>
/// </summary>
public sealed class WardrobeStore
{
    private readonly Dictionary<string, List<OutfitSlot>> _root;

    /// <summary>Wrap an existing root (from config), or start empty.</summary>
    public WardrobeStore(Dictionary<string, List<OutfitSlot>>? root = null)
        => _root = root ?? new Dictionary<string, List<OutfitSlot>>();

    /// <summary>The backing map, for the plugin to persist. Do not mutate directly — use the methods.</summary>
    public IReadOnlyDictionary<string, List<OutfitSlot>> Root => _root;

    /// <summary>The saved outfits for <paramref name="characterKey"/> in slot order (never null).</summary>
    public IReadOnlyList<OutfitSlot> Get(string characterKey)
        => _root.TryGetValue(characterKey, out var list) ? list : Array.Empty<OutfitSlot>();

    /// <summary>Append a new outfit as the last slot for <paramref name="characterKey"/>.</summary>
    public void Add(string characterKey, OutfitSlot slot) => List(characterKey).Add(slot);

    /// <summary>Rename the outfit at <paramref name="index"/>; returns false if out of range.</summary>
    public bool Rename(string characterKey, int index, string newName)
    {
        var list = List(characterKey);
        if (index < 0 || index >= list.Count) return false;
        list[index].Name = newName;
        return true;
    }

    /// <summary>Delete the outfit at <paramref name="index"/>; returns false if out of range.</summary>
    public bool Delete(string characterKey, int index)
    {
        var list = List(characterKey);
        if (index < 0 || index >= list.Count) return false;
        list.RemoveAt(index);
        return true;
    }

    /// <summary>Re-capture the outfit at <paramref name="index"/> in place from a freshly
    /// <paramref name="captured"/> slot: overwrite its regions, per-area dyes and weapon skin (clearing the
    /// legacy flat dyes — <see cref="OutfitSlot.DyeAreas"/> is authoritative) but KEEP its name and slot
    /// position. Takes the captured slot whole rather than one parameter per field so adding a captured
    /// facet (the weapon skin, 1.1.0) never widens the signature. Returns false if out of range.</summary>
    public bool Update(string characterKey, int index, OutfitSlot captured)
    {
        var list = List(characterKey);
        if (index < 0 || index >= list.Count) return false;
        var slot = list[index];
        slot.Regions = captured.Regions;
        slot.DyeAreas = captured.DyeAreas;
        slot.Dyes = new Dictionary<int, float[]>();
        slot.WeaponProfessionId = captured.WeaponProfessionId;
        slot.WeaponSkinId = captured.WeaponSkinId;
        slot.SavedAtMs = captured.SavedAtMs;
        return true;
    }

    /// <summary>Move the outfit at <paramref name="index"/> by <paramref name="delta"/> positions
    /// (e.g. -1 = up one slot, +1 = down one slot), clamped to the list. Returns false if out of range
    /// or the move is a no-op (already at that end).</summary>
    public bool Move(string characterKey, int index, int delta)
    {
        var list = List(characterKey);
        if (index < 0 || index >= list.Count) return false;
        var target = index + delta;
        if (target < 0 || target >= list.Count || target == index) return false;
        var slot = list[index];
        list.RemoveAt(index);
        list.Insert(target, slot);
        return true;
    }

    private List<OutfitSlot> List(string characterKey)
    {
        if (!_root.TryGetValue(characterKey, out var list))
        {
            list = new List<OutfitSlot>();
            _root[characterKey] = list;
        }
        return list;
    }

    /// One-time: if the current character's outfits are still under its NAME key (or "default") and it
    /// has no char-id-keyed entry yet, COPY them to the char-id key. Keeps the name key (rollback).
    /// Returns true if it copied. Idempotent: a non-empty char-id key short-circuits.
    public bool MigrateNameToCharId(string name, string charId)
    {
        if (string.IsNullOrEmpty(charId)) return false;
        if (_root.TryGetValue(charId, out var existing) && existing.Count > 0) return false;
        var src = !string.IsNullOrEmpty(name) && _root.TryGetValue(name, out var byName) && byName.Count > 0 ? byName
                : _root.TryGetValue("default", out var def) && def.Count > 0 ? def : null;
        if (src is null) return false;
        _root[charId] = src.Select(CloneSlot).ToList();   // deep copy; the two keys must not alias
        return true;
    }

    /// Deep-copies an <see cref="OutfitSlot"/> so a migrated entry and its legacy source never alias the
    /// same nested collections (a later Update/Rename on one must not mutate the other).
    private static OutfitSlot CloneSlot(OutfitSlot slot) => new()
    {
        Name = slot.Name,
        Regions = new Dictionary<int, int>(slot.Regions),
        Dyes = slot.Dyes.ToDictionary(kv => kv.Key, kv => (float[])kv.Value.Clone()),
        DyeAreas = slot.DyeAreas.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToDictionary(a => a.Key, a => (float[])a.Value.Clone())),
        WeaponProfessionId = slot.WeaponProfessionId,
        WeaponSkinId = slot.WeaponSkinId,
        SavedAtMs = slot.SavedAtMs,
    };
}
