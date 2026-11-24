using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// GSD1 NAV PROBE v1 — Suikoden 1 navigation groundwork. LOGGING ONLY, PASSIVE.
//
// Suikoden 1's structures are cleaner than Suikoden 2's, from the (newly
// regenerated) GSD1 decompile — the old BepInEx\_decompiled\GSD1.cs was 0 bytes
// because ilspycmd writes "<name>.decompiled.cs", so nothing had ever been
// verified against Suikoden 1:
//
//   Village_c.player       : HUMAN -> map_x/map_y are TILE coords (byte),
//                                     map_dx/map_dy sub-tile, dir facing
//   Village_c.map_human    : List<HUMAN>   (NPCs)
//   Village_c.vil          : VIL -> map_table : Il2CppReferenceArray<MAP>
//   MAP.atari_data         : Il2CppStructArray<byte>  <- FLAT collision grid
//   MAP.width / MAP.height : short                    <- its dimensions
//
// So unlike GSD2 (fixed-point EVENT_POS plus a tilemap/attribute-plane split),
// GSD1 exposes tile coordinates and a flat collision byte array directly.
//
// Same safety rule as the GSD2 probe, which is there because breaking it once
// corrupted a live session: the only hook is on a controller method whose
// arguments are primitives (NewPlayerMove(int)), and its postfix captures
// __instance without ever reading __args. Village_c.GNDAtariCheck(int,int,bool)
// is a value-parameter-only collision query and so would be safe to CALL, but
// nothing here calls it yet — the flat atari_data array is read first, because
// pure field reads carry no risk at all.
//
// F11 in the field: one-shot dump of the collision grid around the player.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd1Nav
{
    public static bool Enabled = true;

    private static bool _hooked;
    private static bool _gaveUp;
    private static int _tries;
    private static float _nextTry;
    private static object _village;    // live Village_c instance

    /// <summary>The live Village_c, for read-only probes (Gsd1Probe). Null until captured.</summary>
    internal static object Village => _village;

    /// <summary>When Village_c.NewPlayerMove last ran (realtime), or -1.</summary>
    internal static float CapturedAt { get; private set; } = -1f;

    /// <summary>Suikoden 1's field is the one running. The launcher can switch
    /// games without restarting the process, and the controller of the game left
    /// behind stays referenced; the one captured MORE RECENTLY is the live one.
    /// In a session that only ever ran one game this is just "captured".</summary>
    internal static bool Live => _village != null && CapturedAt >= NavProbe.CapturedAt;
    private static bool _f11Down;
    private static bool _f12Down;
    private static bool _f6Down;
    private static float _nextLog;

    public static void Tick()
    {
        if (!Enabled || _gaveUp) return;

        // Installed from the heartbeat rather than alongside Village_c, because
        // the arrival banner for the FIRST area fires while that area is still
        // loading — before any Village_c exists to capture.
        Gsd1Place.TickHooks();

        if (!_hooked)
        {
            // Retry about once a second and then stop, rather than every frame —
            // an unthrottled retry once wrote ~60,000 log lines in one session.
            if (Time.realtimeSinceStartup < _nextTry) return;
            _nextTry = Time.realtimeSinceStartup + 1f;
            if (!TryHook() && ++_tries >= 60)
            {
                _gaveUp = true;
                Plugin.Logger.LogInfo("[Nav1] Village_c not found after 60 tries; GSD1 likely not running this session.");
            }
            return;
        }

        if (Live)
        {
            // Every frame, not once a second: a cell the player has stood on is
            // PROVEN walkable, and that is the only ground truth this mod can
            // collect without asking the player to describe a room. The
            // one-second position log samples 4-8 cells apart, which is enough
            // to tell where somebody went and not enough to tell which cells
            // they crossed — and "which cells" is exactly what falsifies a
            // collision hypothesis. F6 writes the accumulated set out.
            NoteVisited();

            if (Time.realtimeSinceStartup >= _nextLog)
            {
                _nextLog = Time.realtimeSinceStartup + 1f;
                LogPosition();
            }
        }

        Gsd1Path.Tick(Live ? _village : null);   // J/K/L + Shift, P — landmark cycling and A*
        PollKey();
    }

    // Visited cells, keyed by map, capped so a long session cannot grow without
    // bound. Cleared when the map key changes.
    private static readonly System.Collections.Generic.HashSet<int> _visited =
        new System.Collections.Generic.HashSet<int>();
    private static string _visitedMap;

    private static void NoteVisited()
    {
        try
        {
            var key = Gsd1Names.MapKey(_village);
            if (key == null) return;                       // mid-load; never record
            if (key != _visitedMap) { _visited.Clear(); _visitedMap = key; }
            if (_visited.Count > 20000) return;

            var player = UiText.Member(_village, "player");
            if (player == null) return;
            int x = NavProbeShared.ToInt(UiText.Member(player, "map_x"));
            int y = NavProbeShared.ToInt(UiText.Member(player, "map_y"));
            if (x < 0 || y < 0 || x > 4095 || y > 4095) return;
            _visited.Add((x << 12) | y);
        }
        catch { }
    }

    private static bool TryHook()
    {
        try
        {
            var village = Gsd.FindAnywhere("Village_c");
            if (village == null) return false;

            // NewPlayerMove(Int32) — a single primitive parameter, and the
            // postfix reads only __instance, never __args.
            var m = village.GetMethod("NewPlayerMove", BindingFlags.Public | BindingFlags.NonPublic |
                                                       BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (m == null) { Plugin.Logger.LogWarning("[Nav1] Village_c.NewPlayerMove NOT DECLARED."); return false; }

            var h = new Harmony("io.github.artemis626.suikodenaccess.nav1");
            h.Patch(m,
                prefix: new HarmonyMethod(typeof(Gsd1MovementBlock), nameof(Gsd1MovementBlock.Prefix)),
                postfix: new HarmonyMethod(typeof(Gsd1Nav), nameof(CaptureVillage)));
            Plugin.Logger.LogInfo("[Nav1] patched Village_c.NewPlayerMove (prefix blocks movement during text entry)");
            _hooked = true;
            return true;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] TryHook failed: {e.Message}"); return false; }
    }

    private static void CaptureVillage(object __instance)
    {
        _village = __instance;
        CapturedAt = Time.realtimeSinceStartup;
    }

    private static void PollKey()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            bool down = kb[Key.F11].isPressed;
            if (down && !_f11Down) Dump();
            _f11Down = down;

            bool down12 = kb[Key.F12].isPressed;
            if (down12 && !_f12Down) ProbeCollision();
            _f12Down = down12;

            bool down6 = kb[Key.F6].isPressed;
            if (down6 && !_f6Down) DumpCollisionFile();
            _f6Down = down6;
        }
        catch { }
    }

    // Once a second, and only when something changed: standing still used to
    // write the same line sixty times a minute.
    private static string _lastPosition;

    private static void LogPosition()
    {
        try
        {
            var player = UiText.Member(_village, "player");
            if (player == null) return;
            var line =
                $"[Nav1] map_x={UiText.Member(player, "map_x")} map_y={UiText.Member(player, "map_y")} " +
                $"map_dx={UiText.Member(player, "map_dx")} map_dy={UiText.Member(player, "map_dy")} " +
                $"dir={UiText.Member(player, "dir")} " +
                $"map_human={NavProbeShared.CountOf(UiText.Member(_village, "map_human"))} " +
                // Logged so a play session shows whether vil.id actually changes
                // between rooms — exit learning groups by it, and if it is a
                // village-level id the grouping needs to be finer.
                $"mapKey={Gsd1Names.MapKey(_village)}";
            if (line == _lastPosition) return;
            _lastPosition = line;
            Plugin.Logger.LogInfo(line);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] LogPosition failed: {e.Message}"); }
    }

    // ── F12: the collision MEASUREMENT ──────────────────────────────────────
    // One keypress must settle BLDAtariCheck's coordinate space, because every
    // interior route depends on it and it has never been measured. The building
    // layer is demonstrably live — a route through the villa bedroom reported 98
    // cells blocked by buildings and still cut straight through a wall — so the
    // question is not "is it on" but "is it being asked about the right cells".
    //
    // What is printed, all around the player, all in ONE press:
    //
    //   1. GROUND per CELL — the derived walkability A* actually uses, i.e.
    //      GNDAtariCheck(c>>1, r>>1) with the (r&1,c&1) quadrant bit picked out.
    //      The old F12 called GNDAtariCheck with the player's CELL coordinates,
    //      which is the very scale bug §1.7 fixed in the pathfinder — so the
    //      diagnostic was drawing a window at twice the scale of the router's.
    //   2. GROUND raw masks per CHIP — the space GNDAtariCheck really takes.
    //   3. BUILDINGS in three candidate spaces × four levels, summarised first
    //      (non-zero count, the player's own cell, distinct return values) and
    //      then drawn for the combinations that carry information. A combination
    //      that blocks nothing, or blocks everything, is reported as such and not
    //      drawn: "found none" must never look like "never looked".
    //        cell : x, y                — what the pathfinder does today
    //        chip : x>>1, y>>1          — the leading hypothesis
    //        dbl  : x<<1, y<<1          — the inverse, cheap to rule out
    //   4. The `ref int tatemono_no` READ BACK. This is the decisive part and it
    //      was being thrown away: a non-zero answer names WHICH building entry
    //      blocked, and that entry states its own map_x/map_y. If cell (14,34)
    //      is blocked by an entry sitting at (14,33), the query space is cells;
    //      if it is blocked by one sitting at (28,66), it is chips. That is a
    //      direct comparison that assumes nothing about what TATEMONO.size means.
    //   5. The nearby TATEMONO entries, so the windows can be rasterised offline
    //      against real rectangles without a second play session.
    //
    // Still only value parameters plus one primitive out-parameter, still one
    // shot per keypress, never per frame — the same acceptable category as the
    // GNDAtariCheck calls that have been safe in play for weeks.
    private static MethodInfo _gndAtari;
    private static MethodInfo _bldAtari;
    private static bool _gndMissing;
    private static bool _bldMissing;

    private const BindingFlags PBF = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private const int WinX = 8;   // cells left/right of the player
    private const int WinY = 5;   // cells above/below
    private const int MaxLevel = 3;

    private static void L(string s) => Plugin.Logger.LogInfo("[Nav1] F12: " + s);

    private static bool ResolveProbes()
    {
        var t = _village.GetType();
        if (_gndAtari == null && !_gndMissing)
        {
            foreach (var m in t.GetMethods(PBF))
            {
                if (m.Name != "GNDAtariCheck") continue;
                var ps = m.GetParameters();
                if (ps.Length == 3 && ps[0].ParameterType == typeof(int) &&
                    ps[1].ParameterType == typeof(int) && ps[2].ParameterType == typeof(bool))
                { _gndAtari = m; break; }
            }
            if (_gndAtari == null) { _gndMissing = true; L("GNDAtariCheck(int,int,bool) is NOT declared on Village_c."); }
            else L("resolved GNDAtariCheck(int,int,bool).");
        }
        if (_bldAtari == null && !_bldMissing)
        {
            foreach (var m in t.GetMethods(PBF))
                if (m.Name == "BLDAtariCheck" && m.GetParameters().Length == 5) { _bldAtari = m; break; }
            if (_bldAtari == null) { _bldMissing = true; L("BLDAtariCheck(5 args) is NOT declared on Village_c."); }
            else L("resolved BLDAtariCheck(x,y,level,ref tatemono_no,isLog).");
        }
        return _gndAtari != null || _bldAtari != null;
    }

    private static int GndMask(int chipX, int chipY)
    {
        if (_gndAtari == null || chipX < 0 || chipY < 0) return -1;
        try { return NavProbeShared.ToInt(_gndAtari.Invoke(_village, new object[] { chipX, chipY, false })); }
        catch { return -1; }
    }

    /// <summary>One BLDAtariCheck point query, with the out-parameter read back.</summary>
    private static bool Bld(int x, int y, int level, out int ret, out int tno)
    {
        ret = 0; tno = -1;
        if (_bldAtari == null || _bldMissing || x < 0 || y < 0) return false;
        try
        {
            // Reflection copies a by-ref primitive back into the args array on
            // return. This is a direct call, not a Harmony patch, so none of the
            // by-ref boxing hazard that corrupted GSD2 collision applies here.
            var args = new object[] { x, y, level, 0, false };
            ret = NavProbeShared.ToInt(_bldAtari.Invoke(_village, args));
            tno = NavProbeShared.ToInt(args[3]);
            return true;
        }
        catch (Exception e)
        {
            _bldMissing = true;
            L($"BLDAtariCheck THREW at ({x},{y}) level {level}: {e.Message} — building probe stops here.");
            return false;
        }
    }

    private static int Q(int space, int v) => space == 0 ? v : space == 1 ? v >> 1 : v << 1;
    private static readonly string[] SpaceName = { "cell", "chip", "dbl " };

    private static char Hex(int v) =>
        v < 0 ? '?' : v == 0 ? '.' : v < 16 ? "0123456789abcdef"[v] : '+';

    private static string TatDesc(object tat, int tn, int no)
    {
        if (tn <= 0) return $"tno={no} (map_tatemono count={tn}, nothing to compare against)";
        if (no < 0 || no >= tn) return $"tno={no} OUT OF RANGE 0..{tn - 1}";
        var b = UiText.Element(tat, no);
        if (b == null) return $"tno={no} (element read null)";
        return $"tno={no} pos=({UiText.Member(b, "map_x")},{UiText.Member(b, "map_y")}) " +
               $"size=({UiText.Member(b, "size_x")},{UiText.Member(b, "size_y")}) " +
               $"lvl={UiText.Member(b, "level")} pri={UiText.Member(b, "pri")} " +
               $"ofs=({UiText.Member(b, "t_ofx")},{UiText.Member(b, "t_ofy")})";
    }

    // ── F6: the whole map, to a file, for offline analysis ──────────────────
    // F12 draws a window around the player and is meant to be read in the log.
    // This writes the WHOLE map to suikoden1_collision_dump.txt instead, because
    // the questions left are correlation questions — does the building layer
    // agree with the TATEMONO rectangles, and does either agree with where the
    // player has actually walked — and a seventeen-by-eleven window has too few
    // constraints to answer them. Over a whole map there are hundreds.
    //
    // One grid serves every coordinate-space hypothesis at once: the raw answer
    // of BLDAtariCheck(x, y) for x and y over the full range. A cell-space
    // reading is that table at (c, r); a chip-space reading is the same table at
    // (c>>1, r>>1). Nothing needs to be re-measured to change the hypothesis.
    //
    // Written to a file rather than the log because it is roughly 200 lines of
    // dense hex and the log is also how the player reports everything else.
    private static void DumpCollisionFile()
    {
        try
        {
            if (_village == null) { Plugin.Logger.LogInfo("[Nav1] F6: no Village_c captured yet."); return; }
            if (!ResolveProbes()) { Plugin.Logger.LogInfo("[Nav1] F6: no collision method resolved; nothing written."); return; }

            var player = UiText.Member(_village, "player");
            int px = NavProbeShared.ToInt(UiText.Member(player, "map_x"));
            int py = NavProbeShared.ToInt(UiText.Member(player, "map_y"));
            var map = UiText.Member(_village, "mapVillage");
            int w = NavProbeShared.ToInt(UiText.Member(map, "width"));
            int h = NavProbeShared.ToInt(UiText.Member(map, "height"));
            int layer = NavProbeShared.ToInt(UiText.Member(map, "layer_no"));

            // Query range. The map is w x h CHIPS, so cell coordinates run to
            // 2w-1 / 2h-1 and that is the widest either hypothesis needs. Kept
            // to the map's own size: a query far outside it has never been shown
            // to be safe, and nothing here needs one.
            int maxX = Math.Max(2 * w, 2 * h) > 0 ? 2 * w : 48;
            int maxY = 2 * h > 0 ? 2 * h : 48;
            if (maxX < 8 || maxX > 256) maxX = 48;
            if (maxY < 8 || maxY > 256) maxY = 48;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Suikoden Access — GSD1 collision dump");
            sb.AppendLine($"# mapKey={Gsd1Names.MapKey(_village)}  player cell=({px},{py})  chip=({px >> 1},{py >> 1})");
            sb.AppendLine($"# MAP width={w} height={h} chips, layer_no={layer}, " +
                          $"wNew={UiText.Member(map, "wNew")} hNew={UiText.Member(map, "hNew")} " +
                          $"w2={UiText.Member(map, "w2")} h2={UiText.Member(map, "h2")}");
            sb.AppendLine($"# query range x 0..{maxX - 1}, y 0..{maxY - 1}");
            sb.AppendLine("# Grids are one hex value per queried coordinate, columns x ascending, one row per y.");
            sb.AppendLine();

            // GNDAtariCheck over the same range, so both layers are in one space.
            sb.AppendLine($"[GND] GNDAtariCheck(x,y,false) — 2x2 quadrant mask per CHIP (0..15)");
            for (int y = 0; y < maxY; y++)
            {
                var row = new System.Text.StringBuilder();
                for (int x = 0; x < maxX; x++) row.Append(GndMask(x, y).ToString("x2")).Append(' ');
                sb.AppendLine($"y={y,3}: {row}");
            }
            sb.AppendLine();

            // BLDAtariCheck at every level that returned anything, plus the
            // tatemono_no it reports for each coordinate.
            foreach (int level in new[] { 0, 1, 2, 3 })
            {
                if (_bldMissing) break;
                var vals = new System.Text.StringBuilder();
                var nos = new System.Text.StringBuilder();
                int nonZero = 0;
                for (int y = 0; y < maxY; y++)
                {
                    var vrow = new System.Text.StringBuilder();
                    var nrow = new System.Text.StringBuilder();
                    for (int x = 0; x < maxX; x++)
                    {
                        int ret = Bld(x, y, level, out int v, out int t) ? v : -1;
                        int tno = ret < 0 ? -1 : t;
                        if (ret > 0) nonZero++;
                        vrow.Append(ret < 0 ? "????" : ret.ToString("x4")).Append(' ');
                        nrow.Append(tno < 0 ? "..." : tno.ToString("d3")).Append(' ');
                    }
                    vals.AppendLine($"y={y,3}: {vrow}");
                    nos.AppendLine($"y={y,3}: {nrow}");
                }
                sb.AppendLine($"[BLD level={level}] raw ushort return, {nonZero} non-zero of {maxX * maxY}");
                sb.Append(vals);
                sb.AppendLine();
                sb.AppendLine($"[BLDTNO level={level}] the ref tatemono_no reported at the same coordinate");
                sb.Append(nos);
                sb.AppendLine();
            }

            // Every TATEMONO, not just the near ones — the rectangles to
            // correlate the grids against.
            var tat = UiText.Member(_village, "map_tatemono");
            int tn = NavProbeShared.CountOf(tat);
            sb.AppendLine($"[TATEMONO] Village_c.map_tatemono count={tn} " +
                          $"(map_tatemono_kazu={UiText.Member(_village, "map_tatemono_kazu")})");
            for (int i = 0; i < tn; i++)
            {
                var b = UiText.Element(tat, i);
                if (b == null) { sb.AppendLine($"  tat[{i}] = null"); continue; }
                sb.AppendLine($"  tat[{i}] pos=({UiText.Member(b, "map_x")},{UiText.Member(b, "map_y")}) " +
                              $"size=({UiText.Member(b, "size_x")},{UiText.Member(b, "size_y")}) " +
                              $"id={UiText.Member(b, "id")} lvl={UiText.Member(b, "level")} " +
                              $"pri={UiText.Member(b, "pri")} ofs=({UiText.Member(b, "t_ofx")},{UiText.Member(b, "t_ofy")})");
            }
            sb.AppendLine();

            // MAP.tatemono_data is a DIFFERENT array (42 entries against 40) and
            // the ref tatemono_no may index it rather than map_tatemono. Dumped
            // so the question can be answered rather than assumed.
            var td = UiText.Member(map, "tatemono_data");
            int tdn = NavProbeShared.CountOf(td);
            sb.AppendLine($"[TATEMONO_DATA] MAP.tatemono_data count={tdn}");
            for (int i = 0; i < tdn; i++)
            {
                var b = UiText.Element(td, i);
                if (b == null) { sb.AppendLine($"  td[{i}] = null"); continue; }
                sb.AppendLine($"  td[{i}] pos=({UiText.Member(b, "map_x")},{UiText.Member(b, "map_y")}) " +
                              $"anime_no={UiText.Member(b, "anime_no")} pri={UiText.Member(b, "pri")} " +
                              $"lvl={UiText.Member(b, "level")} ofs=({UiText.Member(b, "t_ofx")},{UiText.Member(b, "t_ofy")})");
            }
            sb.AppendLine();

            // The falsifier: every cell the player has stood on since this map
            // loaded is walkable, whatever any hypothesis says.
            sb.AppendLine($"[WALKED] {_visited.Count} distinct cells stood on since this map loaded (map {_visitedMap})");
            var walked = new System.Collections.Generic.List<int>(_visited);
            walked.Sort();
            var line = new System.Text.StringBuilder();
            int n = 0;
            foreach (var v in walked)
            {
                line.Append($"({v >> 12},{v & 0xfff}) ");
                if (++n % 12 == 0) { sb.AppendLine("  " + line); line.Clear(); }
            }
            if (line.Length > 0) sb.AppendLine("  " + line);

            var dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
            var path = System.IO.Path.Combine(dir, "suikoden1_collision_dump.txt");
            System.IO.File.WriteAllText(path, sb.ToString());
            Plugin.Logger.LogInfo($"[Nav1] F6: wrote {sb.Length} chars to {path} " +
                                  $"({_visited.Count} walked cells, {tn} tatemono, range {maxX}x{maxY}).");
            Narration.Label("Collision dump written.");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] F6 dump failed: {e.Message}"); }
    }

    private static void ProbeCollision()
    {
        try
        {
            if (_village == null) { L("no Village_c captured yet — stand on a field map and press again."); return; }
            if (!ResolveProbes()) { L("neither collision method resolved; NOTHING was measured."); return; }

            var player = UiText.Member(_village, "player");
            int px = NavProbeShared.ToInt(UiText.Member(player, "map_x"));
            int py = NavProbeShared.ToInt(UiText.Member(player, "map_y"));
            var map = UiText.Member(_village, "mapVillage");
            var tat = UiText.Member(_village, "map_tatemono");
            int tn = NavProbeShared.CountOf(tat);

            L("=============== BEGIN COLLISION MEASUREMENT ===============");
            L($"player cell=({px},{py}) chip=({px >> 1},{py >> 1}) dir={UiText.Member(player, "dir")} " +
              $"tatemono_flg={UiText.Member(player, "tatemono_flg")} tatemono_no={UiText.Member(player, "tatemono_no")}");
            L($"map width={UiText.Member(map, "width")} height={UiText.Member(map, "height")} (chips) " +
              $"layer_no={UiText.Member(map, "layer_no")} map_tatemono count={tn} " +
              $"mapKey={Gsd1Names.MapKey(_village)}");
            L($"window: cells x {px - WinX}..{px + WinX}, y {py - WinY}..{py + WinY}. " +
              "Legend: '.' = 0/open, '#' = blocked, hex digit = the value returned, " +
              "'+' = >15, '?' = off-map or call failed, [] = the player's own cell.");

            // ── 1. ground, per cell, exactly as A* sees it ──────────────────
            if (_gndAtari == null) L("--- GROUND: SKIPPED, GNDAtariCheck did not resolve. Measured nothing. ---");
            else
            {
                // CELL coordinates, unshifted, and non-zero means solid — the
                // rule settled against 233 walked cells. This window used to
                // halve its input and pick a quadrant bit out of the answer,
                // which drew a different part of the map from the one the router
                // was searching and helped hide the bug it was meant to find.
                L("--- GROUND, blocked per CELL: GNDAtariCheck(c,r) != 0 ---");
                for (int r = py - WinY; r <= py + WinY; r++)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int c = px - WinX; c <= px + WinX; c++)
                    {
                        int mask = (c < 0 || r < 0) ? -1 : GndMask(c, r);
                        char ch = mask < 0 ? '?' : mask != 0 ? '#' : '.';
                        bool self = c == px && r == py;
                        sb.Append(self ? '[' : ' ').Append(ch).Append(self ? ']' : ' ');
                    }
                    L($"  gnd cell y={r,3}:{sb}");
                }
                L("--- GROUND raw masks per CELL (4 bits: the cell's own quarters) ---");
                for (int r = py - WinY; r <= py + WinY; r++)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int c = px - WinX; c <= px + WinX; c++)
                    {
                        int mask = (c < 0 || r < 0) ? -1 : GndMask(c, r);
                        bool self = c == px && r == py;
                        sb.Append(self ? '[' : ' ').Append(mask < 0 ? " ?" : mask.ToString().PadLeft(2)).Append(self ? ']' : ' ');
                    }
                    L($"  gnd mask y={r,3}:{sb}");
                }
            }

            // ── 2. buildings: summarise every space × level first ───────────
            if (_bldAtari == null) L("--- BUILDINGS: SKIPPED, BLDAtariCheck did not resolve. Measured nothing. ---");
            else
            {
                int cells = (2 * WinX + 1) * (2 * WinY + 1);
                L($"--- BUILDINGS: summary over the {cells}-cell window, every space x level ---");
                var informative = new System.Collections.Generic.List<int[]>();
                for (int s = 0; s < 3; s++)
                {
                    for (int level = 0; level <= MaxLevel; level++)
                    {
                        if (_bldMissing) break;
                        int hits = 0, seen = 0, self = -1;
                        var vals = new System.Collections.Generic.SortedSet<int>();
                        for (int r = py - WinY; r <= py + WinY; r++)
                            for (int c = px - WinX; c <= px + WinX; c++)
                            {
                                if (!Bld(Q(s, c), Q(s, r), level, out int ret, out _)) continue;
                                seen++; vals.Add(ret);
                                if (ret != 0) hits++;
                                if (c == px && r == py) self = ret;
                            }
                        L($"  space={SpaceName[s]} level={level}: {hits} of {seen} cells non-zero, " +
                          $"player's own cell={self}, distinct returns={{{string.Join(",", vals)}}}");
                        if (hits > 0 && hits < seen) informative.Add(new[] { s, level });
                        else L($"    (not drawn: {(hits == 0 ? "blocks nothing" : "blocks every cell")} — " +
                               "that IS the measurement, not a missing one)");
                    }
                }

                // ── 3. draw the combinations that carry information ─────────
                int drawn = 0;
                foreach (var combo in informative)
                {
                    if (drawn >= 4)
                    { L($"  (…{informative.Count - 4} further informative combination(s) not drawn; the summary above lists them)"); break; }
                    drawn++;
                    int s = combo[0], level = combo[1];
                    L($"--- BUILDINGS space={SpaceName[s]} level={level}: return value per cell ---");
                    for (int r = py - WinY; r <= py + WinY; r++)
                    {
                        var sb = new System.Text.StringBuilder();
                        for (int c = px - WinX; c <= px + WinX; c++)
                        {
                            int ret = Bld(Q(s, c), Q(s, r), level, out int v, out _) ? v : -1;
                            bool self = c == px && r == py;
                            sb.Append(self ? '[' : ' ').Append(Hex(ret)).Append(self ? ']' : ' ');
                        }
                        L($"  bld {SpaceName[s]} y={r,3}:{sb}");
                    }

                    // ── 4. the out-parameter: WHICH entry blocked, and where
                    //      that entry says it is. This is the comparison that
                    //      settles the coordinate space.
                    L($"--- BUILDINGS space={SpaceName[s]} level={level}: blocking entries, with their own stated position ---");
                    int listed = 0;
                    for (int r = py - WinY; r <= py + WinY && listed < 14; r++)
                        for (int c = px - WinX; c <= px + WinX && listed < 14; c++)
                        {
                            if (!Bld(Q(s, c), Q(s, r), level, out int ret, out int tno) || ret == 0) continue;
                            listed++;
                            L($"    queried ({Q(s, c)},{Q(s, r)}) for cell ({c},{r}) -> ret={ret} {TatDesc(tat, tn, tno)}");
                        }
                    if (listed == 0) L("    none — the window has no blocking entry to name.");
                }
                if (informative.Count == 0)
                    L("  NO combination of space and level was informative: every one blocked the whole " +
                      "window or none of it. That is a real result — the building layer is not a per-cell " +
                      "query in any of these three spaces at levels 0-" + MaxLevel + ".");
            }

            // ── 5. nearby TATEMONO, for offline rasterisation ───────────────
            if (tn <= 0) L($"--- map_tatemono: count={tn}. Nothing to cross-check against. ---");
            else
            {
                L($"--- map_tatemono entries within 16 cells of the player (of {tn} total) ---");
                int shown = 0;
                for (int i = 0; i < tn && shown < 28; i++)
                {
                    var b = UiText.Element(tat, i);
                    if (b == null) continue;
                    int tx = NavProbeShared.ToInt(UiText.Member(b, "map_x"));
                    int ty = NavProbeShared.ToInt(UiText.Member(b, "map_y"));
                    if (Math.Abs(tx - px) > 16 || Math.Abs(ty - py) > 16) continue;
                    shown++;
                    L($"    tat[{i}] pos=({tx},{ty}) size=({UiText.Member(b, "size_x")},{UiText.Member(b, "size_y")}) " +
                      $"id={UiText.Member(b, "id")} lvl={UiText.Member(b, "level")} pri={UiText.Member(b, "pri")} " +
                      $"ofs=({UiText.Member(b, "t_ofx")},{UiText.Member(b, "t_ofy")})");
                }
                if (shown == 0) L("    none within 16 cells — every entry on this map is further away than that.");
            }

            L("=============== END COLLISION MEASUREMENT ===============");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] F12 probe failed: {e.Message}"); }
    }

    // F11: the decisive GSD1 dump — the flat collision grid around the player.
    private static void Dump()
    {
        try
        {
            if (_village == null) { Plugin.Logger.LogInfo("[Nav1] F11: no Village_c captured yet."); return; }

            var player = UiText.Member(_village, "player");
            int tx = NavProbeShared.ToInt(UiText.Member(player, "map_x"));
            int ty = NavProbeShared.ToInt(UiText.Member(player, "map_y"));
            Plugin.Logger.LogInfo($"[Nav1] F11: player tile=({tx},{ty}) " +
                $"dx={UiText.Member(player, "map_dx")} dy={UiText.Member(player, "map_dy")} dir={UiText.Member(player, "dir")}");

            var vil = UiText.Member(_village, "vil");
            var mapTable = vil != null ? UiText.Member(vil, "map_table") : null;
            int mapCount = NavProbeShared.CountOf(mapTable);
            Plugin.Logger.LogInfo($"[Nav1] F11: vil.map_table length={mapCount} " +
                $"(vil.map_kazu={(vil != null ? UiText.Member(vil, "map_kazu") : null)})");

            // Report every MAP in the table rather than assuming index 0 — the
            // GSD2 probe assumed an index and printed an empty window for it.
            for (int i = 0; i < Math.Max(mapCount, 0) && i < 20; i++)
            {
                var map = UiText.Element(mapTable, i);
                if (map == null) { Plugin.Logger.LogInfo($"[Nav1] F11:  map[{i}] null"); continue; }

                int w = NavProbeShared.ToInt(UiText.Member(map, "width"));
                int h = NavProbeShared.ToInt(UiText.Member(map, "height"));
                var atari = UiText.Member(map, "atari_data");
                int alen = NavProbeShared.CountOf(atari);

                // atari_data is far too small to be per-tile (352 bytes for an
                // 80x48 = 3840-tile map), so log the pattern/index counts that
                // would make it a per-PATTERN table instead: index maps a
                // position to a chip id, and atari_data is keyed by that id.
                int idxLen = NavProbeShared.CountOf(UiText.Member(map, "index"));
                int idx2Len = NavProbeShared.CountOf(UiText.Member(map, "index2"));
                int patKazu = NavProbeShared.ToInt(UiText.Member(map, "pat_kazu"));
                int wNew = NavProbeShared.ToInt(UiText.Member(map, "width_new"));
                int hNew = NavProbeShared.ToInt(UiText.Member(map, "height_new"));
                Plugin.Logger.LogInfo($"[Nav1] F11:  map[{i}] w={w} h={h} (w*h={w * h}) wNew={wNew} hNew={hNew} " +
                                      $"atari={alen} index={idxLen} index2={idx2Len} pat_kazu={patKazu}");

                // Value histogram of atari_data: a collision/attribute table has
                // few distinct values.
                if (alen > 0 && alen <= 4096)
                {
                    var hist = new System.Collections.Generic.Dictionary<int, int>();
                    for (int k = 0; k < alen; k++)
                    {
                        int v = NavProbeShared.ToInt(UiText.Element(atari, k));
                        hist.TryGetValue(v, out int had);
                        hist[v] = had + 1;
                    }
                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var kv in hist) parts.Add($"{kv.Key}x{kv.Value}");
                    parts.Sort();
                    Plugin.Logger.LogInfo($"[Nav1] F11:   map[{i}] atari hist ({hist.Count} distinct): " +
                        (hist.Count <= 16 ? string.Join(" ", parts) : $"{hist.Count} values"));
                }

                // Window the grid only on a map whose dimensions actually contain
                // the player's tile.
                if (alen > 0 && w > 0 && h > 0 && tx < w && ty < h)
                {
                    Plugin.Logger.LogInfo($"[Nav1] F11:  map[{i}] window around ({tx},{ty}), player cell in brackets:");
                    for (int r = ty - 4; r <= ty + 4; r++)
                    {
                        if (r < 0 || r >= h) continue;
                        var sb = new System.Text.StringBuilder();
                        for (int c = tx - 6; c <= tx + 6; c++)
                        {
                            if (c < 0 || c >= w) { sb.Append("   . "); continue; }
                            int flat = r * w + c;
                            if (flat < 0 || flat >= alen) { sb.Append("   ? "); continue; }
                            string cell = NavProbeShared.ToInt(UiText.Element(atari, flat)).ToString().PadLeft(3);
                            sb.Append(c == tx && r == ty ? "[" + cell + "]" : " " + cell + " ");
                        }
                        Plugin.Logger.LogInfo("[Nav1] F11:   row " + r.ToString().PadLeft(3) + ": " + sb);
                    }
                }
            }

            // NPCs, for landmark naming later.
            var humans = UiText.Member(_village, "map_human");
            int hc = NavProbeShared.CountOf(humans);
            Plugin.Logger.LogInfo($"[Nav1] F11: map_human count={hc}");
            for (int i = 0; i < Math.Min(Math.Max(hc, 0), 12); i++)
            {
                var hm = UiText.Element(humans, i);
                Plugin.Logger.LogInfo($"[Nav1] F11:  human[{i}] map_x={UiText.Member(hm, "map_x")} map_y={UiText.Member(hm, "map_y")} " +
                    $"chara_no={UiText.Member(hm, "chara_no")} map_event_no={UiText.Member(hm, "map_event_no")} dir={UiText.Member(hm, "dir")}");
            }
            Plugin.Logger.LogInfo("[Nav1] F11: --- end GSD1 dump ---");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Nav1] F11 dump failed: {e.Message}"); }
    }
}

/// <summary>Small conversions shared by the GSD1 and GSD2 probes.</summary>
internal static class NavProbeShared
{
    public static int ToInt(object o) { try { return Convert.ToInt32(o); } catch { return int.MinValue; } }

    // Il2Cpp List<T> exposes Count; Il2CppReferenceArray<T>/Il2CppStructArray<T>
    // expose Length instead. Checking only Count made every array read as -1.
    public static int CountOf(object list)
    {
        if (list == null) return -1;
        foreach (var name in new[] { "Count", "Length" })
        {
            try
            {
                var prop = list.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null) return Convert.ToInt32(prop.GetValue(list));
            }
            catch { }
        }
        return -1;
    }
}
