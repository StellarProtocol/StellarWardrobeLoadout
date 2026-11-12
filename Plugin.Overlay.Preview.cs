using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// Overlay partial: the hover-driven 3D preview (show / hide, the dye source chosen for it, and the outfit name +
/// "N pieces · weapon skin" line under the model).
/// </summary>
public sealed partial class Plugin
{
    // The line under the model, built when the preview changes (an event), never per poll.
    private string _previewName = "";
    private string _previewInfo = "";

    private void ShowPreview(OutfitSlot slot)
    {
        if (ReferenceEquals(slot, _previewSlot)) return;   // already showing this outfit
        // The weapon skin rides the preview map under key 731 — but only when it belongs to the class we're on
        // now (owner rule 2026-09-05); a skin is per-class, so another class's cannot render on this model.
        var currentProfessionId = _services.Wardrobe.GetWornWeaponSkin()?.ProfessionId ?? 0;
        var previewOutfit = WardrobeRules.PreviewOutfit(slot, currentProfessionId);
        _services.WardrobePreview.Show(_services.CombatSnapshot.LocalEntityId, previewOutfit, BuildPreviewDyes(slot));
        DiagPreview(slot, previewOutfit);
        _previewSlot = slot;
        RefreshPreviewInfo();
        _window?.MarkDirty();
    }

    // Outfit name + "7 pieces · weapon skin" for the previewed outfit (also re-run after a rename / update of it).
    private void RefreshPreviewInfo()
    {
        if (_previewSlot is not { } slot) { _previewName = _previewInfo = ""; return; }
        int n = Worn(slot.Regions);
        var pieces = n == 1 ? _loc.T("wardrobe.preview.pieceOne") : _loc.TFormat("wardrobe.preview.pieces", n);
        _previewName = slot.Name;
        _previewInfo = slot.HasWeaponSkin() ? pieces + " · " + _loc.T("wardrobe.preview.weapon") : pieces;
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
        _hoverPending = null;
        _previewSlot = null;
        _previewName = _previewInfo = "";
        try { _services.WardrobePreview.Hide(); } catch { /* never throw */ }
    }
}
