// BotForkState.cs -- the C2/SAIN fork's per-bot state.
//
// One instance per carry bot, created with and owned by McsBotPlayerData (its
// Fork property). Written by C2Gate (C2 layer gate), DecisionPolicy (SAIN
// decision prefix), McsGuardLayer and the Break off command; read by ForkDebug
// for the overlay and by BotWeaponSelectorPatch. Holds no Unity objects that
// outlive a raid. Reads and writes no files.

using MiyakoCarryService.Client.C2Sain.Core;
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

        /// <summary>Direction (away from the player) the bot scans while guarding.</summary>
        public Vector3 GuardOutward;

        /// <summary>True when the Guard layer wants the bot to hurry (returning from a chase or far away).</summary>
        public bool MoveFast;
    }
}
