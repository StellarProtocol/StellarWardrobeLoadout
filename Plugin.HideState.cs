using System;
using System.Collections.Generic;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// The per-outfit show/hide state (the game's wardrobe "hide hat" toggles). The game keeps ONE global string per
/// character in the synced settings map — <c>Z.ContainerMgr.CharSerialize.settingData.settingMap[201]</c>,
/// <c>"region=state|…"</c>, 2 = hidden — and has no per-outfit notion of it, so the plugin captures it with each
/// outfit and writes it back after the outfit is worn (Knowledge Base <c>Fashion-Show-Hide-State.md</c>).
/// <para>Both directions go through the framework <c>ILua</c> on the main thread and run only on a user action (save,
/// replace, wear, hotkey) — never per frame, so a chunk compiled per call is fine (Plugin-Performance-Rules §4).
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

    // Clear first, so a chunk that fails before the rawset leaves nil (= "read failed"), never a previous read.
    // The marker is prefixed so an empty setting (nothing ever hidden — everything shown) is still a successful read.
    private static readonly string ReadHideChunk =
        "rawset(_G, '" + HideGlobal + "', nil)\n" +
        "pcall(function()\n" +
        "  local s = (((Z.ContainerMgr).CharSerialize).settingData).settingMap[201]\n" +
        "  rawset(_G, '" + HideGlobal + "', '" + WardrobeRules.HideReadbackMarker + "' .. (s == nil and '' or tostring(s)))\n" +
        "end)";

    // The current show/hide state as an explicit region → hidden map, or null when it could not be read (the save
    // then goes ahead without one — applying that outfit will leave show/hide alone). Main thread (a window click).
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
            lua.DoString(ReadHideChunk);
            var raw = lua.ReadGlobalString(HideGlobal);
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
