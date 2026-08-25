using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// Hotkey- and menu-driven wardrobe (cosmetic outfit) switcher. Captures the player's currently
/// worn outfit into a named, per-character slot and re-applies any slot through the game's own
/// fashion switch (<see cref="IWardrobe.ApplyAsync"/> → <c>WorldProxy.FashionWear</c>), which runs
/// every server-side validation — this plugin never bypasses it.
///
/// <para>Declares 8 bindable "apply outfit N" hotkeys (<c>wardrobe.apply.1</c> … <c>wardrobe.apply.8</c>,
/// no suggested defaults — the user binds them in Settings → Hotkeys) mapping to the first 8 saved
/// outfits, plus a window-toggle hotkey. The overlay (<c>Plugin.Overlay.cs</c>) holds an unlimited
/// scrollable list where outfits are saved / renamed / deleted / reordered / applied.</para>
///
/// <para>Outfits are stored per character (keyed by character name) in the plugin config
/// (<c>System.Text.Json</c> via <see cref="WardrobeStore"/>). Apply success/failure is toasted by the
/// GAME itself; this plugin toasts only its own guard messages (API-not-ready, empty slot,
/// switch-in-flight, nothing-to-save) and logs outcomes.</para>
/// </summary>
public sealed partial class Plugin : IStellarPlugin
{
    private const int HotkeySlotCount = 8;
    private const string OutfitsKey = "outfits";

    public string Name => "WardrobeLoadout";

    private readonly IPluginServices _services;
    private readonly ILocalization _loc;
    private readonly IConfigSection _cfg;
    private readonly IHotkeyAction[] _actions;
    private IHotkeyAction? _toggleAction;
    private readonly WardrobeStore _store;

    // 0 = idle, 1 = an apply is in flight (the game allows one fashion switch at a time).
    private int _inFlight;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc = services.Localization;
        _cfg = services.Config.GetSection("wardrobe");
        _store = new WardrobeStore(_cfg.Get<Dictionary<string, List<OutfitSlot>>>(OutfitsKey, new()));
        _services.Log.Info("[WardrobeLoadout] plugin constructed");

        _actions = new IHotkeyAction[HotkeySlotCount];
        for (var i = 0; i < HotkeySlotCount; i++)
        {
            var n = i + 1;   // 1-based slot number captured per action
            _actions[i] = _services.Hotkeys.DeclareAction(
                new HotkeyAction(
                    Id: $"wardrobe.apply.{n}",
                    Description: _loc.TFormat("wardrobe.hotkey.apply", n),
                    SuggestedDefault: null),
                callback: () => OnApply(n));
        }

        _toggleAction = _services.Hotkeys.DeclareAction(
            new HotkeyAction(
                Id: "wardrobe.window.toggle",
                Description: _loc.T("wardrobe.hotkey.windowToggle"),
                SuggestedDefault: null),
            callback: ToggleOverlay);

        InitOverlay();
    }

    public void Dispose()
    {
        DisposeOverlay();

        foreach (var action in _actions)
        {
            try { action.Dispose(); }
            catch { /* disposal must not throw */ }
        }
        try { _toggleAction?.Dispose(); }
        catch { /* disposal must not throw */ }
    }

    // The character the outfits are scoped to. Name is the only stable per-character identifier the
    // plugin surface exposes; "default" is the fallback before the name resolves.
    private string CharacterKey => string.IsNullOrEmpty(_services.PlayerState.Name) ? "default" : _services.PlayerState.Name!;

    // Hotkey n → the n-th saved outfit for the current character (slot position, 1-based).
    private void OnApply(int slotNumber)
    {
        if (!_services.Wardrobe.IsAvailable)
        {
            DiagSkipped(slotNumber, "wardrobe API unavailable");
            Toast(NoticeTipType.RedBar, _loc.T("wardrobe.toast.apiNotReady"));
            return;
        }

        var slots = _store.Get(CharacterKey);
        if (slotNumber - 1 >= slots.Count)
        {
            _services.Log.Info($"[WardrobeLoadout] No outfit in slot {slotNumber}");
            Toast(NoticeTipType.RedBar, _loc.TFormat("wardrobe.toast.noSlot", slotNumber));
            return;
        }

        ApplyOutfit(slots[slotNumber - 1]);
    }

    // Applies a saved outfit; rejects an overlapping switch (one server-side switch at a time).
    private void ApplyOutfit(OutfitSlot slot)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            DiagSkipped(0, "a switch is already in flight");
            Toast(NoticeTipType.RedBar, _loc.T("wardrobe.toast.inProgress"));
            return;
        }

        DiagApplying(slot);
        _ = ApplyAndReportAsync(slot);
    }

    private async Task ApplyAndReportAsync(OutfitSlot slot)
    {
        try
        {
            var result = await _services.Wardrobe.ApplyAsync(slot.Regions).ConfigureAwait(false);
            Report(slot, result);
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[WardrobeLoadout] apply '{slot.Name}' threw: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    private void Report(OutfitSlot slot, WardrobeResult result)
    {
        _services.Log.Info($"[WardrobeLoadout] apply '{slot.Name}' -> {result}");
        if (result == WardrobeResult.Success)
        {
            Toast(NoticeTipType.GreenBar, _loc.TFormat("wardrobe.toast.applied", slot.Name));
        }
        else if (result is WardrobeResult.Rejected or WardrobeResult.InCombat or WardrobeResult.Timeout)
        {
            // The game toasts the specific reason (combat lock, etc.) itself; this is the plugin's
            // own coarse failure note so a no-op click isn't silent.
            Toast(NoticeTipType.RedBar, _loc.TFormat("wardrobe.toast.applyFailed", slot.Name));
        }
    }

    // Capture the currently worn outfit into a new named slot for the current character. Refuses when
    // the wardrobe read isn't ready or nothing is worn (an all-zero map).
    private bool SaveCurrentOutfit()
    {
        var worn = _services.Wardrobe.GetWornOutfit();
        if (worn is null || AllEmpty(worn))
        {
            Toast(NoticeTipType.RedBar, _loc.T("wardrobe.toast.captureEmpty"));
            return false;
        }

        var key = CharacterKey;
        var name = _loc.TFormat("wardrobe.window.defaultName", _store.Get(key).Count + 1);
        _store.Add(key, new OutfitSlot
        {
            Name = name,
            Regions = new Dictionary<int, int>(worn),
            DyeAreas = CaptureDyeAreas(),
            SavedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        Persist();
        DiagSaved(name, worn);
        Toast(NoticeTipType.GreenBar, _loc.TFormat("wardrobe.toast.saved", name));
        return true;
    }

    // Capture the worn dye colours per region AND per area from IEntityDetail.GetFashion (the attr-201
    // broadcast the Entity Inspector reads) — region → (EFashionColorAreaType area 1..16 → RGB triple,
    // each channel 0..1). Carries each colour's real area so multi-area pieces preview correctly; when the
    // source did not supply area keys, falls back to positional areas 1,2,3,… (the pre-fix behaviour).
    // Best-effort: any failure just yields no dyes (the piece previews in its default colour).
    private Dictionary<int, Dictionary<int, float[]>> CaptureDyeAreas()
    {
        var result = new Dictionary<int, Dictionary<int, float[]>>();
        try
        {
            foreach (var fe in _services.EntityDetail.GetFashion(_services.CombatSnapshot.LocalEntityId))
            {
                if (fe.Dyes.Length == 0) continue;
                var map = new Dictionary<int, float[]>(fe.Dyes.Length);
                for (var i = 0; i < fe.Dyes.Length; i++)
                {
                    var area = i < fe.DyeAreas.Length ? fe.DyeAreas[i] : i + 1;   // positional if no area keys
                    map[area] = new[] { fe.Dyes[i].R, fe.Dyes[i].G, fe.Dyes[i].B };
                }
                result[fe.Slot] = map;
            }
        }
        catch { /* dyes are best-effort */ }
        return result;
    }

    private static bool AllEmpty(IReadOnlyDictionary<int, int> outfit)
    {
        foreach (var kv in outfit)
        {
            if (kv.Value != 0) return false;
        }
        return true;
    }

    private void Persist()
    {
        _cfg.Set(OutfitsKey, _store.Root);
        _cfg.Save();
    }

    private void Toast(NoticeTipType type, string content)
        => _services.NoticeTips.Create(type).WithContent(content).Show();
}
