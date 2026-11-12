using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// Overlay partial: the hover-driven 3D preview (show / hide, the dye source chosen for it, and the outfit name +
/// "N pieces · N hidden · weapon skin" lines under the model).
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
        // The outfit's OWN show/hide map goes along (framework 2.31.0, IWardrobePreviewOptions): the framework dresses
        // the model by it — so a piece the player hides globally right now is still shown when this outfit shows it.
        // An outfit saved before the show/hide capture has Hidden == null = the player's current setting (as before).
        // ShowWeapon is ONE switch shared by every plugin using the preview service, so it is re-asserted before
        // every Show, never trusted to still hold this plugin's value.
        var currentProfessionId = _services.Wardrobe.GetWornWeaponSkin()?.ProfessionId ?? 0;
        var previewOutfit = WardrobeRules.PreviewOutfit(slot, currentProfessionId);
        _services.WardrobePreview.ShowWeapon = _previewWeapon;
        _services.WardrobePreview.Show(_services.CombatSnapshot.LocalEntityId, previewOutfit, BuildPreviewDyes(slot),
            slot.Hidden);
        DiagPreview(slot, previewOutfit);
        _previewSlot = slot;
        RefreshPreviewInfo();
        _window?.MarkDirty();
    }

    // Outfit name + "7 pieces · 2 hidden · weapon skin" for the previewed outfit (also re-run after a rename / update
    // of it). "N hidden" only for an outfit with a saved show/hide state that hides at least one of its pieces. Name and
    // details sit on two full-width fixed-height lines (each ellipsized), so neither can squeeze the other.
    private void RefreshPreviewInfo()
    {
        if (_previewSlot is not { } slot) { _previewName = _previewInfo = ""; return; }
        int n = Worn(slot.Regions);
        var info = n == 1 ? _loc.T("wardrobe.preview.pieceOne") : _loc.TFormat("wardrobe.preview.pieces", n);
        int hidden = WardrobeRules.CountHiddenPieces(slot);
        if (hidden > 0)
            info += " · " + (hidden == 1 ? _loc.T("wardrobe.preview.hiddenOne") : _loc.TFormat("wardrobe.preview.hidden", hidden));
        if (slot.HasWeaponSkin()) info += " · " + _loc.T("wardrobe.preview.weapon");
        _previewName = slot.Name;
        _previewInfo = info;
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
