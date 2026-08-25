using System;
using System.Collections.Generic;
using System.Text;
using Stellar.Abstractions.Diagnostics;

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
        _services.Log.Info($"[WardrobeLoadout] apply '{slot.Name}' ({Worn(slot.Regions)} worn)");
    }

    private void DiagSkipped(int slotNumber, string reason)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[WardrobeLoadout] apply slot {slotNumber} skipped: {reason}");
    }

    private void DiagSaved(string name, IReadOnlyDictionary<int, int> worn)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[WardrobeLoadout] saved '{name}' ({Worn(worn)} worn) for {CharacterKey}");
    }

    private void DiagUpdated(string name, IReadOnlyDictionary<int, int> worn)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[WardrobeLoadout] updated '{name}' -> current outfit ({Worn(worn)} worn) for {CharacterKey}");
    }

    // Log the per-area dyes chosen for a preview as region:{area=RRGGBB …} so a test can confirm each
    // colour landed on its real EFashionColorAreaType area (Base1-4=1-4, Socks1-4=5-8, BaseEx=9-12,
    // UnderWear=13-16) — the hex matches what the Entity Inspector shows for the worn piece.
    private void DiagPreview(OutfitSlot slot)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var sb = new StringBuilder();
        foreach (var reg in slot.DyeAreas)
        {
            sb.Append(reg.Key).Append(":{");
            foreach (var a in reg.Value) sb.Append(a.Key).Append('=').Append(Hex(a.Value)).Append(' ');
            sb.Append("} ");
        }
        _services.Log.Info($"[WardrobeLoadout] preview '{slot.Name}' dyeAreas={sb}(legacyFlat={slot.Dyes.Count})");
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
