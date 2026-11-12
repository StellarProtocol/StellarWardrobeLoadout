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
        var outfit = new Dictionary<int, int>(slot.Regions);
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
