using System;
using System.Collections.Generic;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// Per-character store of named wardrobe outfits. Wraps a <c>characterKey → List&lt;OutfitSlot&gt;</c>
/// root that the plugin persists to its config section verbatim (via <c>System.Text.Json</c>). Pure
/// BCL — no framework dependency — so the mutation logic is unit-testable in isolation.
///
/// <para>Ordering IS the slot order: index 0 is "slot 1" (hotkey H1), and so on; the overlay exposes
/// rename / delete / move-to-top so the user controls which outfits get the first-8 hotkeys.</para>
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

    /// <summary>Move the outfit at <paramref name="index"/> to the front (slot 1 / hotkey H1);
    /// returns false if out of range or already first.</summary>
    public bool MoveToTop(string characterKey, int index)
    {
        var list = List(characterKey);
        if (index <= 0 || index >= list.Count) return false;
        var slot = list[index];
        list.RemoveAt(index);
        list.Insert(0, slot);
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
}
