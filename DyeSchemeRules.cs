using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Stellar.WardrobeLoadout;

/// <summary>What happened to one piece when an outfit's saved dye was restored through the game's Dyeing Scheme.</summary>
public enum DyePieceKind
{
    /// <summary>The piece already wears the saved colours — nothing sent.</summary>
    Same,
    /// <summary>A stored Dyeing Scheme slot matched and was applied (free).</summary>
    Applied,
    /// <summary>A slot matched but the server refused the apply (non-zero code).</summary>
    Failed,
    /// <summary>No stored Dyeing Scheme slot on that piece holds the saved colours — left as it is.</summary>
    NoMatch,
    /// <summary>The Lua step for that piece raised (RPC timeout, nil table, …).</summary>
    Error,
}

/// <summary>One piece's restore result: the fashionId, what happened, the scheme slot (Applied / Failed), and the
/// server code (Failed) or error text (Error).</summary>
public readonly record struct DyePieceOutcome(int FashionId, DyePieceKind Kind, int SlotId, string? Detail);

/// <summary>The parsed end-of-run readback of one dye-scheme restore. <see cref="Status"/> is <c>done</c> (every piece
/// walked), <c>off</c> (the game's Dyeing Scheme function is not usable for this character — nothing was tried), or
/// <c>fail</c> (the run itself raised; <see cref="Detail"/> has the text).</summary>
public sealed class DyeSchemeOutcome
{
    public string Status { get; init; } = "";
    public string? Detail { get; init; }
    public List<DyePieceOutcome> Pieces { get; init; } = new();

    /// <summary>Pieces whose saved colours could NOT be put back (no matching scheme, a refused apply, or an error).</summary>
    public int Unrestored
    {
        get
        {
            var n = 0;
            foreach (var p in Pieces) if (p.Kind is DyePieceKind.NoMatch or DyePieceKind.Failed or DyePieceKind.Error) n++;
            return n;
        }
    }

    /// <summary>Owner rule 2026-11-27: ONE red toast when at least one piece could not be restored. Nothing for a run
    /// the game's function gate skipped (<c>off</c>) — that is silent, log only.</summary>
    public bool ShouldToast => Status == "done" && Unrestored > 0;

    /// <summary>The log body: <c>fid=7015150 -> same, fid=7037026 -> slot 2 ok, fid=… -> no match</c>.</summary>
    public string Summary()
    {
        if (Status == "off") return "skipped (Dyeing Scheme not usable here)";
        if (Status == "fail") return "failed: " + (Detail ?? "?");
        if (Pieces.Count == 0) return "no pieces";
        var sb = new StringBuilder(Pieces.Count * 28);
        for (var i = 0; i < Pieces.Count; i++)
        {
            var p = Pieces[i];
            if (i > 0) sb.Append(", ");
            sb.Append("fid=").Append(p.FashionId.ToString(CultureInfo.InvariantCulture)).Append(" -> ");
            switch (p.Kind)
            {
                case DyePieceKind.Same: sb.Append("same"); break;
                case DyePieceKind.Applied: sb.Append("slot ").Append(p.SlotId.ToString(CultureInfo.InvariantCulture)).Append(" ok"); break;
                case DyePieceKind.Failed:
                    sb.Append("slot ").Append(p.SlotId.ToString(CultureInfo.InvariantCulture)).Append(" err ").Append(p.Detail); break;
                case DyePieceKind.NoMatch: sb.Append("no match"); break;
                default: sb.Append("error ").Append(p.Detail); break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Pure decisions + string formats for restoring an outfit's EXACT dye through the game's S4 "Dyeing Scheme" (Knowledge
/// Base <c>Fashion-Dye-Areas.md</c> "Dyeing Scheme"). Pure BCL so the test project compiles it directly. Every Lua
/// literal it builds carries only integers we parsed or control, formatted invariant-culture — nothing user-typed (the
/// outfit name never goes into Lua).
/// </summary>
public static class DyeSchemeRules
{
    /// <summary>Prefix the Lua colour read parks in front of its payload, so an all-failed read (global nil) is told
    /// apart from a read that found pieces.</summary>
    public const string ReadOkPrefix = "ok|";

    /// <summary>Whether the outfit's saved dye should be restored after the outfit switch resolved. Same gate as the
    /// weapon skin and show/hide: only after a SUCCESSFUL switch, and only when the outfit recorded exact colours (null =
    /// saved before this feature → old behaviour, colours left alone; owner ruling 2026-11-27: never match from the
    /// approximate RGB <c>DyeAreas</c>).</summary>
    public static bool ShouldSendDyeSchemes(bool outfitSucceeded, OutfitSlot slot)
        => outfitSucceeded && slot.DyeColors is { Count: > 0 };

    /// <summary>The worn pieces as a Lua array literal <c>{7015150,7037026}</c> (distinct non-zero fashionIds, ascending),
    /// or null when nothing is worn.</summary>
    public static string? FormatFidList(IReadOnlyDictionary<int, int> regions)
    {
        var ids = new SortedSet<int>();
        foreach (var kv in regions) if (kv.Value > 0) ids.Add(kv.Value);
        if (ids.Count == 0) return null;
        var sb = new StringBuilder(ids.Count * 11 + 2).Append('{');
        var first = true;
        foreach (var id in ids)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(id.ToString(CultureInfo.InvariantCulture));
        }
        return sb.Append('}').ToString();
    }

    /// <summary>Decode the colour read: <c>ok|fid:area=h,s,v/area=h,s,v;fid:;…</c> (<c>fid:</c> with no areas = the piece
    /// wears its default colours — <c>GetServerFashionAllColor</c> returned an empty table). A malformed piece is dropped
    /// (best effort, the rest still saves); numbers must be integral (the game's HSV are IntVec3 ints). Null when the read
    /// failed (no prefix) or yielded no piece at all — the save then records no exact dye.</summary>
    public static Dictionary<int, Dictionary<int, int[]>>? ParseColors(string? readback)
    {
        if (readback is null || !readback.StartsWith(ReadOkPrefix, StringComparison.Ordinal)) return null;
        var result = new Dictionary<int, Dictionary<int, int[]>>();
        foreach (var piece in readback.Substring(ReadOkPrefix.Length).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = piece.IndexOf(':');
            if (colon <= 0 || !TryInt(piece.Substring(0, colon), out var fid) || fid <= 0) continue;
            var areas = new Dictionary<int, int[]>();
            var ok = true;
            foreach (var entry in piece.Substring(colon + 1).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = entry.IndexOf('=');
                var hsv = eq > 0 ? entry.Substring(eq + 1).Split(',') : Array.Empty<string>();
                if (eq <= 0 || hsv.Length != 3 || !TryInt(entry.Substring(0, eq), out var area)
                    || !TryInt(hsv[0], out var h) || !TryInt(hsv[1], out var s) || !TryInt(hsv[2], out var v))
                {
                    ok = false;
                    break;
                }
                areas[area] = new[] { h, s, v };
            }
            if (ok) result[fid] = areas;
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>The Lua literal the restore run walks, pieces in ascending fashionId order:
    /// <c>{{7015150,{[1]={h=120,s=50,v=60},[2]={h=0,s=0,v=100}}},{7037026,{}}}</c> — each colour map in the exact shape
    /// <c>GetServerFashionAllColor</c> returns, so it goes straight into <c>GetSameStorePlanColorData</c>. A null map or a
    /// colour that is not three ints is skipped. Null when nothing is left.</summary>
    public static string? FormatPieces(IReadOnlyDictionary<int, Dictionary<int, int[]>> colors)
    {
        var fids = new List<int>(colors.Count);
        foreach (var kv in colors) if (kv.Key > 0 && kv.Value is not null) fids.Add(kv.Key);
        if (fids.Count == 0) return null;
        fids.Sort();
        var sb = new StringBuilder(fids.Count * 64).Append('{');
        for (var i = 0; i < fids.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('{').Append(fids[i].ToString(CultureInfo.InvariantCulture)).Append(",{");
            var areas = new List<int>(colors[fids[i]].Keys);
            areas.Sort();
            var first = true;
            foreach (var area in areas)
            {
                var c = colors[fids[i]][area];
                if (c is not { Length: 3 }) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('[').Append(area.ToString(CultureInfo.InvariantCulture)).Append("]={h=")
                  .Append(c[0].ToString(CultureInfo.InvariantCulture)).Append(",s=")
                  .Append(c[1].ToString(CultureInfo.InvariantCulture)).Append(",v=")
                  .Append(c[2].ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            sb.Append("}}");
        }
        return sb.Append('}').ToString();
    }

    /// <summary>Decode the restore run's result global: <c>&lt;seq&gt;|done|fid=same;fid=ok:2;fid=err:2:3413;fid=none;fid=x:msg</c>,
    /// <c>&lt;seq&gt;|off|</c>, or <c>&lt;seq&gt;|fail|msg</c>. Null while the run is still going (global nil), or when the
    /// value belongs to another run (<paramref name="seq"/> mismatch) — the caller keeps polling until its timeout.</summary>
    public static DyeSchemeOutcome? ParseResult(string? raw, int seq)
    {
        if (raw is null) return null;
        var p1 = raw.IndexOf('|');
        if (p1 <= 0 || !TryInt(raw.Substring(0, p1), out var s) || s != seq) return null;
        var p2 = raw.IndexOf('|', p1 + 1);
        var status = p2 < 0 ? raw.Substring(p1 + 1) : raw.Substring(p1 + 1, p2 - p1 - 1);
        var rest = p2 < 0 ? "" : raw.Substring(p2 + 1);
        if (status != "done") return new DyeSchemeOutcome { Status = status, Detail = rest.Length == 0 ? null : rest };

        var pieces = new List<DyePieceOutcome>();
        foreach (var entry in rest.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0 || !TryInt(entry.Substring(0, eq), out var fid)) continue;
            var parts = entry.Substring(eq + 1).Split(':', 3);
            switch (parts[0])
            {
                case "same": pieces.Add(new(fid, DyePieceKind.Same, 0, null)); break;
                case "none": pieces.Add(new(fid, DyePieceKind.NoMatch, 0, null)); break;
                case "ok" when parts.Length >= 2 && TryInt(parts[1], out var okSlot):
                    pieces.Add(new(fid, DyePieceKind.Applied, okSlot, null)); break;
                case "err" when parts.Length >= 3 && TryInt(parts[1], out var errSlot):
                    pieces.Add(new(fid, DyePieceKind.Failed, errSlot, parts[2])); break;
                default:
                    pieces.Add(new(fid, DyePieceKind.Error, 0, parts.Length > 1 ? entry.Substring(eq + 1 + parts[0].Length + 1) : parts[0]));
                    break;
            }
        }
        return new DyeSchemeOutcome { Status = status, Pieces = pieces };
    }

    // Lua may hand back an integral number as "120" or (Lua 5.3 floats) "120.0"; accept either, reject anything
    // non-integral — a rounded colour would never compare equal to the game's own ints.
    private static bool TryInt(string s, out int value)
    {
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue)
        {
            value = (int)d;
            return true;
        }
        value = 0;
        return false;
    }
}
