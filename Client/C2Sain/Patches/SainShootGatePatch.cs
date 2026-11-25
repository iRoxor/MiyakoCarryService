// SainShootGatePatch.cs -- Harmony prefixes on the two methods through which SAIN 4.5.1 fires a carry bot's weapon, so the
// new Passive stance (fork v18) never fires at an enemy it may not (Core/HoldFire.cs, C2Sain/HoldFireGate.cs).
//
// Enabled by MiyakoCarryServicePlugin.EnableAllPatches (SainShootGatePatch and SainManualShootGatePatch). Read in SAIN 4.5.1 with
// cecil_il.ps1 and il_dump.py (2026-11-25):
//   - SAINShootData.AimAndShootAtEnemy(Enemy, BotComponent) is the only caller of ShootData.Shoot() for aimed fire. Its two
//     callers are GetEnemyToShoot (the decision's enemy first) and CheckEnemiesForShootableTargets (then ANY other enemy that
//     has been visible for 0.33 s), reached from ShootAnyVisibleEnemies, which every combat action calls from its steering tick
//     (StandAndShoot, SeekCover, ShiftCover, Search, Rush, MoveToEngage, Regroup, Suppress, AvoidGrenade ...). A decision
//     about one enemy therefore does not stop a shot at another, and SeekCover fires at a visible enemy while the bot runs.
//   - ManualShootClass.TryShoot(Enemy, Vector3, bool, EShootReason) is the other: suppressive fire at a place
//     (SAINBotSuppressClass.SuppressPosition), blind fire from cover (BlindFireController) and the flashed bot's return fire.
// For a carry bot in the new Passive each prefix answers "no shot" (false, SAIN's own "did not shoot") when the enemy is not
// hostile or is beyond the Passive range, and always for a suppressive shot. Every other bot, and every other stance, passes
// through untouched. A fault while deciding for a Passive bot blocks the shot (the stance's promise is not to fire), and the
// exception is logged once. Reads the squad's hostile set and the bot's stance; writes and logs nothing else.

using System;
using System.Reflection;
using HarmonyLib;
using MiyakoCarryService.Client.Utils;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SPT.Reflection.Patching;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain.Patches
{
    /// <summary>Stops SAIN's aimed fire (SAINShootData.AimAndShootAtEnemy) at enemies a Passive carry bot may not fire at.</summary>
    public sealed class SainShootGatePatch : ModulePatch
    {
        private static bool _loggedError;

        /// <summary>Target: SAIN's SAINShootData.AimAndShootAtEnemy(Enemy, BotComponent), whatever its visibility.</summary>
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SAINShootData), "AimAndShootAtEnemy");

        /// <summary>
        /// Answers "no shot" for a Passive carry bot's target that is not hostile or is out of range; runs SAIN's own method
        /// otherwise.
        /// </summary>
        /// <param name="__0">The enemy SAIN is about to aim at and fire on.</param>
        /// <param name="__1">The bot's SAIN component.</param>
        /// <param name="__result">AimAndShootAtEnemy's answer when the original is skipped.</param>
        [PatchPrefix]
        public static bool Prefix(Enemy __0, BotComponent __1, ref bool __result)
        {
            EFT.BotOwner botOwner;
            try
            {
                botOwner = __1?.BotOwner;
                if (__0 == null || !HoldFireGate.AppliesTo(botOwner))
                {
                    return true;
                }
            }
            catch (Exception e)
            {
                LogOnce(e);
                return true;
            }

            try
            {
                if (HoldFireGate.Allows(botOwner, __0.EnemyProfileId, __0.EnemyPosition))
                {
                    return true;
                }
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
            __result = false;
            return false;
        }

        /// <summary>Logs the first exception either shot-gate patch meets; later ones are dropped.</summary>
        internal static void LogOnce(Exception e)
        {
            if (!_loggedError)
            {
                _loggedError = true;
                McsLogger.LogError(e);
            }
        }
    }

    /// <summary>Stops SAIN's manual and suppressive fire (ManualShootClass.TryShoot) for a Passive carry bot.</summary>
    public sealed class SainManualShootGatePatch : ModulePatch
    {
        /// <summary>Target: SAIN's ManualShootClass.TryShoot(Enemy, Vector3, bool, EShootReason).</summary>
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ManualShootClass), "TryShoot");

        /// <summary>
        /// Answers "no shot" for a Passive carry bot's suppressive shot, and for any other manual shot at an enemy that is not
        /// hostile or is out of range; runs SAIN's own method otherwise.
        /// </summary>
        /// <param name="__instance">SAIN's manual shooting class for the bot.</param>
        /// <param name="__0">The enemy the shot is for; null runs SAIN's own method, which refuses it.</param>
        /// <param name="__3">Why SAIN wants the shot (suppression, blind fire ...).</param>
        /// <param name="__result">TryShoot's answer when the original is skipped.</param>
        [PatchPrefix]
        public static bool Prefix(ManualShootClass __instance, Enemy __0, EShootReason __3, ref bool __result)
        {
            EFT.BotOwner botOwner;
            try
            {
                botOwner = __instance?.BotOwner;
                if (__0 == null || !HoldFireGate.AppliesTo(botOwner))
                {
                    return true;
                }
            }
            catch (Exception e)
            {
                SainShootGatePatch.LogOnce(e);
                return true;
            }

            try
            {
                var suppression = __3 == EShootReason.SquadSuppressing || __3 == EShootReason.WalkToCoverSuppress || __3 == EShootReason.Suppress;
                if (HoldFireGate.AllowsManual(botOwner, suppression, __0.EnemyProfileId, __0.EnemyPosition))
                {
                    return true;
                }
            }
            catch (Exception e)
            {
                SainShootGatePatch.LogOnce(e);
            }
            __result = false;
            return false;
        }
    }
}
