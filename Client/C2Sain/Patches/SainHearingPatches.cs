// SainHearingPatches.cs -- the fork's two hooks into SAIN 4.5.1's hearing (fork v8).
//
//   SainGunshotRangePatch -- prefix on HearingInput.CheckAddSoundToCache(SoundEvent, float)
//       (SAIN/Classes/Bot/Sense/Hearing/HearingInput.cs:95), the one call through which SAIN hands
//       each sound to each bot. For carry bots it drops gunshots beyond "Hearing: gunshot range"
//       (Core/Hearing.cs, HearingFilter). Bullet fly-bys and impacts reach SAIN by other paths, so a
//       carry bot shot at from afar still knows it.
//   SainHeardPatch -- postfix on EnemyHearing.SetHeard(SAINHearingReport, float)
//       (SAIN/Classes/Bot/EnemyClasses/Other/EnemyHearing.cs:62), where SAIN records that a bot
//       heard an enemy. For carry bots it tells HeardWatch, which alerts the squad.
//
// Enabled by MiyakoCarryServicePlugin.EnableAllPatches. Each hook logs its first exception and then
// lets SAIN carry on, so a fork bug degrades to plain SAIN hearing. Reads and writes no files.

using System;
using System.Reflection;
using HarmonyLib;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Utils;
using SAIN.Classes.Bot.Sense.Hearing;
using SAIN.Components.BotComponentSpace.Classes.EnemyClasses;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Models.Structs;
using SPT.Reflection.Patching;

namespace MiyakoCarryService.Client.C2Sain.Patches
{
    /// <summary>Drops gunshots beyond the fork's range for carry bots; see the file header.</summary>
    public sealed class SainGunshotRangePatch : ModulePatch
    {
        private static readonly HearingFilterSettings _settings = new();
        private static bool _loggedError;

        /// <summary>Target: SAIN's HearingInput.CheckAddSoundToCache.</summary>
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(HearingInput), nameof(HearingInput.CheckAddSoundToCache));

        /// <summary>Returns false (SAIN skips the sound) for a gunshot beyond the range heard by a carry bot.</summary>
        [PatchPrefix]
        public static bool Prefix(HearingInput __instance, SoundEvent Sound, float PlayerDistance)
        {
            try
            {
                if (!Sound.IsGunShot || __instance?.BotOwner == null || !__instance.BotOwner.IsMcsBotPlayer)
                {
                    return true;
                }
                _settings.GunshotRange = ForkConfig.GunshotHearingRange?.Value ?? 25f;
                return HearingFilter.HearsGunshot(PlayerDistance, _settings);
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
    }

    /// <summary>Passes every enemy a carry bot hears to <see cref="HeardWatch"/>; see the file header.</summary>
    public sealed class SainHeardPatch : ModulePatch
    {
        private static bool _loggedError;

        /// <summary>Target: SAIN's EnemyHearing.SetHeard.</summary>
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(EnemyHearing), nameof(EnemyHearing.SetHeard));

        /// <summary>Runs after SAIN accepted the sound (a null result means SAIN ignored it).</summary>
        [PatchPostfix]
        public static void Postfix(EnemyHearing __instance, SAINHearingReport report, object __result)
        {
            try
            {
                if (__result == null || __instance?.BotOwner == null || !__instance.BotOwner.IsMcsBotPlayer)
                {
                    return;
                }
                HeardWatch.OnHeard(__instance, report.position, report.soundType);
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
