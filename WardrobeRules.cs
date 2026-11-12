using System;
using System.Collections.Generic;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// The plugin's behavioural decisions, extracted as pure functions so they can be pinned by unit
/// tests. Pure BCL — no framework types — because the test project compiles these sources directly
/// (there is no Unity/IL2CPP surface in a test run).
/// </summary>
public static class WardrobeRules
{
    // Mirrors Stellar.Abstractions' WardrobeRegions.WeaponSkinPreview. Repeated as a literal so this
    // file stays pure BCL for the test project; the plugin itself passes the framework constant around.
    private const int WeaponSkinPreviewRegion = 731;

    /// <summary>Whether an outfit's saved weapon skin should be sent after the outfit switch resolved.
    /// True only when the outfit apply SUCCEEDED and the outfit actually carries a skin: a failed outfit
    /// apply must never be followed by a weapon-skin RPC (the player keeps the look they had), and an
    /// outfit saved before 1.1.0 carries no skin, so applying it leaves the worn skin alone.</summary>
    /// <param name="outfitSucceeded">True when the outfit switch returned success.</param>
    /// <param name="slot">The outfit being applied.</param>
    public static bool ShouldSendWeaponSkin(bool outfitSucceeded, OutfitSlot slot)
        => outfitSucceeded && slot.HasWeaponSkin();

    /// <summary>The outfit map to hand <c>IWardrobePreview.Show</c>: the slot's regions, plus its weapon skin
    /// under the preview-only key 731 when that skin belongs to the class the player is CURRENTLY on.
    /// <para>Owner rule, 2026-09-05: <i>"The 3D preview does not show the weapon skin. &lt;-- it should show if
    /// class match"</i>. Weapon skins are per-class (every <c>WeaponSkinTable</c> row is <c>ProfessionId</c>-scoped
    /// and the id encodes it), so another class's skin cannot render on this model — the key is then omitted and
    /// the weapon is left exactly as the model shows it, which is what the game's own weapon-skin tab does for a
    /// non-current class. A stored skin id of 0 means "the class's default look" and is passed through as 0; the
    /// framework resolves it the same way applying the outfit would, so the preview cannot disagree with the
    /// result. An unknown current class (0 — not in world) also omits the key.</para></summary>
    /// <param name="slot">The outfit being previewed.</param>
    /// <param name="currentProfessionId">The class the player is on right now, or 0 when unknown.</param>
    public static Dictionary<int, int> PreviewOutfit(OutfitSlot slot, int currentProfessionId)
    {
        var outfit = new Dictionary<int, int>(slot.Regions.Count + 1);
        // A piece the outfit keeps HIDDEN is left off the preview model, so the preview shows what wearing the outfit
        // looks like. Outfits saved before the show/hide capture (Hidden == null) preview every piece, as before.
        foreach (var kv in slot.Regions)
        {
            if (slot.Hidden is { } hidden && hidden.TryGetValue(kv.Key, out var h) && h) continue;
            outfit[kv.Key] = kv.Value;
        }
        if (currentProfessionId > 0 && slot.HasWeaponSkin() && slot.WeaponProfessionId == currentProfessionId)
            outfit[WeaponSkinPreviewRegion] = slot.WeaponSkinId;
        return outfit;
    }

    /// <summary>How many regions of <paramref name="outfit"/> hold a piece (a non-zero fashion id).</summary>
    /// <param name="outfit">A region → fashionId map (0 = empty region).</param>
    public static int CountNonZero(IReadOnlyDictionary<int, int> outfit)
    {
        var n = 0;
        foreach (var kv in outfit)
        {
            if (kv.Value != 0) n++;
        }
        return n;
    }

    /// <summary>Whether a saved outfit is the one being WORN: its non-zero regions equal the worn outfit's
    /// non-zero regions exactly (same set of regions, same piece in each). Empty regions are ignored on both
    /// sides, so a saved map that lists a region as 0 and a worn map that omits it still match. Nothing worn
    /// never matches. Dyes and the weapon skin are not compared: the "Worn" marker is about which clothes are on.
    /// Allocation-free over the saved map (a concrete <c>Dictionary</c> enumerates without boxing); the caller
    /// counts the worn side ONCE per check with <see cref="CountNonZero"/> and passes it in, so checking every
    /// saved outfit walks the worn map once.</summary>
    /// <param name="saved">The saved outfit's regions.</param>
    /// <param name="worn">The currently worn region → fashionId map.</param>
    /// <param name="wornNonZero"><see cref="CountNonZero"/> of <paramref name="worn"/>.</param>
    public static bool MatchesWorn(Dictionary<int, int> saved, IReadOnlyDictionary<int, int> worn, int wornNonZero)
    {
        if (wornNonZero == 0) return false;
        var n = 0;
        foreach (var kv in saved)
        {
            if (kv.Value == 0) continue;
            if (!worn.TryGetValue(kv.Key, out var w) || w != kv.Value) return false;
            n++;
        }
        return n == wornNonZero;
    }

    /// <summary>Whether <see cref="PickWorn"/> needs the game's current show/hide state: some saved outfit matches the
    /// worn pieces AND carries a saved state to check against it. Otherwise (no piece match, or only older saves with
    /// <c>Hidden == null</c>) the overlay skips the Lua read entirely.</summary>
    /// <param name="rows">The character's saved outfits, in list order.</param>
    /// <param name="worn">The currently worn region → fashionId map.</param>
    /// <param name="wornNonZero"><see cref="CountNonZero"/> of <paramref name="worn"/>.</param>
    public static bool NeedsHiddenState(IReadOnlyList<OutfitSlot> rows, IReadOnlyDictionary<int, int> worn, int wornNonZero)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Hidden is not null && MatchesWorn(rows[i].Regions, worn, wornNonZero)) return true;
        return false;
    }

    /// <summary>Which saved outfit is being WORN, or null. Origin: user report 2026-11-13, "Vampy" and "Naked" hold the
    /// same pieces (Naked hides them), and wearing Naked said Worn = Vampy because the first piece match won. Owner rule
    /// for "wears a slot, then changes things in game":
    /// <list type="bullet">
    /// <item>pieces changed so no outfit <see cref="MatchesWorn"/> → null (no Worn anywhere);</item>
    /// <item>pieces match but the CURRENT show/hide differs from the outfit's saved <see cref="OutfitSlot.Hidden"/> →
    /// that outfit is NOT worn (a modified look); if every piece match has a saved state and none equals the current
    /// one → null;</item>
    /// <item>an exact show/hide match (<see cref="HiddenEquals"/>) beats an outfit saved without a state
    /// (<c>Hidden == null</c>, older saves) — those can't be checked, so they count as worn on pieces alone, but only
    /// when no candidate matches exactly;</item>
    /// <item><paramref name="currentHidden"/> null (setting unreadable) → every piece match is eligible: a read failure
    /// must not blank Worn.</item>
    /// </list>
    /// Ties go to <paramref name="lastApplied"/> (the outfit this plugin last put on, by object identity), then to list
    /// order. Dyes and the weapon skin are deliberately NOT compared — Worn is about which clothes are on and which are
    /// shown; a re-dye or weapon-skin swap keeps the outfit marked. Allocation-free.</summary>
    /// <param name="rows">The character's saved outfits, in list order.</param>
    /// <param name="worn">The currently worn region → fashionId map.</param>
    /// <param name="wornNonZero"><see cref="CountNonZero"/> of <paramref name="worn"/>.</param>
    /// <param name="currentHidden">The game's current region → hidden state, or null when it could not be read.</param>
    /// <param name="lastApplied">The outfit the plugin last applied successfully, or null.</param>
    public static OutfitSlot? PickWorn(IReadOnlyList<OutfitSlot> rows, IReadOnlyDictionary<int, int> worn, int wornNonZero,
        IReadOnlyDictionary<int, bool>? currentHidden, OutfitSlot? lastApplied)
    {
        OutfitSlot? best = null;
        int bestRank = -1;
        bool bestIsLast = false;
        for (var i = 0; i < rows.Count; i++)
        {
            var slot = rows[i];
            if (!MatchesWorn(slot.Regions, worn, wornNonZero)) continue;
            int rank = currentHidden is null ? 0
                     : slot.Hidden is null ? 1
                     : HiddenEquals(slot.Hidden, currentHidden) ? 2 : -1;
            if (rank < 0) continue;   // pieces match but show/hide was changed since → a modified look, not this outfit
            bool isLast = ReferenceEquals(slot, lastApplied);
            // Strictly better rank wins; at equal rank only the last-applied outfit displaces an earlier one, so
            // otherwise the first in list order stays.
            if (rank > bestRank || (rank == bestRank && isLast && !bestIsLast))
            {
                best = slot;
                bestRank = rank;
                bestIsLast = isLast;
            }
        }
        return best;
    }

    /// <summary>Whether two show/hide states are the same over the 14 <see cref="HideableRegions"/> (a region missing
    /// from either map = shown, exactly as the game reads its setting string). Anything else in the maps is ignored.</summary>
    /// <param name="a">One region → hidden map.</param>
    /// <param name="b">The other.</param>
    public static bool HiddenEquals(IReadOnlyDictionary<int, bool> a, IReadOnlyDictionary<int, bool> b)
    {
        foreach (var region in HideableRegions)
        {
            bool ha = a.TryGetValue(region, out var x) && x;
            bool hb = b.TryGetValue(region, out var y) && y;
            if (ha != hb) return false;
        }
        return true;
    }

    /// <summary>Whether an outfit's saved show/hide state should be written after the outfit switch resolved. Same
    /// gate as the weapon skin: only after a SUCCESSFUL switch (a refused switch leaves the player's look — pieces AND
    /// visibility — exactly as it was), and only when the outfit carries a state (null = saved before the feature or
    /// unreadable at save time → the current show/hide is left alone).</summary>
    /// <param name="outfitSucceeded">True when the outfit switch returned success.</param>
    /// <param name="slot">The outfit being applied.</param>
    public static bool ShouldSendHidden(bool outfitSucceeded, OutfitSlot slot)
        => outfitSucceeded && slot.Hidden is not null;

    /// <summary>The wardrobe regions the game lets the player hide (<c>E.FashionRegion</c>, the order of the game's
    /// fashion-settings panel). Weapon skin 731 is NOT hideable — the panel never lists it. Knowledge Base
    /// <c>Fashion-Show-Hide-State.md</c>.</summary>
    public static readonly int[] HideableRegions = { 701, 702, 703, 711, 712, 713, 714, 715, 716, 717, 718, 721, 722, 723 };

    /// <summary>Marker the Lua read chunk prefixes to the raw setting string, so an EMPTY setting (nothing ever
    /// hidden = everything shown, a valid state) is told apart from a failed read (the global left nil).</summary>
    public const char HideReadbackMarker = '#';

    /// <summary>Decode the Lua readback of the game's per-character show/hide setting
    /// (<c>CharSerialize.settingData.settingMap[201]</c>, format <c>"region=state|region=state|…"</c>, state 2 =
    /// hidden, 1 or missing = shown) into an explicit region → hidden map covering EVERY hideable region. Returns
    /// null when the read failed (no marker — the Lua chunk errored or the global was never set): the caller then
    /// stores no show/hide state rather than a wrong one. Unknown regions (731, garbage ids) and malformed entries
    /// are ignored; a region the string does not mention is shown, exactly as the game reads it.</summary>
    /// <param name="readback">The parked Lua global: the marker followed by the raw setting string, or null.</param>
    public static Dictionary<int, bool>? ParseHidden(string? readback)
    {
        if (readback is null || readback.Length == 0 || readback[0] != HideReadbackMarker) return null;
        var hidden = new Dictionary<int, bool>(HideableRegions.Length);
        foreach (var region in HideableRegions) hidden[region] = false;
        foreach (var entry in readback.Substring(1).Split('|'))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            if (!int.TryParse(entry.AsSpan(0, eq), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var region)) continue;
            if (!int.TryParse(entry.AsSpan(eq + 1), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var state)) continue;
            if (hidden.ContainsKey(region)) hidden[region] = state == 2;
        }
        return hidden;
    }

    /// <summary>The Lua table literal the apply chunk walks into <c>fashion_data:SetFashionSetting(region, isHide)</c>:
    /// <c>{[701]=false,[702]=true,…}</c> with an EXPLICIT true/false for every hideable region (a region missing from
    /// <paramref name="hidden"/> is written shown), so applying an outfit sets the whole show/hide state, never a merge
    /// with whatever the player toggled since. That is load-bearing, not tidiness: the live client's save writes its
    /// local dict AS IS, so a partial table would WIPE the player's other hide settings on the server (Knowledge Base
    /// <c>Fashion-Show-Hide-State.md</c> "LIVE client differs"). Only ids from <see cref="HideableRegions"/> are
    /// emitted — values we control, so the literal cannot carry anything else into Lua.</summary>
    /// <param name="hidden">The outfit's saved region → hidden map.</param>
    public static string FormatHideTable(IReadOnlyDictionary<int, bool> hidden)
    {
        var sb = new System.Text.StringBuilder(HideableRegions.Length * 12 + 2);
        sb.Append('{');
        for (var i = 0; i < HideableRegions.Length; i++)
        {
            var region = HideableRegions[i];
            if (i > 0) sb.Append(',');
            sb.Append('[').Append(region.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("]=")
              .Append(hidden.TryGetValue(region, out var h) && h ? "true" : "false");
        }
        return sb.Append('}').ToString();
    }

    /// <summary>The last guard before a hide table reaches the game: true only when <paramref name="table"/> names
    /// EVERY hideable region with an explicit <c>true</c>/<c>false</c>. The apply path refuses anything else — a
    /// partial table saved through the live client replaces the whole server setting (see
    /// <see cref="FormatHideTable"/>).</summary>
    /// <param name="table">A Lua table literal, normally from <see cref="FormatHideTable"/>.</param>
    public static bool IsCompleteHideTable(string? table)
    {
        if (string.IsNullOrEmpty(table)) return false;
        foreach (var region in HideableRegions)
        {
            var key = "[" + region.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]=";
            if (!table.Contains(key + "true", StringComparison.Ordinal)
                && !table.Contains(key + "false", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>How many of the outfit's PIECES it keeps hidden: hidden regions that actually hold a piece. A hidden
    /// but empty region is not counted — the game string routinely marks empty regions hidden, and "3 hidden" for an
    /// outfit with nothing to hide would mislead. 0 for an outfit without a saved show/hide state.</summary>
    /// <param name="slot">The outfit.</param>
    public static int CountHiddenPieces(OutfitSlot slot)
    {
        if (slot.Hidden is not { } hidden) return 0;
        var n = 0;
        foreach (var kv in hidden)
        {
            if (kv.Value && slot.Regions.TryGetValue(kv.Key, out var id) && id != 0) n++;
        }
        return n;
    }

    /// <summary>The short label for a bound hotkey in an outfit row's 30 px badge: the binding's own text
    /// ("F1", "Ctrl+F2", the framework's Ctrl → Shift → Alt order) with the number-row keys shortened to their
    /// digit ("Alpha2" → "2") and the keypad keys to "Num" ("Keypad2" → "Num2"), so common bindings fit.</summary>
    /// <param name="keyName">The primary key's name (<c>StellarKeyCode.ToString()</c>).</param>
    /// <param name="ctrl">Ctrl held.</param>
    /// <param name="shift">Shift held.</param>
    /// <param name="alt">Alt held.</param>
    public static string ShortKeyLabel(string keyName, bool ctrl, bool shift, bool alt)
    {
        var key = keyName.Length == 6 && keyName.StartsWith("Alpha", StringComparison.Ordinal) ? keyName.Substring(5)
                : keyName.StartsWith("Keypad", StringComparison.Ordinal) ? "Num" + keyName.Substring(6)
                : keyName;
        return (ctrl ? "Ctrl+" : "") + (shift ? "Shift+" : "") + (alt ? "Alt+" : "") + key;
    }
}
