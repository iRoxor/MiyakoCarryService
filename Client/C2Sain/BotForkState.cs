// BotForkState.cs -- the C2/SAIN fork's per-bot state.
//
// One instance per carry bot, created with and owned by McsBotPlayerData (its
// Fork property). Written by C2Gate (C2 layer gate), DecisionPolicy and
// EngageTactics (SAIN decision prefix), McsGuardLayer, the fork's commands
// (ForkCommands, Break off) and EscortToPointByWayLogic; read by ForkDebug for the
// overlay and by BotWeaponSelectorPatch. Holds no Unity objects that outlive a
// raid. Reads and writes no files.

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

        /// <summary>True when the guard spot has cover or a bush toward GuardOutward, so the bot crouches there.</summary>
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
