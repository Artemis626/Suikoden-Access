using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// GSD1 DATA PROBE — read-only. Two one-shot dumps, on their own keys.
//
// WHY THIS EXISTS
// Three conclusions in the navigation status doc were reached without ever
// reading the data at runtime, and each of them is load-bearing:
//
//   * "Suikoden 1 exposes no warp data at all"      (exits must be learned)
//   * "map_event_human and map_door are empty"      (no story NPCs, no doorways)
//   * "TATEMONO carries no interactable flag, so
//      the save diary cannot be identified"         (marks are the only route)
//
// The decompile says otherwise on all three counts:
//
//   Village_c.vil_ekp  : List<EVENT_WK>  — POSITIONED map events. EVENT_WK is
//                        { ushort x, y, flag1, image, putno;
//                          byte[] eventPut, eventForm, eventCom }.
//                        eventForm/eventCom are the condition and command
//                        scripts; GSD1 ships EVENT_CHECK and EVENT_COMMAND
//                        enums for exactly those bytes, and EVENT_COMMAND has
//                        VILLAGEOUT (3) and EVENTSTAIRS (33) in it.
//   Village_c.vil_elp  : EVENT_VL { List<EVENT_WK> eventDat; int mapEventMax,
//                        mapPutMapnum, mapPutMapsub, mapPutPara, mapPutMandat }
//                        — per map, with what look like transition targets.
//   Village_c.v_work   : VILLAGE_WORK, which is where map_door ACTUALLY lives
//                        (List<DOOR> map_door + int map_door_kazu), along with
//                        List<KOMONO> komono and the treasure entry.
//
// The old door reader looked for v_work on effect_work (Village_c.effect_work
// -> v_work -> map_door). v_work is a DIRECT field of Village_c; effect_work is
// an EVENT_EFFECT and has no such member, so that read returned null every time
// and the door count was never even logged — the "empty everywhere" result was
// a null path, not a measurement. Everything here logs UNCONDITIONALLY,
// including zeroes and nulls, so absence is distinguishable from never-looked.
//
// SAFETY
// Everything in the F8 dump is a field read. No Harmony patch, no game method
// call, with one deliberate exception grouped at the very END of the dump: the
// three no-argument getters get_village_name / get_map_idx / get_area_idx. They
// are last so that if they misbehave, every other answer is already in the log.
//
// KEYS
//   F8  dump Suikoden 1 map data (events, doors, objects, people)
//   F7  dump every on-screen text label with its hierarchy path
//
// F7 exists because the save-confirmation and party-join popups are
// unread, and the existing dialogue reader only forwards labels named
// Txt_CommandNN. Pressing F7 while one of those popups is on screen names the
// components it is actually built from, which is what Milestones 3-4 needed
// before every other menu could be hooked.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd1Probe
{
    public static bool Enabled = true;

    private const BindingFlags BFI = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags BFS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private static bool _f7Down, _f8Down, _f5Down;

    public static void Tick()
    {
        if (!Enabled) return;
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            bool f7 = kb[Key.F7].isPressed;
            if (f7 && !_f7Down) DumpUiText();
            _f7Down = f7;

            bool f8 = kb[Key.F8].isPressed;
            if (f8 && !_f8Down) DumpMapData(Gsd1Nav.Village);
            _f8Down = f8;

            bool f5 = kb[Key.F5].isPressed;
            if (f5 && !_f5Down) DumpNames(Gsd1Nav.Village);
            _f5Down = f5;
        }
        catch { }
    }

    // ── logging ─────────────────────────────────────────────────────────────
    private static void L(string s) => Plugin.Logger.LogInfo("[G1Dump] " + s);

    // ── reflection helpers ──────────────────────────────────────────────────
    private static object M(object inst, string name)
    {
        try
        {
            if (inst == null) return null;
            var t = inst.GetType();
            return t.GetProperty(name, BFI)?.GetValue(inst) ?? t.GetField(name, BFI)?.GetValue(inst);
        }
        catch { return null; }
    }

    /// <summary>Read a static member. UiText.Member only looks at instance
    /// members, so Village_c.event_c (static) is invisible to it.</summary>
    private static object S(Type t, string name)
    {
        try
        {
            if (t == null) return null;
            return t.GetProperty(name, BFS)?.GetValue(null) ?? t.GetField(name, BFS)?.GetValue(null);
        }
        catch { return null; }
    }

    private static int I(object o) => NavProbeShared.ToInt(o);
    private static int Cnt(object o) => NavProbeShared.CountOf(o);
    private static object E(object list, int i) => UiText.Element(list, i);

    /// <summary>"5: 01 2a ff ..." — the first <paramref name="cap"/> bytes, with the
    /// real length in front, so a truncated dump is never mistaken for a short one.</summary>
    private static string Hex(object arr, int cap = 64)
    {
        int n = Cnt(arr);
        if (n < 0) return "null";
        if (n == 0) return "0:";
        var sb = new StringBuilder();
        sb.Append(n).Append(':');
        for (int i = 0; i < n && i < cap; i++) sb.Append(' ').Append((I(E(arr, i)) & 0xff).ToString("x2"));
        if (n > cap) sb.Append(" ...");
        return sb.ToString();
    }

    private static string Ints(object arr, int cap = 24)
    {
        int n = Cnt(arr);
        if (n < 0) return "null";
        var sb = new StringBuilder();
        sb.Append(n).Append(':');
        for (int i = 0; i < n && i < cap; i++) sb.Append(' ').Append(I(E(arr, i)));
        if (n > cap) sb.Append(" ...");
        return sb.ToString();
    }

    // ── GSD1 event opcode names, read from the game's own enums at runtime ──
    // Used only to GLOSS a byte dump: a byte in an operand position can coincide
    // with an opcode value, so the gloss is a reading aid, not a decode; the raw
    // hex beside it is the evidence. The names come from EVENT_COMMAND and
    // EVENT_CHECK in the player's installed GSD1.dll rather than a copy kept in
    // this repository (legal hygiene: no decompiled material beyond what a hook
    // needs). If an enum cannot be read, the dump simply carries no gloss.
    private static Dictionary<int, string> _com, _chk;
    private static Dictionary<int, string> Com => _com ??= EnumTable("EVENT_COMMAND");
    private static Dictionary<int, string> Chk => _chk ??= EnumTable("EVENT_CHECK");

    private static Dictionary<int, string> EnumTable(string enumName)
    {
        var table = new Dictionary<int, string>();
        try
        {
            var t = Gsd.FindIn("GSD1.dll", enumName);
            if (t == null || !t.IsEnum) { Plugin.Logger.LogInfo($"[G1Dump] {enumName} not readable; byte dumps carry no gloss."); return table; }
            foreach (var v in Enum.GetValues(t))
            {
                int k = Convert.ToInt32(v);
                if (!table.ContainsKey(k)) table[k] = Enum.GetName(t, v);
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[G1Dump] {enumName} read failed: {e.Message}"); }
        return table;
    }

    /// <summary>Names of every byte in the array that matches a table entry, in order.</summary>
    private static string Gloss(object arr, Dictionary<int, string> table, int cap = 64)
    {
        int n = Cnt(arr);
        if (n <= 0) return "";
        var seen = new List<string>();
        for (int i = 0; i < n && i < cap; i++)
        {
            int v = I(E(arr, i)) & 0xff;
            if (table.TryGetValue(v, out var name)) seen.Add(i + ":" + name);
        }
        return string.Join(" ", seen);
    }

    // ── F8: Suikoden 1 map data ─────────────────────────────────────────────
    private static void DumpMapData(object village)
    {
        if (village == null) { L("F8: no Village_c captured yet — stand on a field map and try again."); return; }
        try
        {
            L("===================== BEGIN GSD1 MAP DUMP =====================");

            var player = M(village, "player");
            int px = I(M(player, "map_x")), py = I(M(player, "map_y"));
            L($"player tile=({px},{py}) dir={M(player, "dir")} " +
              $"tatemono_flg={M(player, "tatemono_flg")} tatemono_no={M(player, "tatemono_no")} " +
              $"mapKey={Gsd1Names.MapKey(village)}");
            L($"prev_village_name='{M(village, "prev_village_name")}' " +
              $"can_auto_save={M(village, "can_auto_save")} saveMenuFilter={M(village, "saveMenuFilter")} " +
              $"miniMapFilter={M(village, "miniMapFilter")} event_exit_flg={M(village, "event_exit_flg")}");

            DumpCurrentMap(village, px, py);
            DumpVil(village);
            DumpEkp(village, px, py);
            DumpElp(village);
            DumpVWork(village, px, py);
            DumpPeople(village, px, py);
            DumpTatemono(village, px, py);
            DumpEventC(village);
            DumpShopConstants();
            DumpGetters(village);

            L("====================== END GSD1 MAP DUMP ======================");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[G1Dump] F8 failed: {e}"); }
    }

    // Village_c.mapVillage is the CURRENT map object. The old dump walked
    // vil.map_table guessing which entry the player stood on, which is why the
    // map's true dimensions were written down as unknown.
    private static void DumpCurrentMap(object village, int px, int py)
    {
        try
        {
            var map = M(village, "mapVillage");
            if (map == null) { L("mapVillage: NULL"); return; }
            L($"mapVillage: w={M(map, "width")} h={M(map, "height")} " +
              $"wNew={M(map, "width_new")} hNew={M(map, "height_new")} " +
              $"w2={M(map, "width2")} h2={M(map, "height2")} layer_no={M(map, "layer_no")} " +
              $"tatemono_kazu={M(map, "tatemono_kazu")} human_kazu={M(map, "human_kazu")} " +
              $"map_door_kazu={M(map, "map_door_kazu")}");
            L($"mapVillage arrays: atari_data={Cnt(M(map, "atari_data"))} index={Cnt(M(map, "index"))} " +
              $"index2={Cnt(M(map, "index2"))} pat_kazu={M(map, "pat_kazu")} " +
              $"tatemono_data={Cnt(M(map, "tatemono_data"))} human_data={Cnt(M(map, "human_data"))} " +
              $"hito_data={Cnt(M(map, "hito_data"))} opd_kazu={M(map, "opd_kazu")} " +
              $"opd_table={Cnt(M(map, "opd_table"))}");

            // HITO_DATA turned out to carry ONLY fp_no (a short). The status doc
            // credited it with x/y/putno/image/flag1/kakuritsu/hosuu, which are
            // fields of EVENT_WK and BATTLE_DATA — different classes. Logged so
            // that is settled from data rather than from a reading of the source.
            var hito = M(map, "hito_data");
            int hn = Cnt(hito);
            var sb = new StringBuilder();
            for (int i = 0; i < hn && i < 32; i++) sb.Append(' ').Append(M(E(hito, i), "fp_no"));
            L($"hito_data[{hn}] fp_no:{sb}");
        }
        catch (Exception e) { L("mapVillage FAILED: " + e.Message); }
    }

    private static void DumpVil(object village)
    {
        try
        {
            var vil = M(village, "vil");
            if (vil == null) { L("vil: NULL"); return; }
            var mapTable = M(vil, "map_table");
            var eventData = M(vil, "eventData");
            L($"vil: map_kazu={M(vil, "map_kazu")} map_table={Cnt(mapTable)} eventData={Cnt(eventData)} " +
              $"sce_msg={Cnt(M(vil, "sce_msg"))}");

            var mp = M(vil, "map_person");
            if (mp == null) L("vil.map_person: NULL");
            else L($"vil.map_person: function_index={Ints(M(mp, "function_index"), 48)} " +
                   $"omise_data={Cnt(M(mp, "omise_data"))}");

            // Every map's geometry AND its event table in one place: this is what
            // tells us whether events exist game-wide or only on the current map.
            int mn = Cnt(mapTable);
            for (int i = 0; i < mn && i < 24; i++)
            {
                var m = E(mapTable, i);
                var ev = E(eventData, i);
                L($"  map[{i}] w={M(m, "width")} h={M(m, "height")} " +
                  $"tatemono={M(m, "tatemono_kazu")} human={M(m, "human_kazu")} doors={M(m, "map_door_kazu")} " +
                  $"| eventData[{i}] mapEventMax={M(ev, "mapEventMax")} eventDat={Cnt(M(ev, "eventDat"))} " +
                  $"putMapnum={M(ev, "mapPutMapnum")} putMapsub={M(ev, "mapPutMapsub")} " +
                  $"putPara={M(ev, "mapPutPara")} putMandat={M(ev, "mapPutMandat")} " +
                  $"effectdat={M(ev, "mapEffectdat")} msgp={Cnt(M(ev, "mapMSGP"))}");
            }
        }
        catch (Exception e) { L("vil FAILED: " + e.Message); }
    }

    // THE HEADLINE. vil_ekp has never been enumerated in play.
    private static void DumpEkp(object village, int px, int py)
    {
        try
        {
            var ekp = M(village, "vil_ekp");
            int n = Cnt(ekp);
            L($"--- vil_ekp (List<EVENT_WK>) count={n}  [-1 means the field read as null]");
            if (n <= 0) return;

            for (int i = 0; i < n && i < 128; i++)
            {
                var e = E(ekp, i);
                int x = I(M(e, "x")), y = I(M(e, "y"));
                int d = Math.Abs(x - px) + Math.Abs(y - py);
                string near = d <= 3 ? "  <<<< NEAR PLAYER" : "";
                var put = M(e, "eventPut"); var form = M(e, "eventForm"); var com = M(e, "eventCom");
                L($"  ekp[{i}] pos=({x},{y}) putno={M(e, "putno")} image={M(e, "image")} " +
                  $"flag1={M(e, "flag1")} put={Cnt(put)} form={Cnt(form)} com={Cnt(com)}{near}");
                L($"    put  {Hex(put)}");
                L($"    form {Hex(form)}");
                L($"    com  {Hex(com)}");
                var gc = Gloss(com, Com);
                if (gc.Length > 0) L($"    com? {gc}");
                var gf = Gloss(form, Chk);
                if (gf.Length > 0) L($"    frm? {gf}");
            }
            if (n > 128) L($"  ... {n - 128} more entries not shown");
        }
        catch (Exception e) { L("vil_ekp FAILED: " + e.Message); }
    }

    private static void DumpElp(object village)
    {
        try
        {
            var elp = M(village, "vil_elp");
            if (elp == null) { L("--- vil_elp (EVENT_VL): NULL"); return; }
            L($"--- vil_elp (EVENT_VL): mapEventMax={M(elp, "mapEventMax")} " +
              $"eventDat={Cnt(M(elp, "eventDat"))} mapPutMapnum={M(elp, "mapPutMapnum")} " +
              $"mapPutMapsub={M(elp, "mapPutMapsub")} mapPutPara={M(elp, "mapPutPara")} " +
              $"mapPutMandat={M(elp, "mapPutMandat")} mapEffectdat={M(elp, "mapEffectdat")}");
            L($"    mapMSGP {Ints(M(elp, "mapMSGP"), 32)}");
        }
        catch (Exception e) { L("vil_elp FAILED: " + e.Message); }
    }

    // v_work is a DIRECT field of Village_c. Reading it through effect_work is
    // what made map_door look empty on every map.
    private static void DumpVWork(object village, int px, int py)
    {
        try
        {
            var vw = M(village, "v_work");
            if (vw == null)
            {
                L("--- v_work (VILLAGE_WORK): NULL on Village_c — the direct field read failed too.");
                return;
            }
            L($"--- v_work: map_door_kazu={M(vw, "map_door_kazu")} map_door={Cnt(M(vw, "map_door"))} " +
              $"komono_kazu={M(vw, "komono_kazu")} komono={Cnt(M(vw, "komono"))} " +
              $"hato_kazu={M(vw, "hato_kazu")} neko_kazu={M(vw, "neko_kazu")} fish_kazu={M(vw, "fish_kazu")}");
            L($"    exit_flag={M(vw, "exit_flag")} exit_step_no={M(vw, "exit_step_no")} " +
              $"next_flag={M(vw, "next_flag")} map_out_detect={M(vw, "map_out_detect")} " +
              $"eventMapChangeFlag={M(vw, "eventMapChangeFlag")} isNextVillageMap={M(vw, "isNextVillageMap")} " +
              $"p_map=({M(vw, "p_map_x")},{M(vw, "p_map_y")}) OP_map=({M(vw, "OP_map_x")},{M(vw, "OP_map_y")}) " +
              $"field=({M(vw, "field_x")},{M(vw, "field_y")})");
            L($"    takara=({M(vw, "takara_x")},{M(vw, "takara_y")}) takara_tag={M(vw, "takara_tag")} " +
              $"item_flagf={M(vw, "item_flagf")} item_flagb={M(vw, "item_flagb")}");

            var doors = M(vw, "map_door");
            int dn = Cnt(doors);
            for (int i = 0; i < dn && i < 64; i++)
            {
                var d = E(doors, i);
                int x = I(M(d, "map_x")), y = I(M(d, "map_y"));
                string near = Math.Abs(x - px) + Math.Abs(y - py) <= 3 ? "  <<<< NEAR PLAYER" : "";
                L($"  door[{i}] pos=({x},{y}) size=({M(d, "size_x")},{M(d, "size_y")}) " +
                  $"flag={M(d, "flag")} pri={M(d, "pri")}{near}");
            }

            var kom = M(vw, "komono");
            int kn = Cnt(kom);
            for (int i = 0; i < kn && i < 64; i++)
            {
                var k = E(kom, i);
                int x = I(M(k, "map_x")), y = I(M(k, "map_y"));
                string near = Math.Abs(x - px) + Math.Abs(y - py) <= 3 ? "  <<<< NEAR PLAYER" : "";
                L($"  komono[{i}] pos=({x},{y}) size=({M(k, "size_x")},{M(k, "size_y")}) " +
                  $"pri={M(k, "pri")} para={Hex(M(k, "para"), 24)}{near}");
            }

            var tak = M(vw, "takara");
            if (tak != null)
                L($"  takara KOMONO pos=({M(tak, "map_x")},{M(tak, "map_y")}) " +
                  $"size=({M(tak, "size_x")},{M(tak, "size_y")}) para={Hex(M(tak, "para"), 24)}");
        }
        catch (Exception e) { L("v_work FAILED: " + e.Message); }
    }

    private static void DumpPeople(object village, int px, int py)
    {
        try
        {
            // Logged even when zero. The old reader only logged "N entries" when
            // N > 0, so a list reading 0 and a list reading null produced exactly
            // the same log — nothing — and both were written down as "empty".
            var evh = M(village, "map_event_human");
            L($"--- map_event_human: count={Cnt(evh)} mapEventHumanKazu={M(village, "mapEventHumanKazu")} " +
              $"(count -1 = field read as null)");
            DumpHumanList(evh, "evh", px, py);

            var hum = M(village, "map_human");
            L($"--- map_human: count={Cnt(hum)} map_human_kazu={M(village, "map_human_kazu")}");
            DumpHumanList(hum, "hum", px, py);
        }
        catch (Exception e) { L("people FAILED: " + e.Message); }
    }

    private static void DumpHumanList(object list, string tag, int px, int py)
    {
        int n = Cnt(list);
        for (int i = 0; i < n && i < 40; i++)
        {
            var h = E(list, i);
            int x = I(M(h, "map_x")), y = I(M(h, "map_y"));
            string near = Math.Abs(x - px) + Math.Abs(y - py) <= 3 ? "  <<<< NEAR PLAYER" : "";
            L($"  {tag}[{i}] pos=({x},{y}) ev={M(h, "map_event_no")} chara_no={M(h, "chara_no")} " +
              $"dir={M(h, "dir")} speed={M(h, "speed")} put_pri={M(h, "put_pri")} " +
              $"tatemono_flg={M(h, "tatemono_flg")} tatemono_no={M(h, "tatemono_no")} " +
              $"corn=({M(h, "corn_x")},{M(h, "corn_y")},{M(h, "corn_dir")}){near}");
        }
    }

    // TATEMONO.id and .level were never correlated with what is on screen. If id
    // is a type code it separates a save desk from a wall without an interactable
    // flag; if every entry shares one id it does not, and that is settled here.
    private static void DumpTatemono(object village, int px, int py)
    {
        try
        {
            var t = M(village, "map_tatemono");
            int n = Cnt(t);
            L($"--- map_tatemono: count={n} map_tatemono_kazu={M(village, "map_tatemono_kazu")}");
            var ids = new Dictionary<int, int>();
            for (int i = 0; i < n && i < 96; i++)
            {
                var b = E(t, i);
                int x = I(M(b, "map_x")), y = I(M(b, "map_y"));
                int id = I(M(b, "id"));
                ids.TryGetValue(id, out int had); ids[id] = had + 1;
                string near = Math.Abs(x - px) + Math.Abs(y - py) <= 3 ? "  <<<< NEAR PLAYER" : "";
                L($"  tat[{i}] pos=({x},{y}) size=({M(b, "size_x")},{M(b, "size_y")}) " +
                  $"id={id} level={M(b, "level")} pri={M(b, "pri")} " +
                  $"ofs=({M(b, "t_ofx")},{M(b, "t_ofy")}){near}");
            }
            var parts = new List<string>();
            foreach (var kv in ids) parts.Add($"{kv.Key}x{kv.Value}");
            parts.Sort();
            L($"  tatemono id histogram ({ids.Count} distinct): {string.Join(" ", parts)}");
        }
        catch (Exception e) { L("map_tatemono FAILED: " + e.Message); }
    }

    // Event_c is a STATIC field on Village_c. SAVE_SELECT_MSGS is the reason to
    // look: it is a list of message ids the game itself recognises as "this is
    // the save prompt", which is a far better signal than matching on text.
    private static void DumpEventC(object village)
    {
        try
        {
            var ec = S(village.GetType(), "event_c");
            if (ec == null) { L("--- Event_c (static Village_c.event_c): NULL"); return; }
            L($"--- Event_c: eventNo={M(ec, "eventNo")} eventCNT={M(ec, "eventCNT")} " +
              $"doorCNT={M(ec, "doorCNT")} isTapEvent={M(ec, "isTapEvent")} " +
              $"tapEventList={Cnt(M(ec, "tapEventList"))} character_id={M(ec, "character_id")} " +
              $"speaker_name_id={M(ec, "speaker_name_id")} eventSentaku={M(ec, "eventSentaku")} " +
              $"eventComTMP={Cnt(M(ec, "eventComTMP"))} eventComTMPindex={M(ec, "eventComTMPindex")}");
            L($"    SAVE_SELECT_MSGS   {Ints(M(ec, "SAVE_SELECT_MSGS"), 32)}");
            L($"    YADO_SELECT_MSGS   {Ints(M(ec, "YADO_SELECT_MSGS"), 32)}");
            L($"    TELEPO_SELECT_MSGS {Ints(M(ec, "TELEPO_SELECT_MSGS"), 32)}");
            L($"    backLogMessageId={M(ec, "backLogMessageId")} c_last={M(ec, "c_last")} index_last={M(ec, "index_last")}");

            var wk = M(ec, "eventWK");
            if (wk != null)
                L($"    eventWK pos=({M(wk, "x")},{M(wk, "y")}) putno={M(wk, "putno")} " +
                  $"image={M(wk, "image")} flag1={M(wk, "flag1")}");
        }
        catch (Exception e) { L("Event_c FAILED: " + e.Message); }
    }

    // G1_event_h declares the service kinds as static ints: SHOP_OMISE,
    // SHOP_KAJIYA, SHOP_MONSHO, SHOP_KANTEI, SHOP_YADOYA, SHOP_SAVE, SHOP_SUTERU,
    // SHOP_MINIMAP, SHOP_HANYOU, SHOP_EFUNC, USER_MENU. Their VALUES are what is
    // missing: the save diary's script ends "43 07 ff" (EVENTSHOP, type 7), so if
    // SHOP_SAVE reads 7 then a save point can be recognised from static data
    // alone, on maps nobody has visited. Pure static field reads.
    private static void DumpShopConstants()
    {
        try
        {
            var t = Gsd.FindAnywhere("G1_event_h");
            if (t == null) { L("--- G1_event_h: type not found"); return; }
            var names = new[] { "USER_MENU", "SHOP_OMISE", "SHOP_KAJIYA", "SHOP_MONSHO",
                                "SHOP_KANTEI", "SHOP_YADOYA", "SHOP_SAVE", "SHOP_SUTERU",
                                "SHOP_MINIMAP", "SHOP_HANYOU", "SHOP_EFUNC" };
            var parts = new List<string>();
            foreach (var n in names) parts.Add($"{n}={S(t, n)}");
            L("--- G1_event_h service kinds: " + string.Join(" ", parts));
        }
        catch (Exception e) { L("G1_event_h FAILED: " + e.Message); }
    }

    // The only game calls in this file, deliberately last so a problem here cannot
    // cost the rest of the dump. All three take no arguments and return a value —
    // the same category as GNDAtariCheck, which has been called in play for weeks.
    // Between them they would replace the map-key fingerprint with the game's own
    // ids and give an area a name a player can actually be told.
    private static void DumpGetters(object village)
    {
        L("--- calling three no-argument getters (get_village_name / get_map_idx / get_area_idx)");
        foreach (var name in new[] { "get_village_name", "get_map_idx", "get_area_idx" })
        {
            try
            {
                var m = village.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                                                          BindingFlags.Instance | BindingFlags.DeclaredOnly,
                                                    null, Type.EmptyTypes, null);
                if (m == null) { L($"    {name}: NOT DECLARED"); continue; }
                L($"    {name}() = {m.Invoke(village, null)}");
            }
            catch (Exception e) { L($"    {name} threw: {e.Message}"); }
        }
    }

    // ── F7: every on-screen label, with its hierarchy path ───────────────────
    // The dialogue reader only forwards components named Txt_CommandNN. Anything
    // built from other components — which is the likeliest reason the save
    // confirmation and the party-join popup are silent — is invisible to
    // it. This names them.
    internal static void DumpUiText()
    {
        try
        {
            L("===================== BEGIN UI TEXT SNAPSHOT =====================");
            var all = UnityEngine.Object.FindObjectsOfType<TMP_Text>();
            int n = all != null ? all.Length : 0;
            L($"active TMP_Text components: {n}");
            int shown = 0;
            for (int i = 0; i < n && shown < 200; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string text;
                try { text = t.text; } catch { continue; }
                if (string.IsNullOrWhiteSpace(text)) continue;
                bool visible;
                try { visible = t.gameObject.activeInHierarchy; } catch { visible = false; }
                L($"  {(visible ? "SHOWN " : "hidden")} {PathOf(t)} = '{Dlg.Clean(text)}'");
                shown++;
            }
            if (shown == 0) L("  (no non-empty labels found)");
            L("====================== END UI TEXT SNAPSHOT ======================");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[G1Dump] F7 failed: {e}"); }
    }

    internal static string PathOf(Component c)
    {
        try
        {
            var parts = new List<string>();
            var t = c.transform;
            int guard = 0;
            while (t != null && guard++ < 24) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }
        catch { return "?"; }
    }

    // ── F5: does the game name its own characters and places? ───────────────
    // Everything the mod currently calls by a real name, it got from static data
    // that names itself — VILLAGEOUT for an exit's destination, EVENTSHOP +
    // SHOP_SAVE for the save point. NPCs and places are still "person 3" and
    // "exit 1 to map 2" because nothing had been found that names them. These
    // had been:
    //
    //   Fhuman_c.GetCharacterName(int hno, bool short)          -> string   STATIC
    //   Fhuman_c.GetCharacterNameTextID(int hno, bool short)    -> int      STATIC
    //   FieldBGManager.GetFieldName(int area_no)                -> string   STATIC
    //   VillageManager.GetVillageNameID(int area, int village)  -> int      STATIC
    //   Village_c.get_village_name()                            -> string   STATIC
    //   enum CHARACTER_ID { FP_Q, FP_I, FP_E, ... }
    //
    // All value parameters, all static, so calling them is the same acceptable
    // category as GNDAtariCheck — one shot on a keypress, never per frame.
    //
    // NOTE: NAV-STATUS §1.7 says `get_village_name()` is "not declared on
    // Village_c despite appearing in the decompile's method list". It IS
    // declared; it is STATIC, and the lookup that missed it searched instance
    // members only.
    //
    // THIS PROBE ONLY LOGS. It does not speak a name and does not write one to
    // the names file, because the one thing not yet known is which id space
    // `hno` is — plausibly EVENT_WK.image / HITO_DATA.fp_no / CHARACTER_ID,
    // which are the same number, but that is an inference and a systematic
    // off-by-N would put a real character's name on the wrong person. Names
    // persist to a shareable file and a wrong name is worse than no name
    // (NAV-STATUS §3, paid for when a party follower's name stuck permanently to
    // the character they were standing next to).
    //
    // So it prints, for every person on this map, the position and the name it
    // WOULD say. Somebody standing in the room can confirm or refute the whole
    // mapping in one glance, which is the same kind of check that settled the
    // collision question: an observation no interpretation can reinterpret.
    private static void DumpNames(object village)
    {
        if (village == null) { L("F5: no Village_c captured yet — stand on a field map."); return; }
        try
        {
            L("===================== BEGIN GSD1 NAME PROBE =====================");

            var fhuman = Gsd.FindAnywhere("Fhuman_c");
            var fieldBg = Gsd.FindAnywhere("FieldBGManager");
            var vilMgr = Gsd.FindAnywhere("VillageManager");
            var charId = Gsd.FindAnywhere("CHARACTER_ID");
            var vilType = village.GetType();

            var getName = Gsd.FindMethod(fhuman, "GetCharacterName");
            var getNameId = Gsd.FindMethod(fhuman, "GetCharacterNameTextID");
            var getField = Gsd.FindMethod(fieldBg, "GetFieldName");
            var getVilId = Gsd.FindMethod(vilMgr, "GetVillageNameID");
            var getVilName = Gsd.FindMethod(vilType, "get_village_name");

            // Log the zero: say which of them resolved and which did not, so a
            // silent probe is never mistaken for an empty answer.
            L($"resolved: Fhuman_c={(fhuman != null)} GetCharacterName={(getName != null)} " +
              $"GetCharacterNameTextID={(getNameId != null)} FieldBGManager.GetFieldName={(getField != null)} " +
              $"VillageManager.GetVillageNameID={(getVilId != null)} " +
              $"Village_c.get_village_name={(getVilName != null)} CHARACTER_ID enum={(charId != null)}");

            // ── places ──────────────────────────────────────────────────────
            int area = I(Call(vilType, "get_area_idx", village));
            int mapNo = I(Call(vilType, "get_map_idx", village));
            L($"current area={area} map={mapNo}  village_name='{Str(Invoke(getVilName, null))}'  " +
              $"prev_village_name='{M(village, "prev_village_name")}'");

            // Every static place-name call, side by side, in one line. When the
            // game has displayed its arrival banner this same line is written
            // automatically (Gsd1Place.AbsorbBanner), so the two together score
            // every candidate against a name the game itself put on screen.
            L("  place-name candidates: " + Gsd1Place.CandidateReport(village));

            if (getField != null)
                for (int a = 0; a < 16; a++)
                {
                    var s = Str(Invoke(getField, null, a));
                    if (!string.IsNullOrEmpty(s)) L($"  GetFieldName({a}) = '{s}'" + (a == area ? "   <<<< current area" : ""));
                }
            else L("  GetFieldName: NOT RESOLVED, nothing measured.");

            if (getVilId != null)
                for (int v = 0; v < 24; v++)
                {
                    int id = I(Invoke(getVilId, null, area, v));
                    if (id > 0) L($"  GetVillageNameID(area {area}, village {v}) = text id {id}" +
                                  (v == mapNo ? "   <<<< current map index" : ""));
                }
            else L("  GetVillageNameID: NOT RESOLVED, nothing measured.");

            // ── the id space of GetCharacterName ────────────────────────────
            // Swept rather than assumed. If it answers for a contiguous block
            // starting at 0 then hno is a character id; if it answers only for a
            // handful, it is a party slot and image is the wrong key.
            if (getName == null) L("GetCharacterName: NOT RESOLVED, no names measured.");
            else
            {
                // The WHOLE table, printed. The first sweep capped its output at
                // 96 hits, which was enough to prove the call works and not
                // enough to see which ids are real characters — and that is the
                // question the generic-sprite filter turns on. It also counted
                // the engine's own placeholder as "named", so 255 of 256 looked
                // answered when most of the tail is empty.
                int real = 0, placeholder = 0, blank = 0;
                var sb = new StringBuilder();
                int perLine = 0;
                for (int hno = 0; hno < 256; hno++)
                {
                    var nm = Str(Invoke(getName, null, hno, false));
                    string sym = EnumName(charId, hno);
                    if (string.IsNullOrEmpty(nm)) { blank++; continue; }
                    bool ph = nm.IndexOf("名前未定義", StringComparison.Ordinal) >= 0;
                    if (ph) { placeholder++; } else { real++; }
                    int tid = getNameId == null ? -2 : I(Invoke(getNameId, null, hno, false));
                    sb.Append(hno).Append(sym).Append('=').Append(ph ? "<undefined>" : nm)
                      .Append('[').Append(tid).Append("]  ");
                    if (++perLine >= 8) { L("  " + sb.ToString().TrimEnd()); sb.Clear(); perLine = 0; }
                }
                if (sb.Length > 0) L("  " + sb.ToString().TrimEnd());
                L($"GetCharacterName sweep hno 0..255: {real} real names, " +
                  $"{placeholder} '名前未定義' placeholders, {blank} empty.");
            }

            // ── every person on this map, with the name it WOULD get ────────
            // EVENT_WK.image is the identity the mod already uses for NPCs, and
            // the matching HUMAN in map_human carries the position. Printing
            // position and proposed name together is what makes this checkable
            // by somebody standing in the room.
            var ekp = M(village, "vil_ekp");
            int en = Cnt(ekp);
            var humans = M(village, "map_human");
            int hn = Cnt(humans);
            L($"--- people: vil_ekp={en} map_human={hn} (both counts logged even at 0) ---");
            for (int i = 0; i < en; i++)
            {
                var e = E(ekp, i);
                if (e == null) continue;
                int putno = I(M(e, "putno"));
                if (putno < 0 || putno >= 255) continue;           // not a person
                int image = I(M(e, "image"));

                // position from the matching HUMAN, linked by map_event_no
                int hx = -1, hy = -1;
                for (int k = 0; k < hn; k++)
                {
                    var h = E(humans, k);
                    if (h != null && I(M(h, "map_event_no")) == putno)
                    { hx = I(M(h, "map_x")); hy = I(M(h, "map_y")); break; }
                }

                string nm = getName == null ? "(no method)" : Str(Invoke(getName, null, image, false));
                string shortNm = getName == null ? "" : Str(Invoke(getName, null, image, true));
                int sid = getNameId == null ? -1 : I(Invoke(getNameId, null, image, false));
                L($"  ekp[{i}] putno={putno} image={image} {EnumName(charId, image)} at ({hx},{hy}) " +
                  $"-> name='{nm}' short='{shortNm}' textId={sid}");
            }

            // hito_data holds this map's sprite slots; its fp_no should be the
            // same id space as image, and if it is not, that is worth seeing.
            var map = M(village, "mapVillage");
            var hito = M(map, "hito_data");
            int hdn = Cnt(hito);
            L($"--- hito_data: count={hdn} ---");
            for (int i = 0; i < hdn; i++)
            {
                int fp = I(M(E(hito, i), "fp_no"));
                string nm = getName == null ? "(no method)" : Str(Invoke(getName, null, fp, false));
                L($"  hito_data[{i}] fp_no={fp} {EnumName(charId, fp)} -> name='{nm}'");
            }


            // The questionnaire itself, which also runs automatically once per map.
            Questionnaire(village, Gsd1Names.MapKey(village));

            L("===================== END GSD1 NAME PROBE =====================");
        }
        catch (Exception e) { L("F5 name probe FAILED: " + e.Message); }
    }

    private static object Invoke(MethodInfo m, object inst, params object[] args)
    {
        try { return m?.Invoke(inst, args); } catch (Exception e) { return "<threw: " + e.Message + ">"; }
    }

    private static object Call(Type t, string name, object inst)
    {
        try { return Gsd.FindMethod(t, name)?.Invoke(inst, null); } catch { return null; }
    }

    private static string Str(object o) => o?.ToString() ?? "";

    /// <summary>The CHARACTER_ID member for a value, e.g. "FP_E" — a second,
    /// independent label for the same id, so a name can be sanity-checked
    /// against the game's own symbol rather than only against a guess.</summary>
    private static string EnumName(Type charId, int value)
    {
        try
        {
            if (charId == null || !charId.IsEnum) return "";
            var n = Enum.GetName(charId, Enum.ToObject(charId, value));
            return string.IsNullOrEmpty(n) ? "" : "(" + n + ")";
        }
        catch { return ""; }
    }

    // ── THE IDENTITY QUESTIONNAIRE, RUN AUTOMATICALLY ───────────────────────
    // Called once per map by Gsd1Path as well as by F5. Waiting for a keypress
    // meant a whole session of play produced no rows at all: the player mapped
    // four maps, and the one thing needed to settle the NPC-name band was the
    // one thing that had to be asked for. A measurement that only happens when
    // somebody remembers to trigger it is a measurement that does not happen.
    public static void Questionnaire(object village, string mapKey)
    {
        if (village == null) return;
        try
        {
            var charId = Gsd.FindAnywhere("CHARACTER_ID");
            var fhuman = Gsd.FindAnywhere("Fhuman_c");
            var getName = Gsd.FindMethod(fhuman, "GetCharacterName");
            var getNameId = Gsd.FindMethod(fhuman, "GetCharacterNameTextID");

            var ekp = M(village, "vil_ekp");
            int en = Cnt(ekp);
            var humans = M(village, "map_human");
            int hn = Cnt(humans);
            var map = M(village, "mapVillage");
            var hito = M(map, "hito_data");
            int hdn = Cnt(hito);
            L($"--- people on {mapKey}: vil_ekp={en} map_human={hn} hito_data={hdn} (all counts logged even at 0) ---");
            // ── THE IDENTITY QUESTIONNAIRE ─────────────────────────────────
            // Everything above measures what the game SAYS. This section exists
            // to measure whether it is TRUE, and it is built for the §1.9 method
            // rather than for reading on its own.
            //
            // The problem: CHARACTER_ID's digit-bearing slots (FP_00P..FP_21P)
            // are both generic person sprites and named characters, so
            // GetCharacterName answers "character C" for a town A townsperson.
            // Two rules have already been adopted and retracted, and both were
            // arguments about what the id ranges must mean. An argument about
            // the numbers can generate a candidate; only an observation can
            // choose between candidates.
            //
            // The observation available here is the player: they can walk to a
            // target, talk to it, and say who it actually is. So this prints one
            // row per person, headed by THE EXACT LABEL THE TARGET LIST SPEAKS —
            // "possible character C 2" — so an answer can be tied to a row without
            // ambiguity, followed by every candidate discriminator side by side,
            // uncapped. Nothing here is filtered and nothing is decided; the
            // scoring happens once the answers exist.
            //
            // Capping is what let the last wrong rule survive (the first sweep
            // printed 96 of 256 ids), so every person is printed, every time.
            L("--- IDENTITY QUESTIONNAIRE: who are these people really? ---");
            L("    columns: label the target list speaks | tile | image | CHARACTER_ID symbol |");
            L("             proposed name | textId | digit-in-symbol | chara_no | map_event_no |");
            L("             putno | flag1 | how many people on this map share this image |");
            L("             how many hito_data slots carry it | eventCom bytes | 0x1c in eventCom");

            var rows = new List<QRow>();
            var spoken = Gsd1Path.PeopleSnapshot();
            L($"    the target list currently holds {spoken.Count} people on this map.");

            // How many people share each image, and how many sprite slots carry
            // it. "A unique character appears once; a generic sprite is reused"
            // is one of the candidate rules, so it has to be measured, not
            // assumed — and measured over the WHOLE map.
            var perImage = new Dictionary<int, int>();
            for (int i = 0; i < en; i++)
            {
                var e = E(ekp, i);
                if (e == null) continue;
                int pn = I(M(e, "putno"));
                if (pn < 0 || pn >= 255) continue;
                int im = I(M(e, "image"));
                perImage.TryGetValue(im, out int c);
                perImage[im] = c + 1;
            }
            var perHito = new Dictionary<int, int>();
            for (int i = 0; i < hdn; i++)
            {
                int fp = I(M(E(hito, i), "fp_no"));
                perHito.TryGetValue(fp, out int c);
                perHito[fp] = c + 1;
            }

            for (int i = 0; i < en; i++)
            {
                var e = E(ekp, i);
                if (e == null) continue;
                int putno = I(M(e, "putno"));
                if (putno < 0 || putno >= 255) continue;
                int image = I(M(e, "image"));
                int flag1 = I(M(e, "flag1"));

                // The matching HUMAN carries the live position AND chara_no,
                // which is one of the candidates. It was read as 0 for ambient
                // NPCs once, before several other things were fixed; it is read
                // again here rather than taken on trust.
                int hx = -1, hy = -1, charaNo = -1, evNo = -1;
                for (int k = 0; k < hn; k++)
                {
                    var h = E(humans, k);
                    if (h == null || I(M(h, "map_event_no")) != putno) continue;
                    hx = I(M(h, "map_x")); hy = I(M(h, "map_y"));
                    charaNo = I(M(h, "chara_no")); evNo = I(M(h, "map_event_no"));
                    break;
                }

                // Which spoken label belongs to this person. Matched on the
                // target's stable Id — map_human:<map_event_no>, and
                // map_event_no == putno — rather than on the tile, because an
                // NPC that wanders a step between the two reads would otherwise
                // be labelled as somebody else. A row that cannot be tied to a
                // target says so instead of guessing.
                string wantId = "map_human:" + putno;
                string label = "(not in the target list)";
                foreach (var row in spoken)
                    if (row[3] == wantId) { label = row[0]; break; }

                string sym = EnumName(charId, image);
                bool digit = false;
                foreach (var c in sym) if (c >= '0' && c <= '9') { digit = true; break; }

                string nm = getName == null ? "(no method)" : Str(Invoke(getName, null, image, false));
                int sid = getNameId == null ? -1 : I(Invoke(getNameId, null, image, false));

                // Raw bytes only. The COM opcode widths cannot be recovered
                // (NAV-STATUS §1.7), so this does NOT claim the script contains
                // a STORYMSG — it reports that the byte 0x1c appears somewhere
                // in it, which is a candidate signal and is labelled as one.
                var com = M(e, "eventCom");
                int comLen = Cnt(com);
                bool has1c = false;
                for (int b = 0; b < comLen; b++)
                    if ((I(E(com, b)) & 0xff) == 0x1c) { has1c = true; break; }

                perImage.TryGetValue(image, out int shareCount);
                perHito.TryGetValue(image, out int hitoCount);

                rows.Add(new QRow
                {
                    Label = label,
                    Name = (string.IsNullOrWhiteSpace(nm) || nm.IndexOf("名前未定義", StringComparison.Ordinal) >= 0 || sid < 0)
                           ? null : nm.Trim(),
                    Digit = digit,
                    Shared = shareCount,
                });

                L($"  Q ekp[{i}] | '{label}' | ({hx},{hy}) | image={image} | {sym} | '{nm}' | " +
                  $"textId={sid} | digitSymbol={digit} | chara_no={charaNo} | map_event_no={evNo} | " +
                  $"putno={putno} | flag1={flag1} | sharedByPeople={shareCount} | " +
                  $"hitoSlots={hitoCount} | comBytes={comLen} | has0x1c={has1c}");
            }
            // ── EACH CANDIDATE RULE, APPLIED, ON EVERY MAP ─────────────────
            // The rows above hold the evidence; these lines hold what each
            // candidate would DO with it, so a rule can be compared against the
            // names already known to be right or wrong without reconstructing it
            // by hand from sixteen columns.
            //
            // The ground truth so far, all from a player who was standing there:
            //   CORRECT  character A, character E, character I, character J, character K, character L, character M, character F,
            //            character G, character B    — every one a three-letter symbol
            //   WRONG    character C, character D        — both digit-bearing symbols, both
            //            ordinary townspeople
            // No digit-bearing name has EVER been confirmed correct, and the
            // filter that used to reject them was retracted on the argument that
            // official role and soldier role 1 look useful — not on evidence that
            // they are right. That is the gap these lines close.
            //
            // A rule is only interesting if it keeps every CORRECT name and drops
            // every WRONG one. Keeping nothing scores no violations too, so the
            // count kept is printed beside the count dropped.
            Score("accept every name the game gives", rows, r => r.Name != null);
            Score("reject digit-bearing symbols (FP_00P..FP_21P)", rows, r => r.Name != null && !r.Digit);
            Score("accept only an image carried by ONE person on this map", rows, r => r.Name != null && r.Shared == 1);
            Score("accept a three-letter symbol, or a unique image", rows,
                  r => r.Name != null && (!r.Digit || r.Shared == 1));

            L("--- END QUESTIONNAIRE. Ask the player who each labelled row really is, then");
            L("    score every candidate column against their answers. Do not choose a rule");
            L("    from the id ranges: that argument has been made twice and been wrong twice.");
        }
        catch (Exception e) { L("identity questionnaire FAILED: " + e.Message); }
    }


    /// <summary>One person, reduced to the columns a candidate rule reads.</summary>
    private sealed class QRow
    {
        public string Label;      // exactly what the target list speaks
        public string Name;       // the game's proposed name, or null
        public bool Digit;        // its CHARACTER_ID symbol carries a digit
        public int Shared;        // how many people on this map carry this image
    }

    private static void Score(string rule, List<QRow> rows, Func<QRow, bool> accepts)
    {
        int kept = 0, dropped = 0;
        var names = new List<string>();
        foreach (var r in rows)
        {
            if (accepts(r)) { kept++; if (r.Name != null && !names.Contains(r.Name)) names.Add(r.Name); }
            else dropped++;
        }
        L($"  CANDIDATE '{rule}': names {kept} of {rows.Count} people, leaves {dropped} generic -> " +
          (names.Count == 0 ? "(nothing named)" : string.Join(", ", names)));
    }

}
