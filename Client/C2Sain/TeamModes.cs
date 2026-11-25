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
// and the plan is remade when the player has moved 4 m (PlanScheduleSettings.
// ReplanMove), a contact report arrives or expires, or the number of living bots
// changes. Since fork v12 "remade" means a GuardPlanJob (GuardPlanner.BeginPlan)
// that GameLoop.Update's call to Pump runs a few milliseconds per frame
// (Core/PlanSlicer.cs, the "Guard: planning time per frame" setting) and delivers when it is
// done; Core/PlanScheduler.cs decides, per squad, whether a request uses the plan in
// force, starts a job, carries one on or restarts it. Until a job delivers, the bots
// keep the plan in force (a bot it has no post for follows the player, as for any
// bot a plan leaves without a post). BrainMgr.OnRaidEnded calls Clear(). No
// MonoBehaviour is involved.
// Defend Me (ForkCommands.DefendMe) places the machine's Defend zone through
// StartDefend; Regroup ends it through CancelDefend, and so does the player
// leaving the zone. While the zone exists McsGuardLayer asks GetDefendPost instead:
// a squad Defend plan (GuardPlanner.PlanDefend), remade when the player has moved
// DEFEND_REPLAN_MOVE m, a report arrives or expires, or a bot dies, plus each bot's
// own shifts to new spots (Core/DefendShuffle.cs, GuardPlanner.ShiftDefend).
// Since fork v11 each squad also has a hold-fire gate (Core/Stance.cs, ReactiveGate; called PassiveGate before fork
// v18), ticked with the machine and read by DecisionPolicy through IsReactiveReleased: a Reactive squad holds fire
// until a carry bot or the player is shot at, or the player fires at an enemy (when the "Reactive: fight when you
// shoot" setting is on), and is released until the squad leaves combat.
// Since fork v18 each squad also has a set of hostile enemies (Core/HoldFire.cs, HostileSet): the enemies that hit a
// carry bot or the player, or whose bullets landed near either. ApplyDamagePatch, McsAILeadPlayer.MarkLeadAttacker and
// PlayHitEffectPatch call MarkHostile in every stance; the new Passive stance fires only at those enemies, and only within
// its range (IsHostile, asked by DecisionPolicy, SainShootGatePatch and HoldFireGate). Clear() forgets the sets at raid end.
// Reads and writes no files; with "Debug: diagnostic log" on it writes mode
// changes, every guard and Defend plan (each bot's post, its cover and the bot's
// path to it, from GuardPlanner.DescribePost, then one line with the plan's timing: the
// indoor planner's own line, or "plan timing:") and every Defend shift to the BepInEx log.

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
        /// enemy counts as "just now" for the Reactive gate. Longer than the 0.2 s tick so no event falls
        /// between two ticks.
        /// </summary>
        private const float REACTIVE_EVENT_WINDOW = 1.5f;

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
            public readonly ReactiveGate Reactive = new();

            /// <summary>Fork v18: the enemies that attacked this squad first (Core/HoldFire.cs); filled in every stance.</summary>
            public readonly HostileSet Hostile = new();

            /// <summary>Fork v12: decides when the guard plan is made, kept, carried on or dropped (Core/PlanScheduler.cs).</summary>
            public readonly PlanScheduler Schedule = new();

            /// <summary>Fork v12: the guard plan being made a few milliseconds per frame by Pump, or null. Null whenever Schedule says no job is under way.</summary>
            public GuardPlanJob Job;

            /// <summary>Fork v12: the squad's lead, kept so Pump can deliver a finished plan outside any bot's request.</summary>
            public McsAILeadPlayer Lead;
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

        /// <summary>Fork v12: when a squad's guard plan is made, kept or dropped (Core/PlanScheduler.cs); defaults, never changed.</summary>
        private static readonly PlanScheduleSettings _scheduleSettings = new();

        /// <summary>Fork v12: the time budget per frame for plan jobs; Pump refreshes BudgetMs from the "Guard: planning time per frame" setting.</summary>
        private static readonly SliceSettings _sliceSettings = new();

        /// <summary>Fork v12: the entries Pump visits, copied first so nothing it calls can change the list it walks.</summary>
        private static readonly List<Entry> _pumpEntries = new();

        /// <summary>Fork v12: seconds between logs of an exception from a plan job (Time.time of the last one is _lastFaultLog).</summary>
        private const float FAULT_LOG_INTERVAL = 30f;

        private static float _lastFaultLog = -999f;
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
        /// Fork v11 (called IsPassiveReleased before fork v18): whether a Reactive squad may fight now (Core/Stance.cs,
        /// ReactiveGate). Ticks the squad first when a tick is due. True when the squad is unknown, so a fault never
        /// silences a bot.
        /// </summary>
        /// <param name="lead">The player's MCS lead object.</param>
        public static bool IsReactiveReleased(McsAILeadPlayer lead)
        {
            var entry = GetEntry(lead);
            return entry == null || entry.Reactive.Released;
        }

        /// <summary>
        /// Fork v18: records that an enemy attacked the squad first (it hit a carry bot or the player, or its bullet landed near
        /// either), so the new Passive stance may fire at it from now until the raid ends. The first cause is kept. Does not tick
        /// the squad. With "Debug: diagnostic log" on, the first marking of each enemy writes "passive: &lt;enemy&gt; is hostile:
        /// hit bot|hit you|shot near bot|shot near you". Called whatever the stance, so an enemy that attacked before the switch
        /// to Passive is still hostile.
        /// </summary>
        /// <param name="lead">The player's MCS lead object; null marks nothing.</param>
        /// <param name="enemyProfileId">The enemy's profile id.</param>
        /// <param name="enemyName">The enemy's name, for the log line.</param>
        /// <param name="cause">What the enemy did.</param>
        public static void MarkHostile(McsAILeadPlayer lead, string enemyProfileId, string enemyName, HostileCause cause)
        {
            try
            {
                var player = lead?.McsLeadPlayer;
                if (player == null || string.IsNullOrEmpty(enemyProfileId))
                {
                    return;
                }
                var id = player.ProfileId;
                if (!_entries.TryGetValue(id, out var entry))
                {
                    entry = new Entry();
                    _entries[id] = entry;
                }
                if (entry.Hostile.Mark(enemyProfileId, cause) && ForkConfig.DebugLog?.Value == true)
                {
                    var what = cause switch
                    {
                        HostileCause.HitBot => "hit bot",
                        HostileCause.HitPlayer => "hit you",
                        HostileCause.ShotNearBot => "shot near bot",
                        _ => "shot near you",
                    };
                    McsLogger.LogInfo($"[{ForkLog.Tag}] passive: {enemyName} is hostile: {what}");
                }
            }
            catch (System.Exception e)
            {
                // Called from the game's damage and hit-effect code: noting an attacker must never break either. Logged at most once.
                if (!_loggedHostileFault)
                {
                    _loggedHostileFault = true;
                    McsLogger.LogError(e);
                }
            }
        }

        /// <summary>True once a fault in <see cref="MarkHostile"/> was logged (the first only).</summary>
        private static bool _loggedHostileFault;

        /// <summary>
        /// Fork v18: whether an enemy attacked the squad first (<see cref="MarkHostile"/>). Does not tick the squad and creates
        /// nothing. False when the squad or the enemy is unknown.
        /// </summary>
        /// <param name="lead">The player's MCS lead object.</param>
        /// <param name="enemyProfileId">The enemy's profile id.</param>
        public static bool IsHostile(McsAILeadPlayer lead, string enemyProfileId)
        {
            var player = lead?.McsLeadPlayer;
            return player != null && _entries.TryGetValue(player.ProfileId, out var entry) && entry.Hostile.IsHostile(enemyProfileId);
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
                GuardPost shifted;
                using (ForkCost.Begin(CostKind.Defend))
                {
                    shifted = GuardPlanner.ShiftDefend(player.Position, player.LookDirection, post, bot.Position, _members.Count, threat, IsIndoors(player), _otherPosts);
                }
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
            var clock = System.Diagnostics.Stopwatch.StartNew();
            List<GuardPost> posts;
            using (ForkCost.Begin(CostKind.Defend))
            {
                posts = GuardPlanner.PlanDefend(player.Position, player.LookDirection, _members.Count, threat, indoors);
            }
            clock.Stop();
            AssignPosts(posts, state);
            // Fork v12: a Defend Me plan is still made in one frame; its timing line says how long that frame held.
            LogPlan("defend plan", player, indoors, posts, threat, heard, state, null,
                SliceReport.Format(SliceStats.OneFrame(clock.Elapsed.TotalMilliseconds, PlanPhase.Spots)));
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
        /// The post this bot should hold while guarding, from its squad's guard plan in force. A request
        /// that finds the plan missing or out of date (none yet, the player 4 m from where it was made,
        /// another contact report, a different number of living bots) starts a plan job (PlanScheduler
        /// decides); Pump runs it over the next few frames and then the new plan replaces the old. Until
        /// then this answers from the plan in force; see the file header. Null when that plan has no post
        /// for the bot (there is none yet, or the surroundings offered too few usable spots); the Guard
        /// layer then falls back to a follow spot. Before fork v12 the plan was made right here, in the
        /// bot's frame.
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

            var at = player.Position;
            var wanted = new PlanKey(reportId, _members.Count);
            var request = entry.Schedule.Request(wanted, new V3(at.x, at.y, at.z), Time.time, _scheduleSettings);
            if (request == PlanRequest.StartJob || request == PlanRequest.RestartJob)
            {
                entry.Job = BeginJob(player, threat, wanted, threat.HasValue && entry.ReportHeard);
            }
            if (!entry.Schedule.HasJob)
            {
                entry.Job = null;
            }
            _members.Clear();
            return entry.Plan != null && entry.Plan.Posts.TryGetValue(bot.ProfileId, out var post) ? post : null;
        }

        /// <summary>Fork v12: starts a guard plan job for the player as they stand now. Plans nothing itself; Pump runs it.</summary>
        private static GuardPlanJob BeginJob(Player player, Vector3? threat, PlanKey key, bool heard)
        {
            var minRange = ForkConfig.GuardPostMin?.Value ?? 3f;
            var maxRange = Mathf.Max(minRange + 1f, ForkConfig.GuardPostMax?.Value ?? 12f);
            return GuardPlanner.BeginPlan(player.Position, player.LookDirection, key, threat, IsIndoors(player), minRange, maxRange, heard, Time.time);
        }

        /// <summary>
        /// Fork v12: gives every squad's guard plan job its slice of this frame (the "Guard: planning time per frame"
        /// budget, Core/PlanSlicer.cs) and delivers the plan of a job that has finished. Called once a
        /// frame by GameLoop.Update while a raid is on. Gives up a job nobody has asked about for a couple
        /// of seconds (the squad left the guard layer). A job that throws is dropped and not retried for
        /// 5 s; the exception goes to the BepInEx log at most once per 30 s. Writes the squad's plan in
        /// force; with the diagnostic log on, logs the new plan.
        /// </summary>
        internal static void Pump()
        {
            if (_entries.Count == 0)
            {
                return;
            }
            var now = Time.time;
            _pumpEntries.Clear();
            _pumpEntries.AddRange(_entries.Values);
            foreach (var entry in _pumpEntries)
            {
                var job = entry.Job;
                if (job == null)
                {
                    continue;
                }
                try
                {
                    using (ForkCost.Begin(CostKind.Planner))
                    {
                        if (!entry.Schedule.HasJob)
                        {
                            entry.Job = null;
                        }
                        else if (entry.Schedule.IsIdle(now, _scheduleSettings))
                        {
                            entry.Schedule.AbandonJob();
                            entry.Job = null;
                        }
                        else
                        {
                            _sliceSettings.BudgetMs = ForkConfig.PlannerBudgetMs?.Value ?? 3f;
                            job.RunFrame(_sliceSettings);
                            if (job.Done)
                            {
                                Deliver(entry, job, now);
                            }
                        }
                    }
                }
                catch (System.Exception e)
                {
                    entry.Schedule.Faulted(now, _scheduleSettings);
                    entry.Job = null;
                    if (now - _lastFaultLog > FAULT_LOG_INTERVAL)
                    {
                        _lastFaultLog = now;
                        McsLogger.LogError(e);
                    }
                }
            }
            _pumpEntries.Clear();
        }

        /// <summary>
        /// Fork v12: makes a finished job's posts the squad's plan in force, giving each living bot the post
        /// that keeps the squad's total walk shortest (GuardFormation.Assign, with the bots where they stand
        /// now), and logs the plan when debug logging is on, with how long it took.
        /// </summary>
        private static void Deliver(Entry entry, GuardPlanJob job, float now)
        {
            entry.Job = null;
            var player = entry.Lead?.McsLeadPlayer;
            if (player == null)
            {
                entry.Schedule.AbandonJob();
                return;
            }
            FillMembers(player);
            var state = new GuardPlanState { Anchor = job.Player, ReportId = job.Key.ReportId, BotCount = job.Key.BotCount };
            AssignPosts(job.Posts, state);
            entry.Plan = state;
            entry.Schedule.Delivered();
            var timing = SliceReport.Format(job.Stats) + System.FormattableString.Invariant($", ready {now - job.StartedAt:0.00} s after it began");
            LogPlan("guard plan", player, job.Indoors, job.Posts, job.Threat, job.Heard, state, job.IndoorReport, timing);
            _members.Clear();
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
        /// guard plan that tried indoor approaches ends with the indoor planner's line (GuardPlanJob.
        /// IndoorReport). Since fork v10 a header with fewer posts than bots ends with OffMeshWatch.PlanNote
        /// when the player is off the navmesh ("; you are off the navmesh (nearest walkable ground: 12 m)").
        /// Since fork v12 the last line also says how long the plan took: the indoor line ends with
        /// "; 22 ms in 8 frame(s), worst frame 3.4 ms (scan 2.1, routes 17.4, posts 2.5), ready 0.15 s after
        /// it began" (it ended with "; 22 ms" before), and any other plan gets a "plan timing: ..." line
        /// in the same words (a Defend Me plan, made in one frame, has no "ready").
        /// </summary>
        /// <param name="indoorReport">The indoor planner's line without its timing, or null.</param>
        /// <param name="timing">SliceReport.Format of the plan's cost, with the time to ready for a sliced plan.</param>
        private static void LogPlan(string what, Player player, bool indoors, List<GuardPost> posts, Vector3? threat, bool heard, GuardPlanState state, string indoorReport, string timing)
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
            // Fork v9: after the post lines, so log readers that end a plan's block at its first
            // line without two leading spaces still read every post.
            if (indoorReport != null)
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] {indoorReport}; {timing}");
            }
            else
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] plan timing: {timing}");
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

            entry.Lead = lead;
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
            TickReactive(entry, lead, player, after == TeamMode.Combat, now);
        }

        /// <summary>
        /// Fork v11 (called TickPassive before fork v18): updates the squad's Reactive gate after the machine's tick, and with
        /// "Debug: diagnostic log" on logs its release and its return to holding fire while any carry bot is Reactive. Tracked
        /// whatever the stance, so a switch to Reactive mid-fight keeps fighting. Since fork v18 the lines read "reactive: ...";
        /// builds before it wrote "passive: ..." for the same two events.
        /// </summary>
        private static void TickReactive(Entry entry, McsAILeadPlayer lead, Player player, bool inCombat, float now)
        {
            var botShotAt = false;
            var anyReactive = false;
            foreach (var member in McsMgr.GetAllAliveMcsSquadMembersByMcsLeadId(player.ProfileId))
            {
                var data = member?.AIData?.BotOwner?.GetMcsBotPlayerData();
                if (data == null)
                {
                    continue;
                }
                botShotAt |= SainBridge.IsUnderFire(data, now, REACTIVE_EVENT_WINDOW);
                anyReactive |= StanceRules.HoldsFireUntilReleased(EngageTactics.CurrentStance(data.Fork));
            }
            var playerShotAt = lead.LastLeadAttackerPlayer != null && now - lead.LastLeadAttackerTime <= REACTIVE_EVENT_WINDOW;
            var playerFired = now - lead.LastLeadEngageShotTime <= REACTIVE_EVENT_WINDOW;
            _stanceSettings.ReactiveEngagesWhenPlayerShoots = ForkConfig.PassiveEngagesWhenYouShoot?.Value ?? true;

            var change = entry.Reactive.Update(inCombat, botShotAt, playerShotAt, playerFired, _stanceSettings);
            if (change == ReactiveEvent.None || !anyReactive || ForkConfig.DebugLog?.Value != true)
            {
                return;
            }
            var why = entry.Reactive.Cause switch
            {
                ReactiveCause.BotShotAt => "a carry bot was shot at",
                ReactiveCause.PlayerShotAt => "you were hit",
                _ => "you fired at an enemy",
            };
            McsLogger.LogInfo(change == ReactiveEvent.Released
                ? $"[{ForkLog.Tag}] reactive: squad of {player.Profile?.Nickname} released to fight ({why})"
                : $"[{ForkLog.Tag}] reactive: squad of {player.Profile?.Nickname} holding fire again (combat over)");
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
