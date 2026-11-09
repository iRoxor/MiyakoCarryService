// SainGrenadePatch.cs -- Harmony prefix on SAIN 4.5.1's GrenadeThrowDecider.GetDecision(Enemy, out string),
// the check every SAIN grenade throw goes through (since fork v11).
//
// Enabled by MiyakoCarryServicePlugin.EnableAllPatches. SAIN's EnemyDecisionClass asks GetDecision
// whether to throw at an enemy; GetDecision itself starts the throw (TryThrowGrenade, then the game's
// BotGrenadeController.DoThrow), so answering false here stops the throw before it begins. Checked in
// SAIN 4.5.1 with cecil_il.ps1 (-FindCall GrenadeThrowDecider:: and BotGrenadeController::): no other
// SAIN path calls DoThrow. For carry bots whose stance forbids grenades (Passive and Defensive,
// Core/Stance.cs) it answers false with the reason "fork: no grenades in this stance"; other bots and
// stances pass through untouched. With "Debug: diagnostic log" on it logs the first blocked throw per
// bot per stance. Any exception is logged once and swallowed, so a fork bug degrades to SAIN's own
// grenade behaviour. Reads and writes no files.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Utils;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SPT.Reflection.Patching;

namespace MiyakoCarryService.Client.C2Sain.Patches
{
    /// <summary>Blocks SAIN's grenade throws for carry bots in stances without grenades.</summary>
    public sealed class SainGrenadePatch : ModulePatch
    {
        private static bool _loggedError;

        /// <summary>Bots (profile id) whose blocked throw was already logged, with the stance it was logged under. Cleared by Clear at raid end.</summary>
        private static readonly Dictionary<string, CarryStance> _logged = new();

        /// <summary>Target: SAIN's GrenadeThrowDecider.GetDecision.</summary>
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(GrenadeThrowDecider), "GetDecision");

        /// <summary>
        /// Answers "no throw" for carry bots whose stance forbids grenades; runs SAIN's own check otherwise.
        /// </summary>
        /// <param name="__instance">The bot's grenade decider.</param>
        /// <param name="__result">GetDecision's answer when the original is skipped.</param>
        /// <param name="__1">GetDecision's out reason string.</param>
        [PatchPrefix]
        public static bool Prefix(GrenadeThrowDecider __instance, ref bool __result, ref string __1)
        {
            try
            {
                var botOwner = __instance?.BotOwner;
                if (botOwner == null || !botOwner.IsMcsBotPlayer)
                {
                    return true;
                }
                var fork = botOwner.GetMcsBotPlayerData()?.Fork;
                if (fork == null)
                {
                    return true;
                }
                var stance = EngageTactics.CurrentStance(fork);
                if (StanceRules.AllowsGrenades(stance))
                {
                    return true;
                }
                __result = false;
                __1 = "fork: no grenades in this stance";
                if (ForkConfig.DebugLog?.Value == true && (!_logged.TryGetValue(botOwner.ProfileId, out var was) || was != stance))
                {
                    _logged[botOwner.ProfileId] = stance;
                    McsLogger.LogInfo($"[{ForkLog.Tag}] grenade: {botOwner.Profile?.Nickname} held a grenade throw ({stance} stance)");
                }
                return false;
            }
            catch (Exception e)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    McsLogger.LogError(e);
                }
                return true;
            }
        }

        /// <summary>Forgets which bots were logged. Called at raid end.</summary>
        public static void Clear()
        {
            _logged.Clear();
        }
    }
}
