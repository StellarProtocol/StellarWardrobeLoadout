using System;
using System.Collections.Generic;
using System.Text;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// Diagnostic sibling partial for <see cref="Plugin"/>. Per-event hotkey/save/apply trace lines are
/// gated on <see cref="StellarDiagnostics.IsEnabled"/> so normal play stays quiet; the user-facing
/// apply outcomes (in <c>Plugin.cs</c>) always log.
/// </summary>
public sealed partial class Plugin
{
    private void DiagApplying(OutfitSlot slot)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[WardrobeLoadout] apply '{slot.Name}' ({Worn(slot.Regions)} worn, {Weapon(slot)})");
    }

    private void DiagSkipped(int slotNumber, string reason)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[WardrobeLoadout] apply slot {slotNumber} skipped: {reason}");
    }

    // A store op (save/apply/rename/update/move/delete) that was refused because the char id hasn't
    // resolved yet — never write outfits under an unknown character.
    private void DiagStoreSkipped(string op)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[WardrobeLoadout] {op} skipped: char unresolved");
    }

    private void DiagSaved(OutfitSlot slot, string charKey)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[WardrobeLoadout] saved '{slot.Name}' ({Worn(slot.Regions)} worn, {Weapon(slot)}) for {charKey}");
    }

    private void DiagUpdated(OutfitSlot slot, string charKey)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[WardrobeLoadout] updated '{slot.Name}' -> current outfit ({Worn(slot.Regions)} worn, {Weapon(slot)}) for {charKey}");
    }

    // "weapon=<professionId>/<skinId>" for an outfit that carries a skin, else "weapon=none" — so a saved
    // or applied outfit's skin pair is visible in the log without a second read.
    private static string Weapon(OutfitSlot slot)
        => slot.HasWeaponSkin() ? $"weapon={slot.WeaponProfessionId}/{slot.WeaponSkinId}" : "weapon=none";

    // Log the per-area dyes chosen for a preview as region:{area=RRGGBB …} so a test can confirm each
    // colour landed on its real EFashionColorAreaType area (Base1-4=1-4, Socks1-4=5-8, BaseEx=9-12,
    // UnderWear=13-16) — the hex matches what the Entity Inspector shows for the worn piece.
    private void DiagPreview(OutfitSlot slot, IReadOnlyDictionary<int, int> previewOutfit)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var sb = new StringBuilder();
        foreach (var reg in slot.DyeAreas)
        {
            sb.Append(reg.Key).Append(":{");
            foreach (var a in reg.Value) sb.Append(a.Key).Append('=').Append(Hex(a.Value)).Append(' ');
            sb.Append("} ");
        }
        _services.Log.Info($"[WardrobeLoadout] preview '{slot.Name}' dyeAreas={sb}(legacyFlat={slot.Dyes.Count}) {PreviewWeapon(slot, previewOutfit)}");
    }

    // What the preview will actually dress on the weapon, and why when it won't (the class-match rule).
    private static string PreviewWeapon(OutfitSlot slot, IReadOnlyDictionary<int, int> previewOutfit)
    {
        if (previewOutfit.TryGetValue(WardrobeRegions.WeaponSkinPreview, out var skinId))
            return skinId == 0 ? "weapon=default-look" : $"weapon={skinId}";
        if (!slot.HasWeaponSkin()) return "weapon=none";
        return $"weapon=none (stored for class {slot.WeaponProfessionId}, not the current class)";
    }

    private static string Hex(float[] rgb)
    {
        if (rgb.Length < 3) return "??????";
        int R = (int)Math.Round(Math.Clamp(rgb[0], 0f, 1f) * 255);
        int G = (int)Math.Round(Math.Clamp(rgb[1], 0f, 1f) * 255);
        int B = (int)Math.Round(Math.Clamp(rgb[2], 0f, 1f) * 255);
        return $"{R:X2}{G:X2}{B:X2}";
    }

    private static int Worn(IReadOnlyDictionary<int, int> outfit)
    {
        var n = 0;
        foreach (var kv in outfit)
        {
            if (kv.Value != 0) n++;
        }
        return n;
    }
}
