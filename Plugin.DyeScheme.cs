using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// Restores an outfit's EXACT dye on wear through the game's S4 "Dyeing Scheme" (Knowledge Base
/// <c>Fashion-Dye-Areas.md</c> "Dyeing Scheme"). The game stores 2–5 colour slots per PIECE and has no "active scheme"
/// id — it matches by colour — so the outfit records each worn piece's colours at save / update
/// (<c>fashion_data:GetServerFashionAllColor</c>, <see cref="OutfitSlot.DyeColors"/>) and, after a successful wear, the
/// plugin asks the game's own matcher (<c>FashionVM.GetSameStorePlanColorData</c>) which stored slot holds those colours
/// and applies it with <c>WorldProxy.FashionApplyDyeScheme</c> — FREE. Plain dyeing and saving a scheme cost currency, so
/// no paid path (<c>AsyncSendFashionColor</c>, <c>FashionSaveDyeScheme</c>) is ever called: a piece without a matching
/// slot keeps its current colour and the player gets one red toast.
/// <para>Both Lua functions are compiled ONCE into <c>_G</c> (Plugin-Performance-Rules §4); each save / wear sends a
/// one-line call carrying the integer literal. The apply is async (one RPC per piece, sequential inside ONE game
/// coroutine), so its outcome is parked in a result global and picked up by the overlay's existing 10 Hz tick
/// (<see cref="TickDyeScheme"/>) — a single null check while idle, no extra timer, no per-poll allocation while the
/// global is still nil. ILua is main-thread only: every entry point here runs on the main thread.</para>
/// </summary>
public sealed partial class Plugin
{
    private const string DyeReadGlobal = "__stellar_wardrobe_dye_read";
    private const string DyeReadFn = "__stellar_wardrobe_dye_read_fn";
    private const string DyeRunFn = "__stellar_wardrobe_dye_run";
    private const string DyeResGlobal = "__stellar_wardrobe_dye_res";
    private const string DyeSeqGlobal = "__stellar_wardrobe_dye_seq";
    private const string DyeNoFn = "nofn";

    // A run is one RPC per re-dyed piece; give a slow server plenty of room before giving up on the readback (the game's
    // own coroutine keeps going either way — this only stops the poll).
    private const float DyeRunTimeoutSec = 60f;

    // Colour read: per worn fashionId, the piece's worn HSV ints per area, serialised "ok|fid:area=h,s,v/…;fid:;…"
    // (string readback is the reliable ILua path; a Lua table would not read back). The function clears the global
    // first, so a read that fails before the rawset leaves nil (= "unreadable"), never a previous read.
    private static readonly string DefineDyeReadChunk =
        "local function read(fids)\n" +
        "  local fd = Z.DataMgr.Get('fashion_data')\n" +
        "  local parts = {}\n" +
        "  for _, fid in ipairs(fids) do\n" +
        "    local c = fd:GetServerFashionAllColor(fid)\n" +
        "    if c ~= nil then\n" +
        "      local a = {}\n" +
        "      for area, hsv in pairs(c) do a[#a + 1] = tostring(area) .. '=' .. tostring(hsv.h) .. ',' .. tostring(hsv.s) .. ',' .. tostring(hsv.v) end\n" +
        "      parts[#parts + 1] = tostring(fid) .. ':' .. table.concat(a, '/')\n" +
        "    end\n" +
        "  end\n" +
        "  rawset(_G, '" + DyeReadGlobal + "', '" + DyeSchemeRules.ReadOkPrefix + "' .. table.concat(parts, ';'))\n" +
        "end\n" +
        "rawset(_G, '" + DyeReadFn + "', function(fids) rawset(_G, '" + DyeReadGlobal + "', nil); pcall(read, fids) end)";

    // Restore run. norm() is the game's own normalisation from GetSameStorePlanColorData (fashion_vm.lua ~1719): drop
    // areas equal to the piece default, encode area+HSV as one int, sort — so "already wearing it" is decided exactly
    // the way the game decides "this scheme is the current one". The game's VM wrapper ApplyFashionDyeScheme returns
    // NOTHING (and toasts per piece), so the run calls the WorldProxy RPC it wraps to get the code, then dispatches the
    // same FashionColorSave event the wrapper fires on success. A newer run bumps the seq global: an older run stops at
    // its next piece and never writes its result over the newer one's.
    private static readonly string DefineDyeRunChunk =
        "local function norm(vm, fid, colors)\n" +
        "  local list = {}\n" +
        "  for area, c in pairs(colors) do\n" +
        "    if vm.convertHSVToInt(vm.GetFashionDefaultColorByArea(fid, area)) ~= vm.convertHSVToInt(c) then\n" +
        "      list[#list + 1] = vm.getAreaColorData(area, c)\n" +
        "    end\n" +
        "  end\n" +
        "  table.sort(list)\n" +
        "  return list\n" +
        "end\n" +
        "local function piece(vm, fd, wp, fid, saved)\n" +
        "  local cur = fd:GetServerFashionAllColor(fid) or {}\n" +
        "  if vm.compareColorIntList(norm(vm, fid, cur), norm(vm, fid, saved)) then return 'same' end\n" +
        "  local slot = vm.GetSameStorePlanColorData(fid, saved, vm.GetStorePlanColorData(fid))\n" +
        "  if not slot then return 'none' end\n" +
        "  local ret = wp.FashionApplyDyeScheme({fashionId = fid, slotId = slot}, fd.CancelSource:CreateToken())\n" +
        "  if ret ~= 0 then return 'err:' .. tostring(slot) .. ':' .. tostring(ret) end\n" +
        "  Z.EventMgr:Dispatch(Z.ConstValue.FashionColorSave, fid)\n" +
        "  return 'ok:' .. tostring(slot)\n" +
        "end\n" +
        "local function clean(s) return (string.gsub(tostring(s), '[;|]', ' ')) end\n" +
        "rawset(_G, '" + DyeRunFn + "', function(seq, pieces)\n" +
        "  rawset(_G, '" + DyeSeqGlobal + "', seq)\n" +
        "  local function finish(status, body)\n" +
        "    if rawget(_G, '" + DyeSeqGlobal + "') == seq then rawset(_G, '" + DyeResGlobal + "', tostring(seq) .. '|' .. status .. '|' .. body) end\n" +
        "  end\n" +
        "  local ok, err = pcall(function()\n" +
        "    if not Z.VMMgr.GetVM('gotofunc').CheckFuncCanUse(E.FunctionID.FashionStorePlan, true) then finish('off', ''); return end\n" +
        "    Z.CoroUtil.create_coro_xpcall(function()\n" +
        "      local vm = Z.VMMgr.GetVM('fashion'); local fd = Z.DataMgr.Get('fashion_data'); local wp = require('zproxy.world_proxy')\n" +
        "      local out = {}\n" +
        "      for _, p in ipairs(pieces) do\n" +
        "        if rawget(_G, '" + DyeSeqGlobal + "') ~= seq then return end\n" +
        "        local pok, r = pcall(piece, vm, fd, wp, p[1], p[2])\n" +
        "        out[#out + 1] = tostring(p[1]) .. '=' .. (pok and r or ('x:' .. clean(r)))\n" +
        "      end\n" +
        "      finish('done', table.concat(out, ';'))\n" +
        "    end, function(e) finish('fail', clean(e)) end)()\n" +
        "  end)\n" +
        "  if not ok then finish('fail', clean(err)) end\n" +
        "end)";

    // The run in flight (null = idle — the tick then costs one null check), and the last seq handed to Lua.
    private sealed class DyeRun
    {
        public int Seq;
        public string Name = "";
        public float Deadline;
    }

    private DyeRun? _dyeRun;
    private int _dyeSeq;

    // The worn pieces' exact colours for CaptureCurrent, or null when unreadable (the save then goes ahead without them
    // — wearing that outfit leaves colours alone). Main thread (a window click); only runs on save / update.
    private Dictionary<int, Dictionary<int, int[]>>? ReadDyeColors(IReadOnlyDictionary<int, int> regions)
    {
        try
        {
            var lua = _services.Lua;
            if (!lua.Ready)
            {
                _services.Log.Warning("[WardrobeLoadout] dye colours not read (Lua not ready) — saved without them");
                return null;
            }
            if (DyeSchemeRules.FormatFidList(regions) is not { } fids) return null;
            var call = "rawset(_G, '" + DyeReadGlobal + "', nil); local f = rawget(_G, '" + DyeReadFn + "'); if f then f(" + fids + ") end";
            lua.DoString(call);
            var raw = lua.ReadGlobalString(DyeReadGlobal);
            if (raw is null)
            {
                // Function missing (first use, or a Lua state reset): define once and retry.
                lua.DoString(DefineDyeReadChunk);
                lua.DoString(call);
                raw = lua.ReadGlobalString(DyeReadGlobal);
            }
            var colors = DyeSchemeRules.ParseColors(raw);
            if (colors is null) _services.Log.Warning("[WardrobeLoadout] dye colours unreadable — saved without them");
            return colors;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[WardrobeLoadout] dye colours read threw: {ex.Message} — saved without them");
            return null;
        }
    }

    // Start the restore run for a worn outfit. Posted to the main thread by ApplyAndReportAsync AFTER the outfit switch
    // and its weapon skin were awaited, and after SendHiddenState (posts run in order), so the outfit switch has left
    // the game's one-switch-in-flight slot before any dye RPC goes out.
    private void SendDyeSchemes(string outfitName, string pieces)
    {
        try
        {
            var lua = _services.Lua;
            if (!lua.Ready)
            {
                _services.Log.Warning($"[WardrobeLoadout] dye scheme '{outfitName}': not sent (Lua not ready)");
                return;
            }
            if (_dyeRun is { } prev && StellarDiagnostics.IsEnabled)
                _services.Log.Info($"[WardrobeLoadout] dye scheme '{prev.Name}': superseded by '{outfitName}'");

            var seq = ++_dyeSeq;
            var seqText = seq.ToString(CultureInfo.InvariantCulture);
            var call = "rawset(_G, '" + DyeResGlobal + "', nil); local f = rawget(_G, '" + DyeRunFn + "'); " +
                       "if f then f(" + seqText + ", " + pieces + ") else rawset(_G, '" + DyeResGlobal + "', '" + DyeNoFn + "') end";
            lua.DoString(call);
            if (lua.ReadGlobalString(DyeResGlobal) == DyeNoFn)
            {
                lua.DoString(DefineDyeRunChunk);
                lua.DoString(call);
            }
            _dyeRun = new DyeRun { Seq = seq, Name = outfitName, Deadline = _services.Framework.TimeNow + DyeRunTimeoutSec };
            TickDyeScheme();   // a run with nothing to re-dye finishes synchronously: report it now, not a tick later
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[WardrobeLoadout] dye scheme '{outfitName}' threw: {ex.Message}");
        }
    }

    // From the overlay's 10 Hz tick (always, not only while the window is shown). Idle = one null check; while a run
    // is going the read returns null (no allocation) until the coroutine parks its result.
    private void TickDyeScheme()
    {
        if (_dyeRun is not { } run) return;
        string? raw = null;
        try
        {
            if (_services.Lua.Ready) raw = _services.Lua.ReadGlobalString(DyeResGlobal);
        }
        catch { /* treated as "not yet" — the timeout below still ends the poll */ }

        var outcome = DyeSchemeRules.ParseResult(raw, run.Seq);
        if (outcome is null)
        {
            if (_services.Framework.TimeNow < run.Deadline) return;
            _dyeRun = null;
            _services.Log.Warning($"[WardrobeLoadout] dye scheme '{run.Name}': no result after {DyeRunTimeoutSec:0} s");
            return;
        }
        _dyeRun = null;
        ReportDyeSchemes(run.Name, outcome);
    }

    // ALWAYS-ON outcome line (one per wear that restored dye), then the owner-approved single red toast when some piece
    // could not be restored. A run the game's function gate skipped ("off") only logs.
    private void ReportDyeSchemes(string outfitName, DyeSchemeOutcome outcome)
    {
        _services.Log.Info($"[WardrobeLoadout] dye scheme '{outfitName}': {outcome.Summary()}");
        if (outcome.ShouldToast) Toast(NoticeTipType.RedBar, _loc.TFormat("wardrobe.toast.dyeSchemeMissing", outfitName));
    }

    // The apply log's dye part: "none" (outfit saved without exact colours), "not sent" (the switch failed), or
    // "sent (N pieces)" — the per-piece result follows in its own "dye scheme" line once the run finishes.
    private static string DyeNote(OutfitSlot slot, bool outfitSucceeded)
        => slot.DyeColors is not { Count: > 0 } colors ? "none"
         : !outfitSucceeded ? "not sent"
         : $"sent ({colors.Count} pieces)";

    // "dye=none" / "dye=N pcs" for the diagnostics-gated save / update lines.
    private static string DyeDiag(OutfitSlot slot)
        => slot.DyeColors is { } colors ? $"dye={colors.Count} pcs" : "dye=none";
}
