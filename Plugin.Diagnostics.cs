using System.Collections.Generic;
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
