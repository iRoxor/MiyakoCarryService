// BotForkState.cs -- the C2/SAIN fork's per-bot state.
//
// One instance per carry bot, created with and owned by McsBotPlayerData (its
// Fork property). Written by C2Gate (C2 layer gate), DecisionPolicy and
// EngageTactics (SAIN decision prefix), McsGuardLayer, TeamModes (the Defend Me
// shift timer), the guard logics (walk progress, look scan), the fork's commands
// (ForkCommands, Break off) and EscortToPointByWayLogic; read by ForkDebug for the
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

        /// <summary>Break off from combat (command, C2Gate).</summary>
        public readonly BreakOff BreakOff = new();

        /// <summary>Engage Enemy orders and their flank legs (ForkCommands, EngageTactics, McsGuardLayer).</summary>
        public readonly EngageOrder Engage = new();

        /// <summary>Escort sprint/walk/wait cycle (EscortToPointByWayLogic).</summary>
        public readonly EscortPacer EscortPace = new(() => Random.value);

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

        /// <summary>True when the Guard layer wants the bot to hurry (returning from a chase, far away, or flanking).</summary>
        public bool MoveFast;

        /// <summary>The bot's role in the current Engage order, for the overlay ("flank L", "cover", "push").</summary>
        public string EngageRole = "";

        /// <summary>The enemy the current Engage order last aimed at, so its end can say "Enemy down". Cleared when the order ends.</summary>
        public EFT.Player EngageTarget;

        /// <summary>
        /// SAIN's own personality roll for the bot, recorded the first time the fork sees its SAIN
        /// component, and the personality it keeps outside Engage orders: that roll when the allowed-
        /// personalities setting permits it, otherwise a re-roll (EngageTactics). BaseForMask is the
        /// setting value the base was chosen under, so a changed setting re-chooses it.
        /// </summary>
        public bool HasBasePersonality;
        public EPersonality SainRolledPersonality;
        public EPersonality BasePersonality;
        public int BaseForMask = -1;

        /// <summary>The personality an Engage order lent, while it is in force.</summary>
        public bool PersonalityLent;
        public EPersonality LentPersonality;
    }
}
