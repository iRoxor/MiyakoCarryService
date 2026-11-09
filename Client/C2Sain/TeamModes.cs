// TeamModes.cs -- one TeamModeMachine per player squad, ticked from the bots' layers.
//
// Owner of the per-squad mode state for the whole raid. C2Gate (C2 layer gate),
// McsGuardLayer, ForkCommands and ForkDebug call Get(); the first caller after
// 0.2 s ticks the machine with the player's position and whether anyone in the
// squad is engaged. ContactReports records contact reports (which also start
// Guard at once) and the player's sightings; HeardWatch records places where a
// carry bot heard an unseen enemy, and while the squad follows, raises an alert
// that works like a short contact report (AlertHeard; fork v8). The guards look at
// the freshest of the engagement threat, the report, the sighting and the heard
// place, each guard only when its sector covers it (ShouldFaceThreat; fork v7
// turned every guard toward it). It also owns each squad's
// guard plan (GuardPlanner): McsGuardLayer asks GetGuardPost for its bot's post,
// and the plan is remade when the player has moved REPLAN_MOVE m, a contact
// report arrives or expires, or the number of living bots changes. BrainMgr.
// OnRaidEnded calls Clear(). No MonoBehaviour is involved.
// Defend Me (ForkCommands.DefendMe) places the machine's Defend zone through
// StartDefend; Regroup ends it through CancelDefend, and so does the player
// leaving the zone. While the zone exists McsGuardLayer asks GetDefendPost instead:
// a squad Defend plan (GuardPlanner.PlanDefend), remade when the player has moved
// DEFEND_REPLAN_MOVE m, a report arrives or expires, or a bot dies, plus each bot's
// own shifts to new spots (Core/DefendShuffle.cs, GuardPlanner.ShiftDefend).
// Since fork v11 each squad also has a Passive hold-fire gate (Core/Stance.cs, PassiveGate), ticked
// with the machine and read by DecisionPolicy through IsPassiveReleased: a Passive squad holds fire
// until a carry bot or the player is shot at, or the player fires at an enemy (when the "Passive:
// fight when you shoot" setting is on), and is released until the squad leaves combat.
// Reads and writes no files; with "Debug: diagnostic log" on it writes mode
// changes, every guard and Defend plan (each bot's post, its cover and the bot's
// path to it, from GuardPlanner.DescribePost) and every Defend shift to the
// BepInEx log.

using System.Collections.Generic;
using Comfort.Common;
using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Misc;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Registry and ticker of per-squad modes. Main-thread only.</summary>
    internal static class TeamModes
    {
        /// <summary>Seconds between ticks of one squad's machine.</summary>
        private const float TICK_INTERVAL = 0.2f;

        /// <summary>Seconds a recorded threat position stays useful for guards to look at.</summary>
        private const float THREAT_MEMORY = 10f;

        /// <summary>Metres the player may move before the squad's guard plan is made again.</summary>
        private const float REPLAN_MOVE = 4f;

        /// <summary>Seconds a contact report ("I see the enemy") stays the guards' focus.</summary>
        private const float REPORT_MEMORY = 30f;

        /// <summary>Seconds a sighting handed over from the player's view stays a look target.</summary>
        private const float SIGHTING_MEMORY = 10f;

        /// <summary>Seconds a place where a carry bot heard an unseen enemy stays a look target (fork v8).</summary>
        private const float HEARD_MEMORY = 8f;

        /// <summary>
        /// Seconds an alert from a heard sound (HeardWatch, while following) stays the guards' focus,
        /// shorter than a contact report's 30 s because nobody has seen the enemy.
        /// </summary>
        private const float HEARD_ALERT_MEMORY = 15f;

        /// <summary>Seconds a guard keeps its answer to "should I face the squad's threat?" (ShouldFaceThreat is not free).</summary>
        public const float FACE_CHECK_INTERVAL = 0.5f;

        /// <summary>Seconds after the player hits an identified enemy during which the player counts as engaged.</summary>
        private const float PLAYER_SHOT_WINDOW = 3f;

        /// <summary>
        /// Fork v11: seconds a hit or near miss on a bot, a hit on the player, or the player firing at an
        /// enemy counts as "just now" for the Passive gate. Longer than the 0.2 s tick so no event falls
        /// between two ticks.
        /// </summary>
        private const float PASSIVE_EVENT_WINDOW = 1.5f;

        /// <summary>
        /// Metres the player may move inside a Defend Me zone before the squad's Defend spots are
        /// picked again. The spots are 3-5 m out, so a 2 m move already leaves some 1-7 m away.
        /// </summary>
        private const float DEFEND_REPLAN_MOVE = 2f;

        /// <summary>
        /// A bot this close (m) to its Defend spot counts as there when its fork state is missing.
        /// Otherwise the Guard layer's arrival verdict is used (within 1.25 m, or stalled within 3 m;
        /// Core/ArrivalWatch.cs), so a bot stopped just short of its spot may still shift.
        /// </summary>
        private const float DEFEND_AT_SPOT = 1.25f;

        private sealed class Entry
        {
            public readonly TeamModeMachine Machine = new();
            public float NextTick;
            public bool HasThreat;
            public Vector3 ThreatPosition;
            public float ThreatTime = -999f;
            public bool HasReport;
            public Vector3 ReportPosition;
            public float ReportTime = -999f;
            public float ReportMemory = REPORT_MEMORY;
            public bool ReportHeard;
            public int ReportId;
            public bool HasHeard;
            public Vector3 HeardPosition;
            public float HeardTime = -999f;
            public bool HasSighting;
            public Vector3 SightingPosition;
            public float SightingTime = -999f;
            public GuardPlanState Plan;
            public GuardPlanState DefendPlan;
            public readonly PassiveGate Passive = new();
        }

        /// <summary>
        /// A squad's guard or Defend plan: where it was made, under which report, for how many bots, and
        /// each bot's post. A Defend plan's posts change as bots shift (GetDefendPost).
        /// </summary>
        private sealed class GuardPlanState
        {
            public Vector3 Anchor;
            public int ReportId;
            public int BotCount;
            public readonly Dictionary<string, GuardPost> Posts = new();
        }

        private static readonly Dictionary<string, Entry> _entries = new();
        private static readonly List<BotOwner> _members = new();
        private static readonly List<V3> _botPoints = new();
        private static readonly List<V3> _postPoints = new();
        private static readonly List<GuardPost> _otherPosts = new();
        private static readonly List<float> _offsets = new();
        private static readonly TeamModeSettings _settings = new();
        private static readonly DefendShuffleSettings _shiftSettings = new();
        private static readonly StanceSettings _stanceSettings = new();
        private static McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        /// <summary>
        /// Returns the squad's machine, ticking it first when a tick is due.
        /// </summary>
        /// <param name="lead">The player's MCS lead object; null returns null.</param>
        public static TeamModeMachine Get(McsAILeadPlayer lead)
        {
            var entry = GetEntry(lead);
            return entry?.Machine;
        }

        /// <summary>
        /// Where guards should look: the squad's current threat (the engaged bot's goal enemy, or the
        /// player's attacker) if recent, else a contact report under 30 s old (15 s for an alert from a
        /// heard sound), else a sighting from the player's view under 10 s old, else a place where a carry
        /// bot heard an unseen enemy under 8 s old. False when none of them is recent.
        /// </summary>
        public static bool TryGetThreat(McsAILeadPlayer lead, out Vector3 position)
        {
            var entry = GetEntry(lead);
            var now = Time.time;
            if (entry != null && entry.HasThreat && now - entry.ThreatTime <= THREAT_MEMORY)
            {
                position = entry.ThreatPosition;
                return true;
            }
            if (entry != null && entry.HasReport && now - entry.ReportTime <= entry.ReportMemory)
            {
                position = entry.ReportPosition;
                return true;
            }
            if (entry != null && entry.HasSighting && now - entry.SightingTime <= SIGHTING_MEMORY)
            {
                position = entry.SightingPosition;
                return true;
            }
            if (entry != null && entry.HasHeard && now - entry.HeardTime <= HEARD_MEMORY)
            {
                position = entry.HeardPosition;
                return true;
            }
            position = default;
            return false;
        }

        /// <summary>
        /// The contact report guards should face, while it is under 30 s old, with its id (it changes
        /// with every report, so guards know to pick new spots). False when there is none.
        /// </summary>
        public static bool TryGetReport(McsAILeadPlayer lead, out Vector3 position, out int reportId)
        {
            var entry = GetEntry(lead);
            if (entry != null && entry.HasReport && Time.time - entry.ReportTime <= entry.ReportMemory)
            {
                position = entry.ReportPosition;
                reportId = entry.ReportId;
                return true;
            }
            position = default;
            reportId = 0;
            return false;
        }

        /// <summary>
        /// Records an "I see the enemy" report and starts Guard at the player's position at once (a
        /// Defend Me zone is left alone). Side effect: guards re-pick spots facing the mark.
        /// </summary>
        /// <param name="lead">The player's MCS lead object.</param>
        /// <param name="mark">The reported enemy's position, or where the crosshair hit.</param>
        /// <param name="playerPos">The player's position, the new Guard zone's centre.</param>
        public static void ReportContact(McsAILeadPlayer lead, Vector3 mark, Vector3 playerPos)
        {
            var entry = GetEntry(lead);
            if (entry == null)
            {
                return;
            }
            RefreshSettings();
            entry.HasReport = true;
            entry.ReportPosition = mark;
            entry.ReportTime = Time.time;
            entry.ReportMemory = REPORT_MEMORY;
            entry.ReportHeard = false;
            entry.ReportId++;
            entry.Machine.StartGuard(new V3(playerPos.x, playerPos.y, playerPos.z), _settings);
        }

        /// <summary>
        /// An alert from a heard, unseen enemy while the squad follows the player (HeardWatch): like a
        /// contact report, Guard starts at the player with posts in cover facing the sound, but the focus
        /// lasts HEARD_ALERT_MEMORY s. A live contact report from the player is never replaced. Returns
        /// true when the alert was raised.
        /// </summary>
        /// <param name="lead">The player's MCS lead object.</param>
        /// <param name="mark">Where the sound was heard.</param>
        /// <param name="playerPos">The player's position, the Guard zone's centre.</param>
        public static bool AlertHeard(McsAILeadPlayer lead, Vector3 mark, Vector3 playerPos)
        {
            var entry = GetEntry(lead);
            if (entry == null || entry.Machine.Mode != TeamMode.C2)
            {
                return false;
            }
            if (entry.HasReport && !entry.ReportHeard && Time.time - entry.ReportTime <= entry.ReportMemory)
            {
                return false;
            }
            RefreshSettings();
            entry.HasReport = true;
            entry.ReportPosition = mark;
            entry.ReportTime = Time.time;
            entry.ReportMemory = HEARD_ALERT_MEMORY;
            entry.ReportHeard = true;
            entry.ReportId++;
            entry.Machine.StartGuard(new V3(playerPos.x, playerPos.y, playerPos.z), _settings);
            return true;
        }

        /// <summary>Records where a carry bot heard an unseen enemy, as a short-lived look target (HeardWatch).</summary>
        public static void RecordHeard(McsAILeadPlayer lead, Vector3 position)
        {
            var entry = GetEntry(lead);
            if (entry == null)
            {
                return;
            }
            entry.HasHeard = true;
            entry.HeardPosition = position;
            entry.HeardTime = Time.time;
        }

        /// <summary>
        /// Whether this guard should turn toward the squad's threat: only guards whose watch direction
        /// (BotForkState.GuardOutward) is within ThreatSectors.DEFAULT_HALF_WIDTH degrees of the threat,
        /// or the closest one when no guard's is (Core/Hearing.cs, ThreatSectors). Fork v7 turned every
        /// guard toward it. Callers cache the answer for FACE_CHECK_INTERVAL s.
        /// </summary>
        /// <param name="lead">The player's MCS lead object.</param>
        /// <param name="bot">The guard asking.</param>
        /// <param name="threat">The threat's position (TryGetThreat).</param>
        public static bool ShouldFaceThreat(McsAILeadPlayer lead, BotOwner bot, Vector3 threat)
        {
            var player = lead?.McsLeadPlayer;
            if (player == null || bot == null)
            {
                return true;
            }
            FillMembers(player);
            _offsets.Clear();
            var me = -1;
            foreach (var member in _members)
            {
                var watch = member.GetMcsBotPlayerData()?.Fork.GuardOutward ?? Vector3.zero;
                if (watch.sqrMagnitude < 0.01f)
                {
                    watch = member.LookDirection;
                }
                var toThreat = threat - member.Position;
                if (member.ProfileId == bot.ProfileId)
                {
                    me = _offsets.Count;
                }
                _offsets.Add(GuardFormation.AngleBetween(GuardFormation.YawOf(watch.x, watch.z), GuardFormation.YawOf(toThreat.x, toThreat.z)));
            }
            _members.Clear();
            return me < 0 || ThreatSectors.ShouldFace(_offsets, me, ThreatSectors.DEFAULT_HALF_WIDTH);
        }

        /// <summary>
        /// Defend Me: places the squad's Defend zone (TeamModeSettings.DefendRadius, 5 m) at the player's
        /// position, replacing any Guard zone, and drops the old Defend plan so spots are picked afresh.
        /// Called once per bot that received the order; repeats only re-anchor the zone.
        /// </summary>
        /// <param name="lead">The player's MCS lead object.</param>
        /// <param name="playerPos">Where the player stood when the order was given.</param>
        public static void StartDefend(McsAILeadPlayer lead, Vector3 playerPos)
        {
            var entry = GetEntry(lead);
            if (entry == null)
            {
                return;
            }
            RefreshSettings();
            var startedNow = !(entry.Machine.HasZone && entry.Machine.ZoneIsDefend);
            entry.Machine.StartDefend(new V3(playerPos.x, playerPos.y, playerPos.z), _settings);
            entry.DefendPlan = null;
            if (startedNow && ForkConfig.DebugLog?.Value == true)
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] squad of {lead.McsLeadPlayer?.Profile?.Nickname}: Defend Me ordered");
            }
        }

        /// <summary>Ends Defend Me (Regroup does this); a Guard zone is left alone. Logs it when debug logging is on.</summary>
        /// <param name="lead">The player's MCS lead object.</param>
        public static void CancelDefend(McsAILeadPlayer lead)
        {
            var entry = GetEntry(lead);
            if (entry == null || !entry.Machine.ZoneIsDefend)
            {
                return;
            }
            entry.Machine.CancelDefend();
            entry.DefendPlan = null;
            if (ForkConfig.DebugLog?.Value == true)
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] squad of {lead.McsLeadPlayer?.Profile?.Nickname}: Defend Me cancelled");
            }
        }

        /// <summary>
        /// Fork v11: whether a Passive squad may fight now (Core/Stance.cs, PassiveGate). Ticks the squad
        /// first when a tick is due. True when the squad is unknown, so a fault never silences a bot.
        /// </summary>
        /// <param name="lead">The player's MCS lead object.</param>
        public static bool IsPassiveReleased(McsAILeadPlayer lead)
        {
            var entry = GetEntry(lead);
            return entry == null || entry.Passive.Released;
        }

        /// <summary>True while the squad has a Defend Me zone, in combat or not.</summary>
        public static bool IsDefending(McsAILeadPlayer lead)
        {
            var machine = Get(lead);
            return machine != null && machine.HasZone && machine.ZoneIsDefend;
        }

        /// <summary>
        /// The spot this bot should hold under Defend Me, from its squad's Defend plan, which is made (or
        /// made again) first when needed; see the file header. Also rolls the bot's shift timer: a bot
        /// standing at its spot sometimes moves to another one in its sector (Core/DefendShuffle.cs).
        /// Null when the plan has no spot for the bot; the Guard layer then falls back to a follow spot.
        /// </summary>
        /// <param name="lead">The player's MCS lead object.</param>
        /// <param name="bot">The carry bot asking.</param>
        public static GuardPost GetDefendPost(McsAILeadPlayer lead, BotOwner bot)
        {
            var entry = GetEntry(lead);
            var player = lead?.McsLeadPlayer;
            if (entry == null || player == null || bot == null)
            {
                return null;
            }

            FillMembers(player);
            var reportId = 0;
            Vector3? threat = null;
            if (TryGetReport(lead, out var reportPosition, out var id))
            {
                reportId = id;
                threat = reportPosition;
            }

            var plan = entry.DefendPlan;
            var fork = bot.GetMcsBotPlayerData()?.Fork;
            if (plan == null || plan.ReportId != reportId || plan.BotCount != _members.Count
                || Vector3.Distance(plan.Anchor, player.Position) > DEFEND_REPLAN_MOVE)
            {
                plan = entry.DefendPlan = MakeDefendPlan(player, threat, reportId, threat.HasValue && entry.ReportHeard);
                foreach (var member in _members)
                {
                    member.GetMcsBotPlayerData()?.Fork.DefendShift.Reset();
                }
            }

            if (!plan.Posts.TryGetValue(bot.ProfileId, out var post))
            {
                _members.Clear();
                return null;
            }

            var atSpot = fork != null ? fork.Arrival.Arrived : Vector3.Distance(bot.Position, post.Spot) <= DEFEND_AT_SPOT;
            if (fork != null && fork.DefendShift.Tick(Time.time, atSpot, _members.Count, _shiftSettings))
            {
                _otherPosts.Clear();
                foreach (var pair in plan.Posts)
                {
                    if (pair.Key != bot.ProfileId)
                    {
                        _otherPosts.Add(pair.Value);
                    }
                }
                var shifted = GuardPlanner.ShiftDefend(player.Position, player.LookDirection, post, bot.Position, _members.Count, threat, IsIndoors(player), _otherPosts);
                _otherPosts.Clear();
                if (shifted != null)
                {
                    plan.Posts[bot.ProfileId] = shifted;
                    post = shifted;
                    if (ForkConfig.DebugLog?.Value == true)
                    {
                        McsLogger.LogInfo($"[{ForkLog.Tag}] defend shift for {bot.Profile?.Nickname}: "
                            + GuardPlanner.DescribePost(player.Position, player.LookDirection, bot.Position, shifted));
                    }
                }
            }
            _members.Clear();
            return post;
        }

        /// <summary>
        /// Plans Defend spots for the bots in _members and gives each the spot that keeps the squad's total
        /// walk shortest (GuardFormation.Assign). Logs the plan when debug logging is on, in the guard
        /// plan's format with the header "defend plan for".
        /// </summary>
        private static GuardPlanState MakeDefendPlan(Player player, Vector3? threat, int reportId, bool heard)
        {
            var state = new GuardPlanState { Anchor = player.Position, ReportId = reportId, BotCount = _members.Count };
            var indoors = IsIndoors(player);
            var posts = GuardPlanner.PlanDefend(player.Position, player.LookDirection, _members.Count, threat, indoors);
            AssignPosts(posts, state);
            LogPlan("defend plan", player, indoors, posts, threat, heard, state);
            return state;
        }

        /// <summary>Records where the player saw an enemy (MCS's hand-over), as a short-lived look target.</summary>
        public static void RecordSighting(McsAILeadPlayer lead, Vector3 position)
        {
            var entry = GetEntry(lead);
            if (entry == null)
            {
                return;
            }
            entry.HasSighting = true;
            entry.SightingPosition = position;
            entry.SightingTime = Time.time;
        }

        /// <summary>
        /// The post this bot should hold while guarding, from its squad's guard plan, which is made (or
        /// made again) first when needed; see the file header. Null when the plan has no post for the bot
        /// (the surroundings offered too few usable spots); the Guard layer then falls back to a follow spot.
        /// </summary>
        /// <param name="lead">The player's MCS lead object.</param>
        /// <param name="bot">The carry bot asking.</param>
        public static GuardPost GetGuardPost(McsAILeadPlayer lead, BotOwner bot)
        {
            var entry = GetEntry(lead);
            var player = lead?.McsLeadPlayer;
            if (entry == null || player == null || bot == null)
            {
                return null;
            }

            FillMembers(player);
            var reportId = 0;
            Vector3? threat = null;
            if (TryGetReport(lead, out var reportPosition, out var id))
            {
                reportId = id;
                threat = reportPosition;
            }

            var plan = entry.Plan;
            if (plan == null || plan.ReportId != reportId || plan.BotCount != _members.Count
                || Vector3.Distance(plan.Anchor, player.Position) > REPLAN_MOVE)
            {
                plan = entry.Plan = MakePlan(player, threat, reportId, threat.HasValue && entry.ReportHeard);
            }
            _members.Clear();
            return plan.Posts.TryGetValue(bot.ProfileId, out var post) ? post : null;
        }

        /// <summary>
        /// Plans posts for the bots in _members and gives each the post that keeps the squad's total walk
        /// shortest (GuardFormation.Assign). Logs the plan when debug logging is on.
        /// </summary>
        private static GuardPlanState MakePlan(Player player, Vector3? threat, int reportId, bool heard)
        {
            var state = new GuardPlanState { Anchor = player.Position, ReportId = reportId, BotCount = _members.Count };
            var indoors = IsIndoors(player);
            var minRange = ForkConfig.GuardPostMin?.Value ?? 3f;
            var maxRange = Mathf.Max(minRange + 1f, ForkConfig.GuardPostMax?.Value ?? 12f);
            var posts = GuardPlanner.Plan(player.Position, player.LookDirection, _members.Count, threat, indoors, minRange, maxRange);
            AssignPosts(posts, state);
            LogPlan("guard plan", player, indoors, posts, threat, heard, state);
            return state;
        }

        /// <summary>Fills _members with the squad's living carry bots.</summary>
        private static void FillMembers(Player player)
        {
            _members.Clear();
            foreach (var member in McsMgr.GetAllAliveMcsSquadMembersByMcsLeadId(player.ProfileId))
            {
                var owner = member?.AIData?.BotOwner;
                if (owner != null)
                {
                    _members.Add(owner);
                }
            }
        }

        /// <summary>Gives each bot in _members the post that keeps the squad's total walk shortest (GuardFormation.Assign).</summary>
        private static void AssignPosts(List<GuardPost> posts, GuardPlanState state)
        {
            _botPoints.Clear();
            _postPoints.Clear();
            foreach (var member in _members)
            {
                var p = member.Position;
                _botPoints.Add(new V3(p.x, p.y, p.z));
            }
            foreach (var post in posts)
            {
                _postPoints.Add(new V3(post.Spot.x, post.Spot.y, post.Spot.z));
            }
            var assignment = GuardFormation.Assign(_botPoints, _postPoints);
            for (var i = 0; i < _members.Count; i++)
            {
                if (assignment[i] >= 0)
                {
                    state.Posts[_members[i].ProfileId] = posts[assignment[i]];
                }
            }
        }

        /// <summary>
        /// Logs a plan when debug logging is on: a header ("guard plan for" or "defend plan for" the
        /// player, indoors or outdoors, posts per bots, and "facing a contact report" or "facing a heard sound"
        /// when the plan faces one) and one line per bot (GuardPlanner.DescribePost). Since fork v9 a
        /// guard plan that tried indoor approaches ends with GuardPlanner.LastIndoorReport. Since fork
        /// v10 a header with fewer posts than bots ends with OffMeshWatch.PlanNote when the player is
        /// off the navmesh ("; you are off the navmesh (nearest walkable ground: 12 m)").
        /// </summary>
        private static void LogPlan(string what, Player player, bool indoors, List<GuardPost> posts, Vector3? threat, bool heard, GuardPlanState state)
        {
            if (ForkConfig.DebugLog?.Value != true)
            {
                return;
            }
            McsLogger.LogInfo($"[{ForkLog.Tag}] {what} for {player.Profile?.Nickname}: {(indoors ? "indoors" : "outdoors")}, "
                + $"{posts.Count} post(s) for {_members.Count} bot(s){(threat.HasValue ? (heard ? ", facing a heard sound" : ", facing a contact report") : "")}"
                + (posts.Count < _members.Count ? OffMeshWatch.PlanNote(player.Position) : ""));
            foreach (var member in _members)
            {
                state.Posts.TryGetValue(member.ProfileId, out var post);
                McsLogger.LogInfo($"[{ForkLog.Tag}]   {member.Profile?.Nickname}: "
                    + GuardPlanner.DescribePost(player.Position, player.LookDirection, member.Position, post));
            }
            if (what == "guard plan" && GuardPlanner.LastIndoorReport != null)
            {
                // Fork v9: after the post lines, so log readers that end a plan's block at its first
                // line without two leading spaces still read every post.
                McsLogger.LogInfo($"[{ForkLog.Tag}] {GuardPlanner.LastIndoorReport}");
            }
        }

        /// <summary>
        /// True inside the game's indoor zones (the test SAIN uses for its bots: AIData.EnvironmentId is
        /// not 0), or under a roof (solid geometry within 20 m straight above the player's head). The roof
        /// test is a backstop: indoor plans keep posts out of side rooms, outdoor plans do not.
        /// </summary>
        private static bool IsIndoors(Player player)
        {
            try
            {
                if (player.AIData != null && player.AIData.EnvironmentId != 0)
                {
                    return true;
                }
            }
            catch
            {
                // AIData can be missing on some player types; fall through to the roof test.
            }
            return Physics.Raycast(player.Position + Vector3.up * 1.8f, Vector3.up, 20f, LayersMaskController.HighPolyWithTerrainMask);
        }

        /// <summary>Forgets every squad. Called at raid end.</summary>
        public static void Clear()
        {
            _entries.Clear();
        }

        /// <summary>Finds or creates the squad entry and ticks it when due.</summary>
        private static Entry GetEntry(McsAILeadPlayer lead)
        {
            var player = lead?.McsLeadPlayer;
            if (player == null)
            {
                return null;
            }

            var id = player.ProfileId;
            if (!_entries.TryGetValue(id, out var entry))
            {
                entry = new Entry();
                _entries[id] = entry;
            }

            var now = Time.time;
            if (now >= entry.NextTick)
            {
                entry.NextTick = now + TICK_INTERVAL;
                Tick(entry, lead, player, now);
            }
            return entry;
        }

        /// <summary>
        /// One tick: refresh settings, work out whether the squad is engaged (and where the threat is),
        /// then advance the machine. Logs a mode change when debug logging is on.
        /// </summary>
        private static void Tick(Entry entry, McsAILeadPlayer lead, Player player, float now)
        {
            RefreshSettings();

            var engaged = false;
            var attacker = lead.GetLeadThreatEnemy();
            if (attacker != null)
            {
                engaged = true;
                RecordThreat(entry, attacker.Position, now);
            }
            else if (now - lead.LastLeadEngageShotTime <= PLAYER_SHOT_WINDOW)
            {
                engaged = true;
            }

            foreach (var member in McsMgr.GetAllAliveMcsSquadMembersByMcsLeadId(player.ProfileId))
            {
                var botOwner = member?.AIData?.BotOwner;
                if (botOwner == null)
                {
                    continue;
                }
                if (SainBridge.IsBotEngaged(botOwner, botOwner.GetMcsBotPlayerData(), now))
                {
                    engaged = true;
                    if (SainBridge.TryGetGoalEnemyPosition(botOwner, out var enemyPos))
                    {
                        RecordThreat(entry, enemyPos, now);
                    }
                    break;
                }
            }

            var before = entry.Machine.Mode;
            var pos = player.Position;
            var after = entry.Machine.Tick(now, new V3(pos.x, pos.y, pos.z), engaged, _settings);
            if (before != after && ForkConfig.DebugLog != null && ForkConfig.DebugLog.Value)
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] squad of {player.Profile?.Nickname}: {before} -> {after}");
            }
            TickPassive(entry, lead, player, after == TeamMode.Combat, now);
        }

        /// <summary>
        /// Fork v11: updates the squad's Passive gate after the machine's tick, and with "Debug:
        /// diagnostic log" on logs its release and its return to holding fire while any carry bot is
        /// Passive. Tracked whatever the stance, so a switch to Passive mid-fight keeps fighting.
        /// </summary>
        private static void TickPassive(Entry entry, McsAILeadPlayer lead, Player player, bool inCombat, float now)
        {
            var botShotAt = false;
            var anyPassive = false;
            foreach (var member in McsMgr.GetAllAliveMcsSquadMembersByMcsLeadId(player.ProfileId))
            {
                var data = member?.AIData?.BotOwner?.GetMcsBotPlayerData();
                if (data == null)
                {
                    continue;
                }
                botShotAt |= SainBridge.IsUnderFire(data, now, PASSIVE_EVENT_WINDOW);
                anyPassive |= EngageTactics.CurrentStance(data.Fork) == CarryStance.Passive;
            }
            var playerShotAt = lead.LastLeadAttackerPlayer != null && now - lead.LastLeadAttackerTime <= PASSIVE_EVENT_WINDOW;
            var playerFired = now - lead.LastLeadEngageShotTime <= PASSIVE_EVENT_WINDOW;
            _stanceSettings.PassiveEngagesWhenPlayerShoots = ForkConfig.PassiveEngagesWhenYouShoot?.Value ?? true;

            var change = entry.Passive.Update(inCombat, botShotAt, playerShotAt, playerFired, _stanceSettings);
            if (change == PassiveEvent.None || !anyPassive || ForkConfig.DebugLog?.Value != true)
            {
                return;
            }
            var why = entry.Passive.Cause switch
            {
                PassiveCause.BotShotAt => "a carry bot was shot at",
                PassiveCause.PlayerShotAt => "you were hit",
                _ => "you fired at an enemy",
            };
            McsLogger.LogInfo(change == PassiveEvent.Released
                ? $"[{ForkLog.Tag}] passive: squad of {player.Profile?.Nickname} released to fight ({why})"
                : $"[{ForkLog.Tag}] passive: squad of {player.Profile?.Nickname} holding fire again (combat over)");
        }

        /// <summary>Remembers where the squad's threat is.</summary>
        private static void RecordThreat(Entry entry, Vector3 position, float now)
        {
            entry.HasThreat = true;
            entry.ThreatPosition = position;
            entry.ThreatTime = now;
        }

        /// <summary>Copies the BepInEx values into the machine's settings object.</summary>
        private static void RefreshSettings()
        {
            if (ForkConfig.IdleRadius == null)
            {
                return;
            }
            _settings.IdleRadius = ForkConfig.IdleRadius.Value;
            _settings.IdleSeconds = ForkConfig.IdleSeconds.Value;
            _settings.GuardRadius = ForkConfig.GuardRadius.Value;
            _settings.CombatEndDelay = ForkConfig.CombatEndDelay.Value;
        }
    }
}
