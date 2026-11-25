using System.Reflection;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Misc;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Utils;
using SPT.Reflection.Patching;
using Systems.Effects;
using UnityEngine;

namespace MiyakoCarryService.Client.Patches.Bots
{
    /// <summary>
    /// 借鉴friendlyPmc
    /// </summary>
    public sealed class PlayHitEffectPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(EffectsCommutator), nameof(EffectsCommutator.PlayHitEffect));

        private static McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        [PatchPostfix]
        public static void Postfix(EffectsCommutator __instance, Shot info, PlayerHitInfo playerHitInfo)
        {
            if (!Tools.IsHost)
            {
                return;
            }
            
            var shooter = info.Player;
            if (shooter == null)
            {
                return;
            }

            if (shooter.iPlayer.Profile.Info.GroupId is "Fika" or "Mcs" || McsMgr.IsMcsLeadPlayer(shooter.iPlayer.ProfileId) || McsMgr.IsMcsBotPlayer(shooter.iPlayer.ProfileId))
            {
                return;
            }

            if (!__instance.IsHitPointAlreadyProcessed(info.HitPoint))
            {
                foreach (var mcsBotPlayer in McsMgr.GetAllAliveMcsBotPlayer())
                {
                    var botOwner = mcsBotPlayer.AIData.BotOwner;
                    if (mcsBotPlayer.Position.McsSqrDistance(info.HitPoint) <= botOwner.Settings.FileSettings.Mind.BULLET_FEEL_CLOSE_SDIST * botOwner.Settings.FileSettings.Mind.BULLET_FEEL_CLOSE_SDIST)
                    {
                        var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
                        if (mcsBotPlayerData != null)
                        {
                            mcsBotPlayerData.LastShotAtTime = Time.time;
                            mcsBotPlayerData.AddSuppressionFromNearMiss();
                            MarkHostile(mcsBotPlayerData.McsAILeadPlayer, shooter.iPlayer, HostileCause.ShotNearBot);
                        }

                        if (!Tools.TryMakeRoomForEnemy(mcsBotPlayer.BotsGroup))
                        {
                            continue;
                        }

                        mcsBotPlayer.BotsGroup.AddEnemy(shooter.iPlayer, EBotEnemyCause.callForHelp1);
                        var mcsLeadPlayer = McsMgr.GetMcsLeadPlayerByMcsBotPlayerId(mcsBotPlayer.ProfileId);
                        var mcsAILeadPlayer = McsMgr.GetMcsAILeadPlayerByMcsLeadPlayerId(mcsLeadPlayer.ProfileId);
                        if (shooter.iPlayer is Player shooterPlayer)
                        {
                            mcsAILeadPlayer.CalcGoalEnemy(shooterPlayer);
                        }
                    }
                }

                // Fork v18: the same test against each player the squads follow. A shooter whose bullet landed that near the
                // player has attacked the squad, which is what the new Passive stance needs to know (Core/HoldFire.cs).
                foreach (var mcsAILeadPlayer in McsMgr.GetAllMcsAILeadPlayer())
                {
                    var leadPlayer = mcsAILeadPlayer?.McsLeadPlayer;
                    if (leadPlayer == null || leadPlayer.HealthController == null || !leadPlayer.HealthController.IsAlive)
                    {
                        continue;
                    }
                    var near = NearBulletMetres(leadPlayer);
                    if (leadPlayer.Position.McsSqrDistance(info.HitPoint) <= near * near)
                    {
                        MarkHostile(mcsAILeadPlayer, shooter.iPlayer, HostileCause.ShotNearPlayer);
                    }
                }
            }
        }

        /// <summary>
        /// Fork v18: marks the shooter hostile to the squad (TeamModes.MarkHostile), unless it is one the fork never fights (the BTR
        /// gunner, Zryachiy and his guards). Never throws into the game's hit effect: a fault is dropped.
        /// </summary>
        /// <param name="lead">The squad's lead object; null marks nothing.</param>
        /// <param name="shooter">Whoever fired the bullet.</param>
        /// <param name="cause">ShotNearBot or ShotNearPlayer.</param>
        private static void MarkHostile(McsAILeadPlayer lead, IPlayer shooter, HostileCause cause)
        {
            try
            {
                if (lead != null && shooter != null && !Tools.IsForbiddenEnemy(shooter))
                {
                    C2Sain.TeamModes.MarkHostile(lead, shooter.ProfileId, shooter.Profile?.Nickname, cause);
                }
            }
            catch (System.Exception)
            {
                // The hit effect belongs to the game; a failure to note an attacker must not break it.
            }
        }

        /// <summary>
        /// Fork v18: how close (m) a bullet must land to the player for its shooter to count as having shot near them: the
        /// distance the player's squad's bots use for themselves (the "bullet feel close" setting, up to 30 m), the largest of
        /// them, or 30 m when no bot of the squad is alive.
        /// </summary>
        /// <param name="leadPlayer">The player the squad follows.</param>
        private static float NearBulletMetres(Player leadPlayer)
        {
            var near = 0f;
            foreach (var member in McsMgr.GetAllAliveMcsSquadMembersByMcsLeadId(leadPlayer.ProfileId))
            {
                var mind = member?.AIData?.BotOwner?.Settings?.FileSettings?.Mind;
                if (mind != null && mind.BULLET_FEEL_CLOSE_SDIST > near)
                {
                    near = mind.BULLET_FEEL_CLOSE_SDIST;
                }
            }
            return near > 0f ? near : 30f;
        }
    }
}