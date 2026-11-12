using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// Overlay partial: the hover-driven 3D preview (show / hide, and the dye source chosen for it). Split out
/// of <c>Plugin.Overlay.cs</c> for the 500-LoC file gate — a pure move.
/// </summary>
public sealed partial class Plugin
{
    private void ShowPreview(int idx)
    {
        if (idx == _previewIdx) return;   // already showing this outfit
        if (RowAt(idx) is not { } slot) return;
        // The weapon skin rides the preview map under key 731 — but only when it belongs to the class we're on
        // now (owner rule 2026-09-05); a skin is per-class, so another class's cannot render on this model.
        var currentProfessionId = _services.Wardrobe.GetWornWeaponSkin()?.ProfessionId ?? 0;
        var previewOutfit = WardrobeRules.PreviewOutfit(slot, currentProfessionId);
        _services.WardrobePreview.Show(_services.CombatSnapshot.LocalEntityId, previewOutfit, BuildPreviewDyes(slot));
        DiagPreview(slot, previewOutfit);
        _previewIdx = idx;
        _window?.MarkDirty();
    }

    // Choose the dye source for a preview: exact per-area (new saves) → legacy flat treated as positional
    // areas (older saves) → live worn dyes (outfits saved before any dye capture). Returns region → area → RGB.
    private IReadOnlyDictionary<int, IReadOnlyDictionary<int, float[]>> BuildPreviewDyes(OutfitSlot slot)
    {
        if (slot.DyeAreas.Count > 0) return ToAreaMap(slot.DyeAreas);
        if (slot.Dyes.Count > 0) return FlatToAreaMap(slot.Dyes);
        return ToAreaMap(CaptureDyeAreas());
    }

    private static IReadOnlyDictionary<int, IReadOnlyDictionary<int, float[]>> ToAreaMap(Dictionary<int, Dictionary<int, float[]>> src)
    {
        var map = new Dictionary<int, IReadOnlyDictionary<int, float[]>>(src.Count);
        foreach (var kv in src) map[kv.Key] = kv.Value;
        return map;
    }

    // Legacy flat triples → positional areas 1,2,3,… (approximate — the pre-fix behaviour, kept so outfits
    // saved before per-area capture still show colour until re-saved).
    private static IReadOnlyDictionary<int, IReadOnlyDictionary<int, float[]>> FlatToAreaMap(Dictionary<int, float[]> flat)
    {
        var map = new Dictionary<int, IReadOnlyDictionary<int, float[]>>(flat.Count);
        foreach (var kv in flat)
        {
            var f = kv.Value;
            var areas = new Dictionary<int, float[]>();
            for (int i = 0, a = 1; i + 2 < f.Length; i += 3, a++) areas[a] = new[] { f[i], f[i + 1], f[i + 2] };
            map[kv.Key] = areas;
        }
        return map;
    }

    private void HidePreview()
    {
        _hoverPendingIdx = -1;
        _previewIdx = -1;
        try { _services.WardrobePreview.Hide(); } catch { /* never throw */ }
    }
}
