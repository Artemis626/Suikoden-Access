using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// GSD2 NPC DATA PROBE — F4, Suikoden 2 field. Research, 2026-09-29 (handoff §17).
//
// Question: what does the game itself know about the people on a map, beyond
// the live EVENT_HUMAN rows (position, type, visibility)? Names above all: an
// entity carries no character number, so EventHumanNameGet(pno) cannot be
// asked about it directly (§15). The static decompile names every place the
// answer could live; this dump reads them so they can be joined offline.
//
//   ehuman                 every live slot, every scalar field
//   mdat.epersondat[]      the map's person SPRITES: fp_no, pno (a character
//                          number!), kao_csr. pno is what EventHumanNameGet
//                          wants, so each distinct pno is also asked for its
//                          name (a value-parameter call, as in v66).
//   mdat.eventdata         mapeventdat[] per map: eventobj[] (otyp, disp, fpno,
//                          x, y, w, h - the INITIAL state of the people, the same
//                          fields as EVENT_HUMAN, so a row should join a slot) and
//                          eventdat[] (etyp + the eventForm / eventCom script
//                          bytes); sce_msg[] (message index lists)
//   EVENTCON.nameID        the speaker of the dialogue line on screen, if any
//   DOORS                  lead 5.7, carried over from the retired U probe:
//                          MAP.tokusyu_atari (special zones), EVENTCON's
//                          doorStateList, and the HD MapInfoHD.eventObjects.
//                          The camp and tent have none; a town is needed.
//   TextMasterData         every loaded text table (stayTextSet), id -> text, so
//                          a speaker nameID can be found by searching for a name
//                          heard in dialogue ("soldier role 3").
//
// Pure reads, plus EventHumanNameGet. One press writes suikoden2_npcs_<map>.txt.
// Dialogue lines in Suikoden 2 are also logged with EVENTCON.nameID and the
// nearest displayed slot (NoteDialogue), which pairs name IDs with names heard.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd2Npc
{
    private const BindingFlags ANY = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags ANYS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private static readonly string[] HumanFields =
    {
        "otyp", "disp", "rgb", "spd", "wt", "objkan", "ano", "eano", "ityp", "tdat", "hrat",
        "ex", "ey", "osta", "fpno", "ozok", "pri", "sx", "sy", "w", "h", "kyori",
    };
    private static readonly string[] ObjFields =
        { "otyp", "disp", "spd", "wt", "ano", "ityp", "fpno", "ozok", "pri", "x", "y", "w", "h" };

    internal static void Run(object ec)
    {
        if (ec == null) { Plugin.Logger.LogInfo("[Npc] F4: no EVENTCON captured - Suikoden 2 field only."); return; }
        string map = Gsd2Walk.CurrentMapKey ?? "unknown";
        var sb = new StringBuilder();
        sb.AppendLine("# CONTAINS THE GAME'S OWN TEXT (Konami's copyright). For local analysis only:");
        sb.AppendLine("# keep it in the ground-truth folder, never share it, never commit it.");
        sb.AppendLine($"# Suikoden 2 NPC data, map {map}, mno={Get(ec, "mno")}, player px=({Get(ec, "px")},{Get(ec, "py")}), " +
                      $"{TitleProbe.Version}, t={Time.realtimeSinceStartup:F1}");
        sb.AppendLine($"# EVENTCON.nameID={Get(ec, "nameID")} isQQQName={UiText.Member(ec, "isQQQName")}");

        int texts = 0;
        Section(sb, "EHUMAN", () => Humans(sb, ec));
        Section(sb, "EPERSONDAT", () => Persons(sb, ec));
        Section(sb, "EVENTDATA", () => Events(sb, ec));
        Section(sb, "DOORS", () => Doors(sb, ec));
        Section(sb, "TEXT", () => texts = Texts(sb));

        try
        {
            var dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var path = System.IO.Path.Combine(dir, $"suikoden2_npcs_{Gsd2Walk.SafeMapKey}.txt");
            System.IO.File.WriteAllText(path, sb.ToString());
            Plugin.Logger.LogInfo($"[Npc] F4: wrote {sb.Length} chars ({texts} text entries) to {path}");
            Narration.Label("NPC data written.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Npc] F4 write failed: {e.Message}"); }
    }

    // Each section stands alone: a failure is written into the file, and the
    // sections after it still run.
    private static void Section(StringBuilder sb, string name, Action body)
    {
        sb.AppendLine().AppendLine($"=== {name}");
        try { body(); }
        catch (Exception e) { sb.AppendLine($"  FAILED: {e.GetType().Name}: {e.Message}"); }
    }

    private static void Humans(StringBuilder sb, object ec)
    {
        var eh = UiText.Member(ec, "ehuman");
        int n = NavProbeShared.CountOf(eh);
        sb.AppendLine($"  count={n}");
        for (int i = 0; i < n; i++)
        {
            var h = UiText.Element(eh, i);
            if (h == null) { sb.AppendLine($"  [{i}] null"); continue; }
            var line = new StringBuilder($"  [{i}] px=({PosPx(UiText.Member(h, "x"))},{PosPx(UiText.Member(h, "y"))})");
            foreach (var f in HumanFields) line.Append($" {f}={Get(h, f)}");
            sb.AppendLine(line.ToString());
        }
    }

    private static void Persons(StringBuilder sb, object ec)
    {
        var list = UiText.Member(UiText.Member(ec, "mdat"), "epersondat");
        int n = NavProbeShared.CountOf(list);
        sb.AppendLine($"  count={n}");
        var named = new Dictionary<int, string>();
        for (int i = 0; i < n; i++)
        {
            var p = UiText.Element(list, i);
            if (p == null) { sb.AppendLine($"  [{i}] null"); continue; }
            int pno = Get(p, "pno");
            if (!named.ContainsKey(pno)) named[pno] = NameOf(ec, pno);
            sb.AppendLine($"  [{i}] fp_no={Get(p, "fp_no")} pno={pno} name='{named[pno]}' kao_csr={Hex(UiText.Member(p, "kao_csr"))}");
        }
    }

    private static MethodInfo _nameGet;

    private static string NameOf(object ec, int pno)
    {
        try
        {
            _nameGet ??= FindMethod(ec.GetType(), "EventHumanNameGet", 1);
            return _nameGet?.Invoke(ec, new object[] { pno }) as string ?? "(null)";
        }
        catch (Exception e) { return $"(threw {e.GetBaseException().GetType().Name})"; }
    }

    private static void Events(StringBuilder sb, object ec)
    {
        var ed = UiText.Member(UiText.Member(ec, "mdat"), "eventdata");
        var maps = UiText.Member(ed, "mapeventdat");
        int nm = NavProbeShared.CountOf(maps), mno = Get(ec, "mno");
        sb.AppendLine($"  mapeventdat count={nm} (current mno={mno})");
        for (int m = 0; m < nm; m++)
        {
            var md = UiText.Element(maps, m);
            if (md == null) continue;
            sb.AppendLine($"  -- map[{m}]{(m == mno ? " CURRENT" : "")} mapno={Get(md, "mapno")} okaz={Get(md, "okaz")} evkaz={Get(md, "evkaz")} " +
                          $"hkaz={Get(md, "hkaz")} batlno={Get(md, "batlno")} mapst={Get(md, "mapst")}");
            var objs = UiText.Member(md, "eventobj");
            int no = NavProbeShared.CountOf(objs);
            for (int k = 0; k < no; k++)
            {
                var o = UiText.Element(objs, k);
                if (o == null) continue;
                var line = new StringBuilder($"     obj[{k}]");
                foreach (var f in ObjFields) line.Append($" {f}={Get(o, f)}");
                line.Append($" px=({Get(o, "x") / 8},{Get(o, "y") / 8})");
                sb.AppendLine(line.ToString());
            }
            var evs = UiText.Member(md, "eventdat");
            int ne = NavProbeShared.CountOf(evs);
            for (int k = 0; k < ne; k++)
            {
                var e = UiText.Element(evs, k);
                if (e == null) continue;
                sb.AppendLine($"     ev[{k}] etyp={Get(e, "etyp")} form={Hex(UiText.Member(e, "eventForm"))} com={Hex(UiText.Member(e, "eventCom"))}");
            }
        }

        var sce = UiText.Member(ed, "sce_msg");
        int ns = NavProbeShared.CountOf(sce);
        sb.AppendLine($"  sce_msg count={ns}");
        for (int i = 0; i < ns; i++)
        {
            var idx = UiText.Member(UiText.Element(sce, i), "msg_index");
            int c = NavProbeShared.CountOf(idx);
            var vals = new List<string>(Math.Max(c, 0));
            for (int j = 0; j < c; j++) vals.Add(Convert.ToString(UiText.Element(idx, j)));
            sb.AppendLine($"  sce_msg[{i}] ({c}): {string.Join(" ", vals)}");
        }
    }

    private static void Doors(StringBuilder sb, object ec)
    {
        var maps = UiText.Member(UiText.Member(ec, "mdat"), "map");
        int cnt = NavProbeShared.CountOf(maps), mno = Get(ec, "mno");
        var map = UiText.Element(maps, mno >= 0 && mno < cnt ? mno : 0);
        var tok = UiText.Member(map, "tokusyu_atari");
        int n = NavProbeShared.CountOf(tok);
        sb.AppendLine($"  map[{mno}] of {cnt}: tokusyu_kazu={Get(map, "tokusyu_kazu")}, tokusyu_atari entries={n} (units as stored)");
        for (int i = 0; i < n; i++)
        {
            var e = UiText.Element(tok, i);
            sb.AppendLine($"  tok[{i}] typ={Get(e, "typ")} x={Get(e, "x")} y={Get(e, "y")} w={Get(e, "w")} h={Get(e, "h")} " +
                          $"dat0={Get(e, "dat0")} dat1={Get(e, "dat1")} dat2={Get(e, "dat2")}");
        }

        foreach (var kv in Pairs(UiText.Member(ec, "doorStateList")))
            sb.AppendLine($"  doorState[{kv.Key}] {Fields(kv.Value)}");

        var mbg = Static(Gsd.FindIn("GSD2.dll", "MapBGManager"), "m_instance");
        var info = UiText.Member(mbg, "MapInfoHD") ?? UiText.Member(mbg, "_MapInfoHD");
        var evs = UiText.Member(info, "eventObjects");
        int en = NavProbeShared.CountOf(evs);
        sb.AppendLine($"  HD MapBGManager={(mbg == null ? "null" : "live")} MapInfoHD={(info == null ? "null" : "live")} eventObjects={en}");
        for (int i = 0; i < en; i++)
        {
            var e = UiText.Element(evs, i);
            string where = "?", name = "?";
            try
            {
                var go = (e as Component)?.gameObject;
                if (go != null) { var p = go.transform.position; where = $"({p.x:F3},{p.y:F3})"; name = go.name; }
            }
            catch { }
            sb.AppendLine($"  hd[{i}] name={name} world={where} objID={UiText.Member(e, "objID")} door={UiText.Member(e, "doorObject")} " +
                          $"eventNo={UiText.Member(e, "eventNo")} eventMapNo={UiText.Member(e, "eventMapNo")}");
        }
    }

    // Every scalar field of a small game object, for structures not yet decoded.
    private static string Fields(object o)
    {
        if (o == null) return "null";
        var parts = new List<string>();
        foreach (var f in o.GetType().GetProperties(ANY))
        {
            if (f.GetIndexParameters().Length > 0) continue;
            var t = f.PropertyType;
            if (!t.IsPrimitive && !t.IsEnum) continue;
            try { parts.Add($"{f.Name}={f.GetValue(o)}"); } catch { }
        }
        return string.Join(" ", parts);
    }

    // Every loaded text table, id -> text. The game's own text, so the file stays
    // in the plugin folder and the ground-truth folder, never in the repository.
    private static int Texts(StringBuilder sb)
    {
        var tmd = Gsd.FindAnywhere("TextMasterData");
        if (tmd == null) { sb.AppendLine("  TextMasterData type not found"); return 0; }
        sb.AppendLine($"  CurrentLanguage={Static(tmd, "CurrentLanguage")} CurrentTextSetGroup={Static(tmd, "CurrentTextSetGroup")}");

        foreach (var kv in Pairs(Static(tmd, "SystemTextBaseID")))
            sb.AppendLine($"  SystemTextBaseID {kv.Key} = {kv.Value}");
        foreach (var kv in Pairs(Static(tmd, "TextBaseID")))
            sb.AppendLine($"  TextBaseID {kv.Key} = {kv.Value}");

        int total = 0;
        foreach (var set in Pairs(Static(tmd, "stayTextSet")))
        {
            var entries = new List<KeyValuePair<object, object>>(Pairs(UiText.Member(set.Value, "fileNodes")));
            if (entries.Count == 0)
            {
                var list = UiText.Member(set.Value, "textList");
                int c = NavProbeShared.CountOf(list);
                for (int i = 0; i < c; i++)
                {
                    var td = UiText.Element(list, i);
                    entries.Add(new KeyValuePair<object, object>(UiText.Member(td, "id"), UiText.Member(td, "text")));
                }
            }
            sb.AppendLine($"  -- set '{set.Key}': {entries.Count} entries");
            foreach (var e in entries)
                sb.AppendLine($"  {set.Key}#{e.Key}\t{OneLine(Convert.ToString(e.Value))}");
            total += entries.Count;
        }
        sb.AppendLine($"  total {total} text entries");
        return total;
    }

    // ── dialogue: which name ID is speaking, next to whom ───────────────────
    /// <summary>Called for every Suikoden 2 dialogue line that has a speaker.
    /// Logs EVENTCON.nameID with the nearest displayed person, so name IDs can be
    /// paired with the names heard. Pure reads.</summary>
    internal static void NoteDialogue(string speaker)
    {
        if (!NavProbe.Live) return;
        var ec = NavProbe.Player;
        try
        {
            int px = Get(ec, "px"), py = Get(ec, "py");
            var eh = UiText.Member(ec, "ehuman");
            int n = NavProbeShared.CountOf(eh), best = -1, bestD = int.MaxValue;
            for (int i = 1; i < n; i++)
            {
                var h = UiText.Element(eh, i);
                if (h == null || Get(h, "disp") != 1) continue;
                int d = Math.Max(Math.Abs(PosPx(UiText.Member(h, "x")) - px), Math.Abs(PosPx(UiText.Member(h, "y")) - py));
                if (d < bestD) { bestD = d; best = i; }
            }
            Plugin.Logger.LogInfo($"[Npc] speaker '{speaker}' nameID={Get(ec, "nameID")} isQQQName={UiText.Member(ec, "isQQQName")} " +
                                  $"on {Gsd2Walk.CurrentMapKey} at ({px},{py}); nearest displayed slot {best} at {bestD} cells");
        }
        catch { }
    }

    // ── helpers ─────────────────────────────────────────────────────────────
    private static int Get(object o, string f) => o == null ? int.MinValue : NavProbeShared.ToInt(UiText.Member(o, f));

    // EVENT_POS is 16.16 fixed point; a px cell is 8 pixels (as Gsd2Path.PosPx).
    private static int PosPx(object pos)
    {
        try
        {
            var all = UiText.Member(pos, "all");
            return all == null ? int.MinValue : (int)(Convert.ToInt64(all) / 65536 / 8);
        }
        catch { return int.MinValue; }
    }

    private static object Static(Type t, string name)
    {
        if (t == null) return null;
        try { return t.GetProperty(name, ANYS)?.GetValue(null) ?? t.GetField(name, ANYS)?.GetValue(null); }
        catch { return null; }
    }

    private static MethodInfo FindMethod(Type t, string name, int args)
    {
        for (; t != null; t = t.BaseType)
            foreach (var m in t.GetMethods(ANY | BindingFlags.DeclaredOnly))
                if (m.Name == name && m.GetParameters().Length == args) return m;
        return null;
    }

    // An Il2Cpp Dictionary, walked through its own enumerator by reflection.
    private static IEnumerable<KeyValuePair<object, object>> Pairs(object dict)
    {
        if (dict == null) yield break;
        object en;
        try { en = dict.GetType().GetMethod("GetEnumerator", Type.EmptyTypes)?.Invoke(dict, null); }
        catch { yield break; }
        if (en == null) yield break;
        var move = en.GetType().GetMethod("MoveNext", Type.EmptyTypes);
        var cur = en.GetType().GetProperty("Current");
        if (move == null || cur == null) yield break;
        for (int guard = 0; guard < 1000000; guard++)
        {
            object pair;
            try { if (!(bool)move.Invoke(en, null)) yield break; pair = cur.GetValue(en); }
            catch { yield break; }
            yield return new KeyValuePair<object, object>(UiText.Member(pair, "Key"), UiText.Member(pair, "Value"));
        }
    }

    private static string Hex(object arr)
    {
        if (arr is Il2CppStructArray<byte> a)
        {
            var sb = new StringBuilder(a.Length * 3);
            for (int i = 0; i < a.Length; i++) sb.Append(a[i].ToString("x2")).Append(i + 1 < a.Length ? " " : "");
            return $"({a.Length}) {sb}";
        }
        return arr == null ? "null" : $"({arr.GetType().Name})";
    }

    private static string OneLine(string s) => s?.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
