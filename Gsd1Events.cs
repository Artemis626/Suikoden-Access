using System;
using System.Collections.Generic;
using System.Text;

namespace SuikodenAccess;

// ─────────────────────────────────────────────────────────────────────────────
// GSD1 MAP EVENTS — the real source of exits, doorways and interactable objects.
//
// Village_c.vil_ekp is a List<EVENT_WK>, one entry per map event:
//
//     ushort x, y          sprite / contact tile  (sentinels: 0,0  254,254  255,255)
//     ushort putno         person slot, 255 = not a person
//     ushort image         sprite id — equals MAP.hito_data[..].fp_no, so it is a
//                          STABLE character identity, unlike a slot index
//     ushort flag1
//     byte[] eventForm     WHEN this event fires   (EVENT_CHECK opcodes)
//     byte[] eventCom      WHAT it does            (EVENT_COMMAND opcodes)
//
// Both byte arrays are scripts, and GSD1 ships the two enums that name their
// opcodes. Confirmed against a live dump of town A map 3 — all 32 entries
// parse to exactly their declared length with the widths below:
//
//   FORM   EVENTTAP(5) 0   PUSHKEY(4) 0   CHKEND(6) 0/terminator
//          PX_NEW(11) 2    PY_NEW(12) 2   EVENTFCHK(7) 2   EVENTFCHK_NO(8) 2
//   COM    VILLAGEOUT(3) 3 = destination map, destination x, destination y
//
// The proof that VILLAGEOUT really is a warp, and that its operands are in the
// player's own coordinate space: on that map, event 0's form reads
// PX_NEW 18..20 / PY_NEW 35..36 and its command reads EVENTSND 117,
// VILLAGEOUT 1,17,36 — and the play log shows the player walking off that
// rectangle and arriving on the map whose fingerprint matches map_table[1], at
// tile (17,36) exactly. Event 1 does the same for (26..28, 35..36) -> (29,36).
//
// So exits are STATIC DATA with known destinations. They no longer have to be
// discovered by walking through them (Gsd1Exits), and a doorway is targetable
// the first time a player enters a room.
//
// WHY THE SCRIPTS ARE ONLY PARTLY DECODED, ON PURPOSE
// Operand widths are known for certain only for the opcodes above. A wrong
// width silently desynchronises the walk and invents plausible-looking
// coordinates, which is worse than reading nothing — a wrong exit sends a blind
// player across the map to a wall. So the walker uses CONFIRMED widths only and
// STOPS at the first opcode it does not know, marking the result partial. A
// destination is reported only when it was found during a clean walk. Unknown
// opcodes are logged with their event index so the table can be extended from
// evidence rather than guessed at.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd1Events
{
    internal enum Kind { Ignore, Exit, Trigger, Object, Person }

    internal sealed class MapEvent
    {
        public int Index;                    // position in vil_ekp — stable, static data
        public int PutNo, Image, Flag1;
        public int TileX, TileY;             // where to send the player
        public Kind Class;

        /// <summary>EVENT_WK.x / EVENT_WK.y exactly as the game stores them,
        /// before Classify moves TileX/TileY to a region centre. This is the
        /// only field pair that matches Event_c.eventWK for EVERY kind of event,
        /// which is what lets a running event be tied back to a table entry.</summary>
        public int RawX = -1, RawY = -1;

        public bool HasRegion;               // form carried PX_NEW / PY_NEW
        public int Rx0, Rx1, Ry0, Ry1;

        /// <summary>The FORM script's story-flag conditions, as
        /// {opcode, operand a, operand b}. Empty means unconditional.</summary>
        public readonly List<int[]> Checks = new List<int[]>();
        public bool HasTap, HasPushKey;

        /// <summary>Every alternative script covering this PLACE, as one
        /// condition list each — this event's own Checks first, then those of
        /// the entries SamePlace folded into it.</summary>
        /// <remarks>
        /// A building entrance routinely carries two or three scripts gated on
        /// DIFFERENT flags, and only one of them is kept as the target. Asking
        /// whether the place is dead therefore has to ask about all of them: if
        /// the kept script is the finished cutscene and the folded-away sibling
        /// is the everyday one, judging the place by the kept script alone would
        /// delete a door that works. A place is dead only when EVERY alternative
        /// is dead, and an alternative with no conditions is always alive.
        /// </remarks>
        public readonly List<List<int[]>> Alternatives = new List<List<int[]>>();

        public bool HasWarp;                 // com carried a VILLAGEOUT during a clean walk
        public int WarpMap, WarpX, WarpY;
        public bool ComPartial, FormPartial;
        public int StoppedAt;                // opcode that ended the walk, -1 if none

        /// <summary>Every cell of the trigger rectangle, nearest-first at use.
        /// A region is a doorway a few cells wide and its centre is often the
        /// door frame itself; offering the whole rectangle to the router is what
        /// turns "no path" into a route.</summary>
        public List<int[]> Cells = new List<int[]>();

        /// <summary>Operand of a trailing EVENTSHOP (0x43), or -1. See the note
        /// on service dispatch below.</summary>
        public int ServiceType = -1;
    }

    // ── opcode widths ───────────────────────────────────────────────────────
    // Operand counts VERIFIED by parsing every event on a real map to exactly its
    // declared array length. Anything absent here stops the walk; nothing is
    // guessed by analogy, because a wrong width produces confident nonsense.
    private const int CHKEND = 0x06;
    private const int PX_NEW = 0x0b, PY_NEW = 0x0c, EVENTTAP = 0x05, PUSHKEY = 0x04;
    private const int EVENTFCHK = 0x07, EVENTFCHK_NO = 0x08;

    private static readonly Dictionary<int, int> FormWidth = new Dictionary<int, int>
    {
        { EVENTTAP, 0 }, { PUSHKEY, 0 }, { CHKEND, 0 },
        { PX_NEW, 2 }, { PY_NEW, 2 }, { EVENTFCHK, 2 }, { EVENTFCHK_NO, 2 },
    };

    private const int VILLAGEOUT = 0x03, COMMANDEND = 0xfd, EVENTSTAIRS = 0x21;

    private static readonly Dictionary<int, int> ComWidth = new Dictionary<int, int>
    {
        { 0x02, 0 },        // PUSHMARU
        { VILLAGEOUT, 3 },  // dest map, dest x, dest y
        { 0x0c, 0 },        // MWINCL
        { 0x0b, 1 },        // MWINOP
        { 0x17, 1 },        // SWINOP
        { 0x18, 0 },        // SWINCL
        { 0x41, 1 },        // EVENTSND
        { 0x46, 0 },        // EVENTWWAIT
        { COMMANDEND, 0 },
    };

    // Deliberately NOT in ComWidth: EVENTMSG(0x04), STORYMSG(0x1c), EVENTNAME(0x12)
    // and SETHUMANDIR(0x11). Their widths cannot be pinned from the sample —
    // several combinations parse the same scripts to the same total length — and
    // an ambiguous width is exactly the kind of guess that would fabricate a
    // destination. Scripts containing them stop early and are marked partial;
    // that costs nothing, because the exits observed so far carry VILLAGEOUT in
    // the first few bytes, before any message opcode.

    private static readonly List<MapEvent> _events = new List<MapEvent>();
    private static readonly Dictionary<int, int> _imageByPutNo = new Dictionary<int, int>();
    private static string _cachedFor;        // map key the cache was built for
    private static int _cachedCount = -1;

    /// <summary>
    /// The sprite id of the person occupying a slot, or 0 if unknown.
    /// </summary>
    /// <remarks>
    /// A person's SCRIPT is on the event; their POSITION is on the matching
    /// HUMAN in Village_c.map_human, linked by map_event_no == putno (verified:
    /// map_human's thirteen entries reported ev = 0..12 against putno 0..12 in
    /// vil_ekp). EVENT_WK.image is that person's sprite id and equals
    /// MAP.hito_data[..].fp_no, so it identifies WHO occupies a slot even though
    /// HUMAN.chara_no reads 0 for everyone.
    ///
    /// That matters because the existing slot-reuse guard does not work: the
    /// sprite fingerprint it relies on read 0.0.0.0.0.0 for every NPC in play, so
    /// it could never detect that a slot's occupant had changed, and a name
    /// learned for "person 4" would quietly transfer to whoever took slot 4 next.
    /// image varies between characters and so can.
    /// </remarks>
    public static int ImageForPutNo(int putNo)
    {
        _imageByPutNo.TryGetValue(putNo, out int img);
        return img;
    }

    /// <summary>Events on the current map. Rebuilt when the map key changes.</summary>
    public static List<MapEvent> ForCurrentMap(object village, string mapKey)
    {
        var ekp = UiText.Member(village, "vil_ekp");
        int n = NavProbeShared.CountOf(ekp);

        // The count is part of the cache identity as well as the key: a room can
        // reload with the same key, and a stale event list would point at tiles
        // that are no longer there.
        if (mapKey != null && mapKey == _cachedFor && n == _cachedCount) return _events;

        _events.Clear();
        _imageByPutNo.Clear();
        _cachedFor = mapKey;
        _cachedCount = n;
        if (n <= 0) return _events;

        var unknownForm = new HashSet<int>();
        var unknownCom = new HashSet<int>();

        for (int i = 0; i < n; i++)
        {
            var raw = UiText.Element(ekp, i);
            if (raw == null) continue;

            var e = new MapEvent
            {
                Index = i,
                PutNo = ReadInt(raw, "putno"),
                Image = ReadInt(raw, "image"),
                Flag1 = ReadInt(raw, "flag1"),
                Rx0 = -1, Rx1 = -1, Ry0 = -1, Ry1 = -1,
                WarpMap = -1, WarpX = -1, WarpY = -1,
                StoppedAt = -1,
            };

            if (e.PutNo != 255 && e.Image > 0 && !_imageByPutNo.ContainsKey(e.PutNo))
                _imageByPutNo[e.PutNo] = e.Image;

            int rawX = ReadInt(raw, "x"), rawY = ReadInt(raw, "y");
            e.RawX = rawX; e.RawY = rawY;
            ParseForm(e, UiText.Member(raw, "eventForm"), unknownForm);
            var comArr = UiText.Member(raw, "eventCom");
            ParseCom(e, comArr, unknownCom);
            ScanService(e, comArr);
            Classify(e, rawX, rawY);
            if (e.Class == Kind.Ignore || e.Class == Kind.Person) continue;

            // Several events routinely cover one trigger rectangle — a building
            // entrance on the town map had two, and a third carried the person
            // standing in the doorway. They are alternative scripts for the same
            // PLACE (usually gated on different story flags), so they are one
            // destination, not several. The first is kept, except that an entry
            // which decoded a destination or a service replaces one that did not.
            //
            // This used to compare only the rectangles' CENTRES, which missed the
            // common case: a staircase on the mansion map is ev[1], region
            // x16-18 y34-35, decoding VILLAGEOUT to map 2 — and ev[19], region
            // x16-19 y34-36, one cell taller and wider, whose script stops at an
            // undecodable opcode. Same staircase, centres one cell apart, so it
            // was announced twice, once as an exit and once as an event, and the
            // two read as different places.
            var dup = _events.Find(o => SamePlace(o, e));
            if (dup != null)
            {
                bool better = (e.HasWarp && !dup.HasWarp) ||
                              (e.ServiceType >= 0 && dup.ServiceType < 0);
                Plugin.Logger.LogInfo($"[Nav1]   ev[{e.Index}] is the same place as ev[{dup.Index}]" +
                                      (better ? " and replaces it (it decoded more)" : "; dropped") +
                                      $" (its conditions are kept as an alternative: " +
                                      (e.Checks.Count == 0 ? "none — this place can always fire" : $"{e.Checks.Count}") + ")");

                // The loser's CONDITIONS survive even though its script does not.
                // See MapEvent.Alternatives: judging a place by one of several
                // gated scripts is how a working door gets deleted.
                var winner = better ? e : dup;
                var loser = better ? dup : e;
                if (better)
                {
                    // The replacement has not been through the Add path, so it
                    // does not yet carry its own conditions; the one it replaces
                    // brings the alternatives already folded into it.
                    _events[_events.IndexOf(dup)] = e;
                    e.Alternatives.Add(e.Checks);
                    foreach (var alt in dup.Alternatives) e.Alternatives.Add(alt);
                }
                else winner.Alternatives.Add(loser.Checks);
                continue;
            }
            e.Alternatives.Add(e.Checks);
            _events.Add(e);
        }

        Plugin.Logger.LogInfo($"[Nav1] events on {mapKey}: {n} in vil_ekp -> {_events.Count} usable " +
                              $"({Count(Kind.Exit)} exit, {Count(Kind.Object)} object, " +
                              $"{Count(Kind.Person)} person, {Count(Kind.Trigger)} trigger)");
        foreach (var e in _events)
        {
            Plugin.Logger.LogInfo("[Nav1]   " + Describe(e));

            // Log the zero: an event with NO conditions is stated as such, so
            // "unconditional" never looks the same as "we did not look".
            int conds = 0;
            foreach (var alt in e.Alternatives) conds += alt.Count;
            if (conds == 0)
                Plugin.Logger.LogInfo($"[Nav1]     ev[{e.Index}] no story-flag condition in any of its " +
                                      $"{e.Alternatives.Count} script(s) — always live");
            else
            {
                for (int a = 0; a < e.Alternatives.Count; a++)
                {
                    var alt = e.Alternatives[a];
                    if (alt.Count == 0)
                    {
                        Plugin.Logger.LogInfo($"[Nav1]     ev[{e.Index}] alt {a}: unconditional — this place is always live");
                        continue;
                    }
                    foreach (var c in alt)
                        Plugin.Logger.LogInfo($"[Nav1]     ev[{e.Index}] alt {a}: {Gsd1Flags.Describe(c[0], c[1], c[2])}");
                }
                var can = Gsd1Flags.CanFireAny(e.Alternatives);
                Plugin.Logger.LogInfo($"[Nav1]     ev[{e.Index}] tmp-bank rule says " +
                    (can == null ? "UNKNOWN (flags unreadable) — nothing is dropped"
                                 : can.Value ? "it CAN still fire — kept"
                                             : (Gsd1Flags.DropDeadEvents
                                                 ? "it can NEVER fire again — DROPPED"
                                                 : "it can NEVER fire again — WOULD BE DROPPED (report mode; nothing dropped)")));
            }
        }
        if (unknownForm.Count > 0) Plugin.Logger.LogInfo($"[Nav1]   unknown FORM opcodes (walk stopped): {Hexes(unknownForm)}");
        if (unknownCom.Count > 0) Plugin.Logger.LogInfo($"[Nav1]   unknown COM opcodes (walk stopped): {Hexes(unknownCom)}");
        return _events;
    }

    /// <summary>Are these two events alternative scripts for one physical place?</summary>
    /// <remarks>
    /// Overlapping rectangles cover the same cells, so they are the same place by
    /// construction. The extra "centres within two cells" test is there to stop a
    /// large scene trigger from swallowing a small one that merely clips its
    /// edge: two doorway scripts differ by a cell or two, a room-wide cutscene
    /// trigger overlapping a doorway does not.
    ///
    /// A point event and a region event are never merged. That keeps the save
    /// desk — a tap object at one cell — out of any region trigger drawn over it.
    /// </remarks>
    private static bool SamePlace(MapEvent a, MapEvent b)
    {
        if (a.HasRegion != b.HasRegion)
            return false;
        if (!a.HasRegion)
            return a.TileX == b.TileX && a.TileY == b.TileY;
        bool overlap = a.Rx0 <= b.Rx1 && b.Rx0 <= a.Rx1 &&
                       a.Ry0 <= b.Ry1 && b.Ry0 <= a.Ry1;
        return overlap &&
               Math.Abs(a.TileX - b.TileX) + Math.Abs(a.TileY - b.TileY) <= 2;
    }

    private static int Count(Kind k) { int c = 0; foreach (var e in _events) if (e.Class == k) c++; return c; }

    private static string Hexes(HashSet<int> s)
    {
        var parts = new List<string>();
        foreach (var v in s) parts.Add("0x" + v.ToString("x2"));
        parts.Sort();
        return string.Join(" ", parts);
    }

    public static string Describe(MapEvent e)
    {
        var sb = new StringBuilder();
        sb.Append($"ev[{e.Index}] {e.Class} at ({e.TileX},{e.TileY})");
        if (e.HasRegion) sb.Append($" region x{e.Rx0}-{e.Rx1} y{e.Ry0}-{e.Ry1}");
        if (e.HasTap) sb.Append(" tap");
        if (e.HasPushKey) sb.Append(" pushkey");
        if (e.PutNo != 255) sb.Append($" putno={e.PutNo} image={e.Image}");
        if (e.HasWarp) sb.Append($" -> map {e.WarpMap} at ({e.WarpX},{e.WarpY})");
        if (e.ServiceType >= 0) sb.Append($" EVENTSHOP type={e.ServiceType}");
        if (e.ComPartial) sb.Append($" [com partial at 0x{e.StoppedAt:x2}]");
        return sb.ToString();
    }

    private static int ReadInt(object o, string field) => NavProbeShared.ToInt(UiText.Member(o, field));

    private static int Byte(object arr, int i) => NavProbeShared.ToInt(UiText.Element(arr, i)) & 0xff;

    // ── form: WHEN does this fire ───────────────────────────────────────────
    private static void ParseForm(MapEvent e, object form, HashSet<int> unknown)
    {
        int n = NavProbeShared.CountOf(form);
        int i = 0;
        while (i < n)
        {
            int op = Byte(form, i);
            if (op == CHKEND) break;                     // terminator; the rest is padding
            if (!FormWidth.TryGetValue(op, out int w)) { e.FormPartial = true; unknown.Add(op); break; }
            if (i + w >= n) { e.FormPartial = true; break; }

            switch (op)
            {
                case PX_NEW: e.Rx0 = Byte(form, i + 1); e.Rx1 = Byte(form, i + 2); e.HasRegion = true; break;
                case PY_NEW: e.Ry0 = Byte(form, i + 1); e.Ry1 = Byte(form, i + 2); e.HasRegion = true; break;
                case EVENTTAP: e.HasTap = true; break;
                case PUSHKEY: e.HasPushKey = true; break;

                // The FORM script is WHEN an event fires, and these two opcodes
                // are its story-flag conditions. They were walked over and
                // discarded until now, which is why the list contains scenes
                // that have already happened: a dining-hall cutscene whose
                // trigger is still in the table, gated on a flag that will never
                // be true again, and a fireplace listed as an interactable
                // object that does nothing when you press against it.
                case EVENTFCHK:
                case EVENTFCHK_NO:
                    e.Checks.Add(new[] { op, Byte(form, i + 1), Byte(form, i + 2) });
                    break;
            }
            i += 1 + w;
        }
        // A region needs both axes to be a rectangle. One axis alone is a stripe
        // across the whole map and is not a place to walk to.
        if (e.Rx0 < 0 || e.Ry0 < 0) e.HasRegion = false;
    }

    // ── com: WHAT does it do ────────────────────────────────────────────────
    private static void ParseCom(MapEvent e, object com, HashSet<int> unknown)
    {
        int n = NavProbeShared.CountOf(com);
        int i = 0;
        while (i < n)
        {
            int op = Byte(com, i);
            if (op == COMMANDEND) break;
            if (!ComWidth.TryGetValue(op, out int w))
            { e.ComPartial = true; e.StoppedAt = op; unknown.Add(op); break; }
            if (i + w >= n) { e.ComPartial = true; break; }

            if (op == VILLAGEOUT && !e.HasWarp)
            {
                e.HasWarp = true;
                e.WarpMap = Byte(com, i + 1);
                e.WarpX = Byte(com, i + 2);
                e.WarpY = Byte(com, i + 3);
            }
            i += 1 + w;
        }
    }

    // ── service dispatch (shops, inns, and the save diary) ──────────────────
    // GSD1 declares the service kinds as constants on G1_event_h: SHOP_OMISE,
    // SHOP_KAJIYA, SHOP_MONSHO, SHOP_KANTEI, SHOP_YADOYA, **SHOP_SAVE**,
    // SHOP_SUTERU, SHOP_MINIMAP, SHOP_HANYOU, SHOP_EFUNC, USER_MENU. So the game
    // does know which interactable is a save point — the question is only how to
    // read it off an event.
    //
    // EVENTSHOP is command 0x43, and the save diary's script ENDS with
    // "43 07 ff" — the only 0x43 among the 32 events on that map. That is where
    // a service dispatch belongs: the terminal action of an interaction script.
    //
    // It is recorded but NOT yet used to name anything, because the operand
    // widths needed to prove the byte is in opcode position cannot be recovered:
    // a script's length constrains only the SUM of its operand counts, and with
    // roughly eight distinct opcodes per script and one equation per script the
    // system is underdetermined by construction, not by a weak search. Naming
    // from an unproven parse risks calling a bookshelf a save point, and a wrong
    // name is worse than no name.
    //
    // What IS used is Gsd1Path's behaviour-based naming, which cannot be wrong.
    // Once a log shows SHOP_SAVE's real value alongside the events this finds,
    // the static rule can be promoted and save points will be named on maps the
    // player has never visited.
    private const int EVENTSHOP = 0x43;

    // CONFIRMED at runtime: G1_event_h reads USER_MENU=1 SHOP_OMISE=2
    // SHOP_KAJIYA=3 SHOP_MONSHO=4 SHOP_KANTEI=5 SHOP_YADOYA=6 SHOP_SAVE=7
    // SHOP_SUTERU=8 SHOP_MINIMAP=9 SHOP_HANYOU=255 SHOP_EFUNC=128 — and the save
    // diary's script ends "43 07 ff", which is EVENTSHOP followed by exactly
    // SHOP_SAVE. That closes the loop: a service point names itself from static
    // map data, with no need to interact with it first and no play session to
    // learn it.
    //
    // 255 and 128 are deliberately absent from this table. SHOP_HANYOU (255) and
    // SHOP_EFUNC (128) are generic dispatch slots whose meaning depends on data
    // this does not read, and 255 is also the commonest filler byte in these
    // scripts — accepting it would put a shop name on anything.
    private static readonly Dictionary<int, string> ServiceName = new Dictionary<int, string>
    {
        { 1, "menu" }, { 2, "shop" }, { 3, "blacksmith" }, { 4, "rune shop" },
        { 5, "appraiser" }, { 6, "inn" }, { 7, "save point" }, { 8, "storage" },
        { 9, "map" },
    };

    /// <summary>A spoken name for this event's service, or null.</summary>
    public static string ServiceLabel(MapEvent e) =>
        e.ServiceType >= 0 && ServiceName.TryGetValue(e.ServiceType, out var n) ? n : null;

    private static void ScanService(MapEvent e, object com)
    {
        int n = NavProbeShared.CountOf(com);
        if (n < 3) return;
        // Walk back over the COMMANDEND padding to the real end of the script,
        // then look only at the tail. Scanning the whole array would match any
        // 0x43 that happened to be an operand.
        int last = n - 1;
        while (last >= 0 && Byte(com, last) == COMMANDEND) last--;
        for (int i = Math.Max(0, last - 3); i < last; i++)
            if (Byte(com, i) == EVENTSHOP) { e.ServiceType = Byte(com, i + 1); return; }
    }

    // ── classification ──────────────────────────────────────────────────────
    // 0,0 / 254,254 / 255,255 are the "not placed" markers seen in the dumps:
    // unspawned NPC slots and events whose position lives in the form instead.
    private static bool Placed(int x, int y) => x > 0 && y > 0 && x < 254 && y < 254;

    private static void Classify(MapEvent e, int rawX, int rawY)
    {
        // A rectangle the player walks onto. The centre is spoken as the
        // target's position, but EVERY cell of the rectangle is offered to the
        // router: a 3x2 doorway trigger often has its centre on the door frame,
        // and aiming only there produced "no path" for exits that are plainly
        // walkable through.
        if (e.HasRegion)
        {
            e.TileX = (e.Rx0 + e.Rx1) / 2;
            e.TileY = (e.Ry0 + e.Ry1) / 2;
            for (int y = e.Ry0; y <= e.Ry1 && e.Cells.Count < 64; y++)
                for (int x = e.Rx0; x <= e.Rx1 && e.Cells.Count < 64; x++)
                    e.Cells.Add(new[] { x, y });
            e.Class = e.HasWarp ? Kind.Exit : Kind.Trigger;
            return;
        }

        if (e.HasTap && Placed(rawX, rawY))
        {
            // A person's event carries their SCRIPT; their POSITION is on the
            // matching HUMAN in map_human. The x/y here is only where they were
            // first placed, and a wandering NPC leaves it immediately.
            //
            // Classifying these as targets listed every townsperson TWICE — once
            // live from map_human and once frozen at this spawn point (29 and 23
            // entries on the same town map). Cycling then alternated between the
            // two copies of the same person, so a target three steps east became
            // one twenty steps away in another direction on the next keypress.
            // Person events are recorded for their `image` only (see
            // ImageForPutNo) and are never targets.
            if (e.PutNo != 255 && e.Image > 0) { e.Class = Kind.Person; return; }

            e.TileX = rawX; e.TileY = rawY;
            e.Cells.Add(new[] { rawX, rawY });
            // putno 255 means no person occupies this event, so what the player
            // interacts with is scenery: a diary, a bookshelf, a sign. That is
            // exactly the category the mod could not reach before.
            e.Class = Kind.Object;
            return;
        }

        e.Class = Kind.Ignore;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// STORY FLAGS — why the event list still contains things that do nothing
//
// An event's FORM script is WHEN it fires, and two of its opcodes are story-flag
// conditions: EVENTFCHK (0x07) and EVENTFCHK_NO (0x08), two operands each. The
// walker read straight past them, so an event whose scene has already played
// stayed in the list forever — a dining-hall cutscene trigger you can walk to and
// nothing happens, a fireplace listed as an interactable object that will not
// respond.
//
// GSD1 exposes the flag banks as static getters with value parameters only, the
// same acceptable category as GNDAtariCheck:
//
//   GAME_WORK.GetStoryFlagS(int index)              -> byte    the whole byte
//   GAME_WORK.GetStoryFlagS(int index, byte mask)   -> bool    one bit
//   GAME_WORK.GetTmpEventFlagS(int index)           -> byte
//   GAME_WORK.GetTmpEventFlagS(int index, byte mask)-> bool
//
// WHAT IS NOT YET KNOWN, and is therefore not acted on: which bank each opcode
// reads, whether the two operands are (index, mask) in that order, and which
// polarity means "may fire". Four readings are plausible and all four are
// consistent with the bytes. So this only REPORTS, next to each event, what every
// candidate answers — and the player has already supplied the ground truth that
// scores them: on the mansion maps, trigger 1 (ev[13] at 23,12) and object 1
// (ev[4] at 39,11) are dead, while both exits and the save point are live. The
// reading that calls exactly those two dead and nothing else is the right one.
//
// This is the §1.9 method again: generate candidates by reasoning, choose between
// them with an observation that cannot be reinterpreted.
//
// Event_c.GoEventForm(int, int) is the game's own FORM evaluator and would settle
// it in one call — but it is a "Go" method, not a query, and calling it could run
// the event rather than test it. Not called for that reason.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Gsd1Flags
{
    private static bool _resolved;
    private static System.Reflection.MethodInfo _storyBit, _storyByte, _tmpBit, _tmpByte;

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        // GSD1.dll explicitly: GAME_WORK is declared in BOTH games, and
        // FindAnywhere prefers GSD2, whose GAME_WORK has no GetStoryFlagS. That
        // is why the first run of this probe reported all four methods missing.
        var gw = Gsd.FindIn("GSD1.dll", "GAME_WORK") ?? Gsd.FindAnywhere("GAME_WORK");
        if (gw == null) { Plugin.Logger.LogInfo("[Nav1] GAME_WORK not found; story flags unreadable."); return; }

        foreach (var m in gw.GetMethods(System.Reflection.BindingFlags.Public |
                                        System.Reflection.BindingFlags.Static))
        {
            var ps = m.GetParameters();
            if (m.Name == "GetStoryFlagS")
            {
                if (ps.Length == 2) _storyBit = m; else if (ps.Length == 1) _storyByte = m;
            }
            else if (m.Name == "GetTmpEventFlagS")
            {
                if (ps.Length == 2) _tmpBit = m; else if (ps.Length == 1) _tmpByte = m;
            }
        }
        Plugin.Logger.LogInfo($"[Nav1] story flags: GetStoryFlagS(i,m)={(_storyBit != null)} " +
                              $"GetStoryFlagS(i)={(_storyByte != null)} " +
                              $"GetTmpEventFlagS(i,m)={(_tmpBit != null)} " +
                              $"GetTmpEventFlagS(i)={(_tmpByte != null)}");
    }

    private static string One(System.Reflection.MethodInfo m, params object[] args)
    {
        if (m == null) return "n/a";
        try { return Convert.ToString(m.Invoke(null, args)); }
        catch (Exception e) { return "threw:" + e.Message; }
    }

    /// <summary>Every candidate reading of one FORM flag check, side by side.</summary>
    public static string Describe(int op, int a, int b)
    {
        Resolve();
        string name = op == 0x07 ? "EVENTFCHK" : "EVENTFCHK_NO";

        // (index, mask) is now more than an assumption: across every event on
        // three maps, operand b is always a power of two — 4, 8, 16, 32, 64 —
        // and operand a is a small index. The swapped order is still printed
        // because it costs one line and settles the question rather than
        // arguing it.
        return $"{name}({a},{b}) " +
               $"story[{a}]={One(_storyByte, a)} story[{a}]&{b}={One(_storyBit, a, (byte)(b & 0xff))} " +
               $"tmp[{a}]={One(_tmpByte, a)} tmp[{a}]&{b}={One(_tmpBit, a, (byte)(b & 0xff))} " +
               $"| swapped story[{b}]&{a}={One(_storyBit, b, (byte)(a & 0xff))} " +
               $"tmp[{b}]&{a}={One(_tmpBit, b, (byte)(a & 0xff))}";
    }

    // ── THE RULE, AND WHAT IS STILL MISSING FROM ITS PROOF ──────────────────
    //
    // GetTmpEventFlagS, not GetStoryFlagS. The story bank reads 0 at every index
    // sampled and gets ev[4] wrong; the tmp bank has real values and matches both
    // targets the player independently reported as dead:
    //
    //   ev[13] dining hall  EVENTFCHK(1,16) holds, EVENTFCHK_NO(20,4) -> tmp[20]&4
    //                       is SET, so the NO condition fails -> cannot fire (player: dead)
    //   ev[4]  fireplace    EVENTFCHK_NO(1,16) -> tmp[1]=115, bit 16 set -> cannot fire (player: dead)
    //   ev[23] save point / ev[0], ev[1] exits  no conditions -> always live (player: live)
    //
    // So: EVENTFCHK requires the bit SET, EVENTFCHK_NO requires it CLEAR, every
    // condition in one script must hold, and a PLACE is live if ANY of its
    // alternative scripts can fire.
    //
    // THE HALF THAT IS NOT PROVEN. Every score above is on the DEAD side. No
    // event carrying conditions has yet been seen to FIRE. A rule with inverted
    // polarity would score exactly the same on these three and would then hide
    // every live conditional event while keeping precisely the dead ones — worse
    // than the bug it fixes. Zero violations is necessary, not sufficient
    // (NAV-STATUS 1.9); this is the same trap the collision work fell into four
    // times, and the same answer applies: find an observation that cannot be
    // reinterpreted and score against it.
    //
    // The observation is waiting to be collected: Event_c.eventWK names the event
    // the game is RUNNING. If an event with conditions runs, it fired, whatever
    // any reading says. NoteRunningEvent below watches for exactly that and logs
    // a CONFIRMS / CONTRADICTED line naming the event. One CONFIRMS line is what
    // DropDeadEvents is waiting for.
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Flip to true ONLY after the log has shown at least one
    /// "RULE CONFIRMS" line — a conditional event actually firing while the rule
    /// said it could. Until then this reports and drops nothing.</summary>
    public const bool DropDeadEvents = false;

    /// <summary>Can this one script's conditions all hold right now? Null when
    /// the flags cannot be read at all.</summary>
    public static bool? CanFire(List<int[]> checks)
    {
        Resolve();
        if (_tmpBit == null || checks == null) return null;
        if (checks.Count == 0) return true;             // unconditional
        try
        {
            foreach (var c in checks)
            {
                bool set = Convert.ToBoolean(_tmpBit.Invoke(null, new object[] { c[1], (byte)(c[2] & 0xff) }));
                bool want = c[0] == 0x07;               // EVENTFCHK wants set, EVENTFCHK_NO wants clear
                if (set != want) return false;
            }
            return true;
        }
        catch { return null; }
    }

    /// <summary>Can this PLACE still do anything — i.e. can any of its
    /// alternative scripts fire? Null when the flags cannot be read.</summary>
    public static bool? CanFireAny(List<List<int[]>> alternatives)
    {
        if (alternatives == null || alternatives.Count == 0) return null;
        bool sawUnknown = false;
        foreach (var alt in alternatives)
        {
            var one = CanFire(alt);
            if (one == null) { sawUnknown = true; continue; }
            if (one.Value) return true;                 // one live script is enough
        }
        // Unknown beats dead: an unreadable flag must never delete a target.
        return sawUnknown ? (bool?)null : false;
    }

    // ── the live-side confirmation ──────────────────────────────────────────
    // Event_c.eventWK is the EVENT_WK the game is currently running. Matching it
    // back to the map's table by the fields EVENT_WK actually stores — x, y,
    // putno, image — says WHICH entry fired. If that entry carries conditions,
    // the game has just answered the question the flag rule cannot answer about
    // itself, and no interpretation of the numbers can argue with it.
    //
    // Matching is on the RAW x/y, not the target position: Classify moves a
    // region event's TileX/TileY to the rectangle's centre, which is not what the
    // game stores.
    private static int _lastFiredSig = int.MinValue;
    private static string _watchMap;
    private static bool _confirmed, _contradicted;

    /// <summary>True once a conditional event has been seen to fire while the
    /// rule said it could. Logged, never spoken.</summary>
    public static bool RuleConfirmedLive => _confirmed;

    /// <summary>True once a conditional event fired that the rule called dead.</summary>
    public static bool RuleContradicted => _contradicted;

    public static void NoteRunningEvent(string mapKey, int rawX, int rawY, int putNo, int image,
                                        int eventNo, List<Gsd1Events.MapEvent> events)
    {
        if (rawX < 0 || rawY < 0) return;
        int sig = (rawX << 20) ^ (rawY << 10) ^ (putNo << 4) ^ image;

        // eventWK KEEPS the last event that ran — it is not cleared when the
        // event ends, which is why the save-point namer has to check the player
        // is standing next to what it matched. So only a CHANGE counts as a
        // firing, and the first value seen on a map is adopted silently: it may
        // be left over from the map before, and scoring a rule against a
        // leftover is how a false confirmation would get made.
        if (mapKey != _watchMap)
        {
            _watchMap = mapKey;
            _lastFiredSig = sig;
            Plugin.Logger.LogInfo($"[Nav1] event watch on {mapKey}: adopting eventWK ({rawX},{rawY}) " +
                                  $"putno={putNo} image={image} eventNo={eventNo} as pre-existing; " +
                                  "only a change from here is scored as a firing.");
            return;
        }

        if (sig == _lastFiredSig) return;               // the same event, still running
        _lastFiredSig = sig;

        Gsd1Events.MapEvent hit = null;
        if (events != null)
            foreach (var e in events)
                if (e.RawX == rawX && e.RawY == rawY && e.PutNo == putNo && e.Image == image) { hit = e; break; }

        if (hit == null)
        {
            // Log the zero: an event that ran and matched nothing is evidence
            // about the matching, and must not look like "we never watched".
            Plugin.Logger.LogInfo($"[Nav1] event running at raw ({rawX},{rawY}) putno={putNo} image={image} " +
                                  $"eventNo={eventNo} — no entry in this map's usable table matches it (it " +
                                  "may be a person event, or one folded into another place); nothing scored.");
            return;
        }

        // Event_c.eventNo is logged beside the matched index every time. If the
        // two always agree, the running event can be read directly instead of
        // matched by position — but that is a claim to be scored, not assumed,
        // so nothing depends on it yet.
        Plugin.Logger.LogInfo($"[Nav1] event running: matched ev[{hit.Index}] by position; " +
                              $"Event_c.eventNo reads {eventNo} " +
                              (eventNo == hit.Index ? "(agrees)" : "(does NOT agree)"));

        int conds = 0;
        foreach (var alt in hit.Alternatives) conds += alt.Count;
        if (conds == 0)
        {
            Plugin.Logger.LogInfo($"[Nav1] event running: ev[{hit.Index}] — unconditional, so it scores nothing.");
            return;
        }

        var said = CanFireAny(hit.Alternatives);
        if (said == null)
        {
            Plugin.Logger.LogInfo($"[Nav1] event running: ev[{hit.Index}] has conditions but the flags are " +
                                  "unreadable — nothing scored.");
            return;
        }

        if (said.Value)
        {
            _confirmed = true;
            Plugin.Logger.LogWarning($"[Nav1] *** RULE CONFIRMS: ev[{hit.Index}] carries {conds} condition(s), " +
                                     $"FIRED, and the tmp-bank rule said it could. {Gsd1Events.Describe(hit)} " +
                                     "— the live side of the rule is scored; DropDeadEvents may be armed.");
        }
        else
        {
            _contradicted = true;
            Plugin.Logger.LogError($"[Nav1] *** RULE CONTRADICTED: ev[{hit.Index}] FIRED but the tmp-bank rule " +
                                   $"called it dead. {Gsd1Events.Describe(hit)} — the polarity or the bank is " +
                                   "wrong. DO NOT arm DropDeadEvents.");
        }
    }
}
