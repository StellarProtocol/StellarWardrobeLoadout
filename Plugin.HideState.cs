using System;
using System.Collections.Generic;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// The per-outfit show/hide state (the game's wardrobe "hide hat" toggles). The game keeps ONE global string per
/// character in the synced settings map — <c>Z.ContainerMgr.CharSerialize.settingData.settingMap[201]</c>,
/// <c>"region=state|…"</c>, 2 = hidden — and has no per-outfit notion of it, so the plugin captures it with each
/// outfit and writes it back after the outfit is worn (Knowledge Base <c>Fashion-Show-Hide-State.md</c>).
/// <para>Both directions go through the framework <c>ILua</c> on the main thread. The WRITE runs only on a user action
/// (wear, hotkey), so its chunk is built per call; the READ also runs on save / replace AND is polled at ~1 Hz while the
/// window is shown and the Worn pick is ambiguous, so it is a <c>_G</c> function compiled once (Plugin-Performance-Rules §4).
/// The READ parks the raw STRING in a global (string readback is the reliable ILua path; a Lua table would not read
/// back) and the pure-C# <see cref="WardrobeRules.ParseHidden"/> decodes it. The WRITE uses the LIVE client's model
/// (validated in game 2026-11-13 — the decompiled <c>SetFashionRegionIsHide</c> does not exist live, "attempt to call
/// a nil value"): set every region in the client-side <c>fashion_data</c> dict (<c>SetFashionSetting</c>, no toast),
/// dispatch <c>FashionAttrChange / EWearSetting</c> so the local model refreshes, then
/// <c>fashion_setting.SetFashionAllRegionIsHide()</c> (async, in a coroutine) saves that dict to the server. The save
/// writes the dict AS IS, so all 14 regions are always set — a partial set would wipe the player's other hide
/// settings server-side.</para>
/// </summary>
public sealed partial class Plugin
{
    private const string HideGlobal = "__stellar_wardrobe_hide";
    private const string HideErrGlobal = "__stellar_wardrobe_hide_err";

    private const string HideReadFn = "__stellar_wardrobe_hide_read";

    // The reader is now also polled (~1 Hz, Worn ranking), so the real chunk is compiled ONCE into a _G function and
    // each read sends only the one-line call (Plugin-Performance-Rules §4). The function clears the global first, so a
    // read that fails before the rawset leaves nil (= "read failed"), never a previous read; the marker is prefixed so
    // an empty setting (nothing ever hidden — everything shown) is still a successful read.
    private static readonly string DefineHideReadChunk =
        "local function read()\n" +
        "  local s = (((Z.ContainerMgr).CharSerialize).settingData).settingMap[201]\n" +
        "  rawset(_G, '" + HideGlobal + "', '" + WardrobeRules.HideReadbackMarker + "' .. (s == nil and '' or tostring(s)))\n" +
        "end\n" +
        "rawset(_G, '" + HideReadFn + "', function() rawset(_G, '" + HideGlobal + "', nil); pcall(read) end)";

    // Also clears the global itself: if the function is gone (a Lua state reset), the read comes back nil and the
    // caller re-defines it once, instead of reading a stale value.
    private const string CallHideReadChunk =
        "rawset(_G, '" + HideGlobal + "', nil); local f = rawget(_G, '" + HideReadFn + "'); if f then f() end";

    // The raw readback (marker + setting string), or null when it failed. Main thread, Lua ready. Only the returned
    // string is allocated (both chunks are constants).
    private string? ReadHideRaw()
    {
        var lua = _services.Lua;
        lua.DoString(CallHideReadChunk);
        if (lua.ReadGlobalString(HideGlobal) is { } raw) return raw;
        lua.DoString(DefineHideReadChunk);
        lua.DoString(CallHideReadChunk);
        return lua.ReadGlobalString(HideGlobal);
    }

    // ── current show/hide, cached for the Worn ranking (WardrobeRules.PickWorn) ────────────────────────────────────
    // Read at most once per HideReadSec, only while the window is shown AND two or more saved outfits share the worn
    // pieces (RefreshWorn decides); the parse runs only when the raw string changed. After this plugin applies an
    // outfit with a saved state, the cache is seeded with that state and reads pause for HideSettleSec: the server copy
    // in settingMap[201] lags the client by ~3 s (Fashion-Show-Hide-State.md), so an immediate read would be stale.
    private const float HideReadSec = 1f;
    private const float HideSettleSec = 5f;
    private string? _hideRaw;
    private IReadOnlyDictionary<int, bool>? _hideCurrent;
    private float _nextHideRead;

    // The cached current state (refreshed if due), or null when unreadable.
    private IReadOnlyDictionary<int, bool>? CurrentHiddenState()
    {
        float now = _services.Framework.TimeNow;
        if (now < _nextHideRead) return _hideCurrent;
        _nextHideRead = now + HideReadSec;
        try
        {
            var raw = _services.Lua.Ready ? ReadHideRaw() : null;
            if (!string.Equals(raw, _hideRaw, StringComparison.Ordinal))
            {
                _hideRaw = raw;
                _hideCurrent = WardrobeRules.ParseHidden(raw);
            }
        }
        catch (Exception ex)
        {
            _hideRaw = null;
            _hideCurrent = null;
            _services.Log.Warning($"[WardrobeLoadout] show/hide state poll threw: {ex.Message}");
        }
        return _hideCurrent;
    }

    // Main thread, after a SUCCESSFUL apply: remember the outfit (Worn tie-break) and refresh the show/hide cache — the
    // outfit's own state when it carries one (the server copy lags, see above), else a fresh read on the next check.
    private void OnApplySucceeded(OutfitSlot slot)
    {
        _lastApplied = slot;
        if (slot.Hidden is { } hidden)
        {
            _hideCurrent = hidden;   // read-only use; the slot's map is never mutated in place
            _hideRaw = null;         // force a re-parse when reads resume
            _nextHideRead = _services.Framework.TimeNow + HideSettleSec;
        }
        else _nextHideRead = 0f;
        _nextWornCheck = 0f;
    }

    // The current show/hide state as an explicit region → hidden map, or null when it could not be read (the save
    // then goes ahead without one — applying that outfit will leave show/hide alone). Main thread (a window click).
    // A fresh map every call: the saved outfit keeps it.
    private Dictionary<int, bool>? ReadHiddenState()
    {
        try
        {
            var lua = _services.Lua;
            if (!lua.Ready)
            {
                _services.Log.Warning("[WardrobeLoadout] show/hide state not read (Lua not ready) — saved without it");
                return null;
            }
            var raw = ReadHideRaw();
            var hidden = WardrobeRules.ParseHidden(raw);
            if (hidden is null)
                _services.Log.Warning("[WardrobeLoadout] show/hide state unreadable — saved without it");
            return hidden;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[WardrobeLoadout] show/hide state read threw: {ex.Message} — saved without it");
            return null;
        }
    }

    // Write an outfit's saved show/hide state (all 14 regions explicit). Posted to the main thread by
    // ApplyAndReportAsync after the outfit switch (and its weapon skin) succeeded. Fire-and-forget like the game's own
    // toggle; a Lua error is parked and logged so a silent no-op stays visible in the owner's log. The server save
    // runs later inside the game's coroutine (xpcall'd by CoroUtil), so its own failure can't be read back here.
    private void SendHiddenState(string outfitName, string hideTable)
    {
        try
        {
            // Refuse anything but a full 14-region table: the live save writes the client dict as-is, and a partial
            // dict replaces the player's whole hide setting on the server.
            if (!WardrobeRules.IsCompleteHideTable(hideTable))
            {
                _services.Log.Warning($"[WardrobeLoadout] show/hide for '{outfitName}' refused (table does not set all 14 regions)");
                return;
            }
            var lua = _services.Lua;
            if (!lua.Ready)
            {
                _services.Log.Warning($"[WardrobeLoadout] show/hide for '{outfitName}' not set (Lua not ready)");
                return;
            }
            // No statement may start with "(": after a call Lua reads it as a call continuation, hence the plain
            // Z.X.Y forms and the explicit semicolons.
            lua.DoString(
                "rawset(_G, '" + HideErrGlobal + "', nil)\n" +
                "local ok, err = pcall(function()\n" +
                "  local vm = Z.VMMgr.GetVM('fashion_setting'); local fd = Z.DataMgr.Get('fashion_data');\n" +
                "  for r, h in pairs(" + hideTable + ") do fd:SetFashionSetting(r, h) end;\n" +
                "  Z.EventMgr:Dispatch(Z.ConstValue.FashionAttrChange, Z.LocalAttr.EWearSetting," +
                " vm.RegionDictToSettingStr(fd:GetFashionSettingDict()));\n" +
                "  Z.CoroUtil.create_coro_xpcall(function() vm.SetFashionAllRegionIsHide() end)();\n" +
                "end)\n" +
                "if not ok then rawset(_G, '" + HideErrGlobal + "', tostring(err)) end");
            if (lua.ReadGlobalString(HideErrGlobal) is { } err)
                _services.Log.Warning($"[WardrobeLoadout] show/hide for '{outfitName}' failed: {err}");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[WardrobeLoadout] show/hide for '{outfitName}' threw: {ex.Message}");
        }
    }

    // The apply log's show/hide part: "none" (outfit saved without a state), "not sent" (the switch failed), or
    // "set (N hidden)" — N = hidden regions, whether or not they hold a piece.
    private static string HiddenNote(OutfitSlot slot, bool outfitSucceeded)
        => slot.Hidden is not { } hidden ? "none"
         : !outfitSucceeded ? "not sent"
         : $"set ({CountTrue(hidden)} hidden)";

    // "hidden=none" / "hidden=<regions>" for the diagnostics-gated save / update lines.
    private static string HiddenDiag(OutfitSlot slot)
    {
        if (slot.Hidden is not { } hidden) return "hidden=none";
        var list = new List<int>();
        foreach (var kv in hidden) if (kv.Value) list.Add(kv.Key);
        list.Sort();
        return "hidden=[" + string.Join(",", list) + "]";
    }

    private static int CountTrue(Dictionary<int, bool> map)
    {
        var n = 0;
        foreach (var kv in map) if (kv.Value) n++;
        return n;
    }
}
