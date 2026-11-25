// BotForkState.cs -- the C2/SAIN fork's per-bot state.
//
// One instance per carry bot, created with and owned by McsBotPlayerData (its
// Fork property). Written by C2Gate (C2 layer gate), DecisionPolicy and
// EngageTactics (SAIN decision prefix), McsGuardLayer, TeamModes (the Defend Me
// shift timer), the guard logics (walk progress, look scan), the fork's commands
// (ForkCommands, the Break off and Regroup recalls through C2Gate.BeginRecall), EscortToPointByWayLogic and, since v15, the
// follow code (McsBrainLayer, McsBaseLayer.EndFollowWalk, GoToPointLogic, the teleports) and, since v17, the reach watch
// (C2Sain/ReachWatch.cs, polled from McsBrainLayer.IsActive) and the trail line (FollowWatch.LogTrail); read by ForkDebug for the
// overlay, by the diagnostics (OpenGroundWatch, BrainWatch, FrozenViewWatch), by
// GuardMoveLogic and by BotWeaponSelectorPatch. Holds no Unity objects that outlive a raid. Reads and
// writes no files.

using MiyakoCarryService.Client.C2Sain.Core;
using SAIN.Preset.Shared.Models.Preset.Personalities;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Per-bot fork state; see the file header for who reads and writes each part.</summary>
    internal sealed class BotForkState
    {
        /// <summary>Time limit on chases (DecisionPolicy).</summary>
        public readonly ChaseLeash Leash = new(() => Random.value);

        /// <summary>
        /// The bot's recall (fork v17: Break off, Regroup or the Aggressive leash pull; Core/BreakOff.cs). Begun through
        /// C2Gate.BeginRecall (the commands, the leash pull), updated and ended by C2Gate, read by McsBrainLayer (the recall
        /// walk), MovePolicy, DecisionPolicy, ReachWatch and ForkDebug.
        /// </summary>
        public readonly BreakOff BreakOff = new();

        /// <summary>Fork v17: how long the bot has been beyond the Aggressive leash range; a Leash recall follows (C2Gate; Core/LeashRules.cs).</summary>
        public readonly LeashPull Pull = new();

        /// <summary>
        /// Fork v17: the reach watch's timers for this bot: stranded off the walkable ground, no complete path to the player, the
        /// catch-up cooldown (C2Sain/ReachWatch.cs writes and reads them; Core/ReachWatch.cs).
        /// </summary>
        public readonly ReachState Reach = new();

        /// <summary>Fork v17: when this bot's next "trail:" diagnostic line is due (FollowWatch.LogTrail; Core/TrailGate.cs).</summary>
        public readonly TrailGate TrailLine = new();

        /// <summary>The aggressive stances' tactics and their flank legs (EngageTactics, McsGuardLayer).</summary>
        public readonly EngageOrder Engage = new();

        /// <summary>Escort sprint/walk/wait cycle (EscortToPointByWayLogic).</summary>
        public readonly EscortPacer EscortPace = new(() => Random.value);

        /// <summary>
        /// Fork v15: how the bot keeps up with a moving player (trailing, catch-up pace, stall re-paths, teleport
        /// grace). Written by McsBrainLayer (GetNextAction, IsActive), McsBaseLayer.EndFollowWalk, GoToPointLogic
        /// (the speed rule), the teleports (McsBaseLayer, CommandMgr) and read by FollowWatch for the stall log.
        /// </summary>
        public readonly FollowTrail Follow = new();

        /// <summary>When the bot shifts to a new Defend Me spot (TeamModes.GetDefendPost).</summary>
        public readonly DefendShuffle DefendShift = new(() => Random.value);

        /// <summary>Whether the bot has reached its guard or Defend spot, counting a stall close to it (McsGuardLayer; TeamModes reads Arrived).</summary>
        public readonly ArrivalWatch Arrival = new();

        /// <summary>Whether the current walk is making progress toward GuardMoveLogic's target (GuardMoveLogic).</summary>
        public readonly ArrivalWatch MoveProgress = new();

        /// <summary>Where the bot looks while it holds still with no enemy to watch (GuardWatchLogic, and GuardMoveLogic when stalled).</summary>
        public readonly LookScan Look = new(() => Random.value);

        /// <summary>
        /// True while the Guard layer wants the bot to keep watching GuardOutward as it walks (a short
        /// Defend Me shift) instead of looking where it is going; read by GuardMoveLogic.
        /// </summary>
        public bool LookOutwardWhileMoving;

        /// <summary>
        /// What turned the bot's head on the guard logics' last frame ("scanning", "at the squad's
        /// threat", ...) and when (Time.time). Written by GuardMoveLogic and GuardWatchLogic every
        /// frame they run; read through DescribeLook by FrozenViewWatch. Diagnostics only.
        /// </summary>
        public string LookSource = "";
        public float LookSourceTime = -999f;

        /// <summary>
        /// GuardWatchLogic's cached answer to "does my sector cover the squad's threat?"
        /// (TeamModes.ShouldFaceThreat) and when it was worked out (Time.time). Fork v8.
        /// </summary>
        public bool FacesThreat = true;
        public float FacesThreatCheckedAt = -999f;

        /// <summary>
        /// LookSource for a diagnostic line, or "not steered by the guard layer" when the guard logics
        /// have not run in the last second (another layer, such as SAIN's, is turning the head).
        /// </summary>
        /// <param name="now">Time.time.</param>
        public string DescribeLook(float now)
        {
            return now - LookSourceTime <= 1f && LookSource.Length > 0 ? LookSource : "not steered by the guard layer";
        }

        /// <summary>Records what the guard logics turned the head toward this frame; see LookSource.</summary>
        /// <param name="source">A short phrase, e.g. "scanning".</param>
        /// <param name="now">Time.time.</param>
        public void SetLook(string source, float now)
        {
            LookSource = source;
            LookSourceTime = now;
        }

        /// <summary>True when the C2 layer won the last brain tick (C2Gate).</summary>
        public bool C2Active;

        /// <summary>Why C2Gate decided as it did, for the overlay.</summary>
        public string C2Reason = "";

        /// <summary>SAIN's decisions before and after the fork's policy, and why it changed them (overlay).</summary>
        public string RawDecision = "";
        public string FinalDecision = "";
        public string VetoReason = "";

        /// <summary>Guard spot chosen by McsGuardLayer, and the player position it was chosen for.</summary>
        public bool HasGuardSpot;
        public Vector3 GuardSpot;
        public Vector3 GuardSpotForPlayer;

        /// <summary>Direction (away from the player, or toward a reported threat) the bot scans while guarding.</summary>
        public Vector3 GuardOutward;

        /// <summary>True when the guard spot has cover or a bush toward GuardOutward (the overlay and log say "from cover").</summary>
        public bool GuardSpotCovered;

        /// <summary>Which plan gave the current guard spot (GuardPost.Kind, or "follow" for the fallback), for the overlay.</summary>
        public string GuardPostKind = "";

        /// <summary>
        /// Fork v10: whether McsBaseLayer.LogReach has logged this bot's fallback move toward the closest
        /// reachable spot, and its staying put with no closer spot, since its last normal follow path.
        /// </summary>
        public bool ReachMoveLogged;
        public bool ReachStayLogged;

        /// <summary>True when the Guard layer wants the bot to hurry (returning from a chase, far away, or flanking).</summary>
        public bool MoveFast;

        /// <summary>The bot's role in the aggressive stance tactics, for the overlay ("flank L", "cover", "push").</summary>
        public string EngageRole = "";


        /// <summary>SAIN's own personality roll for the bot, recorded the first time the fork sees its SAIN component (overlay).</summary>
        public bool HasSainRoll;
        public EPersonality SainRolledPersonality;

        /// <summary>
        /// Fork v11: a stance set from this bot's own menu, for this raid only; null follows the squad
        /// stance (ForkConfig.Stance). Written by ForkCommands, read through EngageTactics.CurrentStance.
        /// </summary>
        public CarryStance? StanceOverride;

        /// <summary>
        /// Fork v11: the stance EngageTactics.EnsureStance last applied, and the SAIN personality it
        /// lends (GigaChad or Wreckless already picked for Hyper-aggressive). HasStance is false until
        /// the first SAIN decision.
        /// </summary>
        public bool HasStance;
        public CarryStance Stance;
        public EPersonality StancePersonality;

        /// <summary>
        /// Fork v18: this bot's limiter for the new Passive's "held fire at" and "engaged hostile" log lines (Core/HoldFire.cs).
        /// Written by DecisionPolicy.ApplyPassive only; ends with the bot.
        /// </summary>
        public readonly HoldFireLog PassiveLog = new();
    }
}
