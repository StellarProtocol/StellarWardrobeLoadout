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
/// <para>An outfit also carries the WEAPON SKIN the player's class was using when it was saved (a
/// separate per-class game system — <see cref="IWardrobe.GetWornWeaponSkin"/> /
/// <see cref="IWardrobe.ApplyWeaponSkinAsync"/>, the Wardrobe's Weapon Skin tab). Applying an outfit
/// re-applies that skin AFTER the outfit switch succeeds (both share the game's one-apply-in-flight
/// slot); an outfit saved before 1.1.0 carries none and leaves the weapon skin alone.</para>
///
/// <para>Outfits are stored per character (keyed by character name) in the plugin's OWN data store —
/// <c>stellar/plugindata/stellar.wardrobeloadout.data/outfits.json</c> via
/// <see cref="IPluginDataStore"/> and <see cref="OutfitPersistence"/> — NOT in the shared config file
/// (owner ruling 2026-09-05: user data belongs in plugindata, settings stay in config). Outfits saved
/// by an earlier build are migrated out of the legacy <c>wardrobe.outfits</c> config key on first
/// construct; that key is then LEFT IN PLACE, frozen and never rewritten, so rolling back to a
/// pre-migration build still finds them (process rules § 6). Plugindata always wins on a later return
/// to this build. Apply success/failure is toasted by the GAME itself; this plugin toasts only its own
/// guard messages (API-not-ready, empty slot, switch-in-flight, nothing-to-save, weapon-skin-failed)
/// and logs outcomes.</para>
/// </summary>
public sealed partial class Plugin : IStellarPlugin
{
    private const int HotkeySlotCount = 8;

    // LEGACY config key (section "wardrobe"). Read once at construct as a migration source; never
    // written again — see OutfitPersistence for the rollback contract.
    private const string LegacyOutfitsKey = "outfits";

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
        _store = new WardrobeStore(LoadOutfits());
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

    // A stable per-character key: the game's char id. Null until it resolves (title / character select /
    // just after logout) — every store op is a no-op under a null key (never write outfits under an
    // unknown character; the old name/"default" fallback is gone — it collided across accounts).
    private string? CharacterKey => _services.PlayerState.CharId != 0 ? _services.PlayerState.CharId.ToString() : null;

    // Latches once the one-time name→char-id migration has been attempted for this plugin session (see
    // TryMigrateNameToCharId). Ticked from the overlay's per-frame preview hook — the only per-frame hook
    // this plugin already has — so it fires as soon as the id first resolves without a dedicated poll.
    private bool _migratedThisSession;

    // Runs once per session, the first time the char id resolves: copies any outfits still sitting under
    // the legacy name key (or "default") onto the char-id key, keeping the legacy key for rollback (see
    // WardrobeStore.MigrateNameToCharId). Cheap to call every frame before that: short-circuits on
    // _migratedThisSession, backed by the store's own idempotency so a re-attempt after a character
    // switch within the same session can never duplicate an already-migrated char-id entry.
    private void TryMigrateNameToCharId()
    {
        if (_migratedThisSession) return;
        if (CharacterKey is not { } key) return;
        _migratedThisSession = true;
        if (_store.MigrateNameToCharId(_services.PlayerState.Name ?? "", key))
        {
            Persist();
            _services.Log.Info($"[WardrobeLoadout] migrated name-keyed outfits onto char id {key}");
        }
    }

    // Every store op (apply/save/rename/update/move/delete) resolves its key through here: the resolved
    // char id, or null (with a diagnostic naming the op) when it hasn't resolved yet. Centralizes the
    // "never write outfits under an unknown character" gate so each call site stays a one-liner.
    private string? ResolveKeyOrSkip(string op)
    {
        if (CharacterKey is { } key) return key;
        DiagStoreSkipped(op);
        return null;
    }

    // Hotkey n → the n-th saved outfit for the current character (slot position, 1-based).
    private void OnApply(int slotNumber)
    {
        if (!_services.Wardrobe.IsAvailable)
        {
            DiagSkipped(slotNumber, "wardrobe API unavailable");
            Toast(NoticeTipType.RedBar, _loc.T("wardrobe.toast.apiNotReady"));
            return;
        }

        if (ResolveKeyOrSkip("apply") is not { } key) return;

        var slots = _store.Get(key);
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

    // Outfit first, THEN (only on success) the saved weapon skin. The two share the game's single
    // apply-in-flight slot, so the outfit switch must be awaited before the skin RPC goes out; a failed
    // outfit switch sends no skin at all (the player keeps the look they had).
    private async Task ApplyAndReportAsync(OutfitSlot slot)
    {
        try
        {
            var result = await _services.Wardrobe.ApplyAsync(slot.Regions).ConfigureAwait(false);
            WardrobeResult? weapon = null;
            if (WardrobeRules.ShouldSendWeaponSkin(result == WardrobeResult.Success, slot))
            {
                weapon = await _services.Wardrobe
                    .ApplyWeaponSkinAsync(slot.WeaponProfessionId, slot.WeaponSkinId).ConfigureAwait(false);
            }
            Report(slot, result, weapon);
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

    // One always-on outcome line carrying BOTH results, then the toasts. weapon is null when the outfit
    // failed (nothing was sent) or the outfit carries no saved skin — the log says which.
    private void Report(OutfitSlot slot, WardrobeResult result, WardrobeResult? weapon)
    {
        var weaponNote = weapon is { } w ? w.ToString()
            : slot.HasWeaponSkin() ? "not sent" : "none";
        _services.Log.Info(
            $"[WardrobeLoadout] apply '{slot.Name}' -> {result} (weapon skin {slot.WeaponProfessionId}/{slot.WeaponSkinId} -> {weaponNote})");
        if (result == WardrobeResult.Success)
        {
            Toast(NoticeTipType.GreenBar, _loc.TFormat("wardrobe.toast.applied", slot.Name));
            if (weapon is { } wr && wr != WardrobeResult.Success)
            {
                Toast(NoticeTipType.RedBar, _loc.TFormat("wardrobe.toast.weaponFailed", slot.Name));
            }
        }
        else if (result is WardrobeResult.Rejected or WardrobeResult.InCombat or WardrobeResult.Timeout)
        {
            // The game toasts the specific reason (combat lock, etc.) itself; this is the plugin's
            // own coarse failure note so a no-op click isn't silent.
            Toast(NoticeTipType.RedBar, _loc.TFormat("wardrobe.toast.applyFailed", slot.Name));
        }
    }

    // Everything a saved outfit captures, in one read: the worn pieces, their per-area dyes, and the
    // class's weapon skin. Returns null when there is nothing to save (wardrobe read not ready, or an
    // all-zero worn map) — the ONE definition of "empty capture", shared by save and update. The name is
    // the caller's business (a new save names it; an update keeps the existing name).
    private OutfitSlot? CaptureCurrent()
    {
        var worn = _services.Wardrobe.GetWornOutfit();
        if (worn is null || AllEmpty(worn)) return null;

        // No weapon skin readable → 0/0, i.e. "this outfit carries none" (applying it leaves the weapon
        // skin alone) rather than "reset to default" — never touch what we could not read.
        var weapon = _services.Wardrobe.GetWornWeaponSkin();
        return new OutfitSlot
        {
            Regions = new Dictionary<int, int>(worn),
            DyeAreas = CaptureDyeAreas(),
            WeaponProfessionId = weapon?.ProfessionId ?? 0,
            WeaponSkinId = weapon?.SkinId ?? 0,
            SavedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    // Capture the currently worn outfit into a new named slot for the current character. Refuses when
    // the wardrobe read isn't ready or nothing is worn (an all-zero map).
    private bool SaveCurrentOutfit()
    {
        if (CaptureCurrent() is not { } captured)
        {
            Toast(NoticeTipType.RedBar, _loc.T("wardrobe.toast.captureEmpty"));
            return false;
        }

        if (ResolveKeyOrSkip("save") is not { } key) return false;

        captured.Name = _loc.TFormat("wardrobe.window.defaultName", _store.Get(key).Count + 1);
        _store.Add(key, captured);
        Persist();
        DiagSaved(captured, key);
        Toast(NoticeTipType.GreenBar, _loc.TFormat("wardrobe.toast.saved", captured.Name));
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

    // The saved outfits, from the plugin's own data store — falling back ONCE to the legacy config key
    // when this is the first run after the move (owner ruling 2026-09-05). The config copy is only ever
    // READ here; Persist() writes plugindata alone, so the frozen key keeps whatever a rolled-back build
    // would need. Bytes we cannot parse are parked, never dropped.
    private Dictionary<string, List<OutfitSlot>> LoadOutfits()
    {
        var stored = _services.Data.Read(OutfitPersistence.FileName);
        var fromData = OutfitPersistence.Deserialize(stored, out var corrupt);
        if (corrupt && stored is not null)
        {
            _services.Data.Write(OutfitPersistence.CorruptFileName, stored);
            _services.Log.Warning(
                $"[WardrobeLoadout] {OutfitPersistence.FileName} could not be parsed — parked as {OutfitPersistence.CorruptFileName}; falling back to the config copy");
        }

        var legacy = _cfg.Get<Dictionary<string, List<OutfitSlot>>>(LegacyOutfitsKey, null);
        switch (OutfitPersistence.Decide(stored is not null && !corrupt, OutfitPersistence.HasOutfits(legacy)))
        {
            case MigrationDecision.UsePluginData:
                return fromData;

            case MigrationDecision.MigrateFromConfig:
            {
                var migrated = OutfitPersistence.Normalize(legacy);
                _services.Data.Write(OutfitPersistence.FileName, OutfitPersistence.Serialize(migrated));
                _services.Log.Info(
                    $"[WardrobeLoadout] migrated {OutfitPersistence.CountOutfits(migrated)} outfits for {migrated.Count} characters from config to plugindata "
                    + $"(config key 'wardrobe.{LegacyOutfitsKey}' left in place, frozen, so a rollback still finds them)");
                return migrated;
            }

            default:
                return new Dictionary<string, List<OutfitSlot>>();
        }
    }

    private void Persist()
        => _services.Data.Write(OutfitPersistence.FileName, OutfitPersistence.Serialize(_store.Root));

    private void Toast(NoticeTipType type, string content)
        => _services.NoticeTips.Create(type).WithContent(content).Show();
}
