// SainSetDecisionsPatch.cs -- Harmony prefix on SAIN 4.5.1's private
// BotDecisionManager.SetDecisions(ECombatDecision solo, ESquadDecision squad,
// ESelfActionType self, Enemy enemy) (SAIN/Classes/Bot/Decision/BotDecisionManager.cs:259).
//
// Enabled by MiyakoCarryServicePlugin.EnableAllPatches. The original takes its
// arguments by value; declaring them ref here lets the prefix rewrite them
// before SAIN stores them. The work is in DecisionPolicy. Any exception is
// logged once and swallowed, so a fork bug degrades to plain SAIN behaviour
// instead of breaking SAIN's decision loop. Reads and writes no files.

using System;
using System.Reflection;
using HarmonyLib;
using MiyakoCarryService.Client.Utils;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SPT.Reflection.Patching;

namespace MiyakoCarryService.Client.C2Sain.Patches
{
    /// <summary>Routes every SAIN decision for a carry bot through <see cref="DecisionPolicy"/>.</summary>
    public sealed class SainSetDecisionsPatch : ModulePatch
    {
        private static bool _loggedError;

        /// <summary>Target: SAIN's BotDecisionManager.SetDecisions.</summary>
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotDecisionManager), "SetDecisions");

        /// <summary>Rewrites the decisions in place for carry bots; other bots pass through untouched.</summary>
        [PatchPrefix]
        public static void Prefix(BotDecisionManager __instance, ref ECombatDecision solo, ref ESquadDecision squad, ref ESelfActionType self, Enemy enemy)
        {
            try
            {
                DecisionPolicy.Apply(__instance.Bot, ref solo, ref squad, ref self, enemy);
            }
            catch (Exception e)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    McsLogger.LogError(e);
                }
            }
        }
    }
}
