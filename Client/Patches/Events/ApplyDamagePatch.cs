using System;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using HarmonyLib;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Models;
using MiyakoCarryService.Client.Utils;
using SPT.Reflection.Patching;
using UnityEngine;

namespace MiyakoCarryService.Client.Patches.Events
{
    /// <summary>
    /// 监测玩家对护航的误伤情况，并视情况触发对应事件
    /// </summary>
    public sealed class ApplyDamagePatch : ModulePatch
    {
        private static McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.ApplyDamage));

#if DEBUG
        [PatchPrefix]
        public static void Prefix(Player ___Player, EBodyPart bodyPart, ref float damage, DamageInfo damageInfo)
        {
            if (___Player == null || ___Player.IsYourPlayer)
            {
                return;
            }

            if (!McsMgr.IsMcsBotPlayer(___Player.ProfileId))
            {
                return;
            }

            if (MiyakoCarryServicePlugin.McsBotPlayerNoDamage.Value)
            {
                damage = 0;
                return;
            }

            if (MiyakoCarryServicePlugin.McsBotPlayerKeepAlive.Value)
            {
                var healthController = ___Player.ActiveHealthController;
                var currentHealth = healthController.GetBodyPartHealth(bodyPart, false);
                var maxAllowedDamage = Math.Max(0f, currentHealth.Current - 1f);  
                damage = Math.Min(damage, maxAllowedDamage);  
                return;
            }
        }
#endif

        [PatchPostfix]
        public static void Postfix(ActiveHealthController __instance, Player ___Player, EBodyPart bodyPart, float damage, DamageInfo damageInfo)
        {
            if (___Player == null)
            {
                return;
            }

            if (!Tools.IsHost)
            {
                return;
            }

            var attacker = damageInfo.Player?.iPlayer;
            if (attacker != null && attacker is Player attackerPlayer && McsMgr.IsMcsBotPlayer(___Player.ProfileId) && !McsMgr.IsMcsBotPlayer(attackerPlayer.ProfileId))
            {
                var mcsBotPlayerData = ___Player.AIData.BotOwner.GetMcsBotPlayerData();
                if (mcsBotPlayerData != null)
                {
                    mcsBotPlayerData.LastHitTime = Time.time;
                    mcsBotPlayerData.LastHitShooter = Tools.IsForbiddenEnemy(attackerPlayer) ? null : attackerPlayer;
                    mcsBotPlayerData.AddSuppression(damage);
                    if (mcsBotPlayerData.LastHitShooter != null)
                    {
                        // Fork v18: whoever hits a carry bot has attacked the squad first (the new Passive stance may fire back at it).
                        C2Sain.TeamModes.MarkHostile(mcsBotPlayerData.McsAILeadPlayer, attackerPlayer.ProfileId, attackerPlayer.Profile?.Nickname, C2Sain.Core.HostileCause.HitBot);
                    }
                }
            }

            if (damage <= 0)
            {
                return;
            }

            if (attacker == null)
            {
                return;
            }

            var isMcsBotAttacker = McsMgr.IsMcsBotPlayer(attacker.ProfileId);
            var isMcsLeadAttacker = McsMgr.IsMcsLeadPlayer(attacker.ProfileId);
            var isMcsBotInjuredPlayer = McsMgr.IsMcsBotPlayer(___Player.ProfileId);
            var isMcsLeadInjuredPlayer = McsMgr.IsMcsLeadPlayer(___Player.ProfileId);

            var commonHp = __instance.GetBodyPartHealth(EBodyPart.Common);
            var headHp = __instance.GetBodyPartHealth(EBodyPart.Head);
            var chestHp = __instance.GetBodyPartHealth(EBodyPart.Chest);
            var isDead = commonHp.AtMinimum || headHp.AtMinimum || chestHp.AtMinimum;

            if (isDead)
            {
                if (isMcsBotInjuredPlayer)
                {
                    var mcsLeadPlayer = ___Player.AIData.BotOwner.GetMcsBotPlayerData().LeadPlayer;
                    var mcsBotPlayer = McsMgr.GetAllAliveMcsSquadMembersByMcsLeadId(mcsLeadPlayer.ProfileId).FirstOrDefault();
                    if (mcsBotPlayer != null)
                    {
                        mcsBotPlayer.BotOwner.TalkMsg(mcsLeadPlayer, mcsBotPlayer.BotOwner.GetPlayer, new McsMsg
                        {
                            PhraseTrigger = EPhraseTrigger.OnFriendlyDown
                        });
                    }
                }
                else if (isMcsBotAttacker && !isMcsLeadInjuredPlayer)
                {
                    var mcsBotPlayerBotOwner = attacker.AIData.BotOwner;
                    if (mcsBotPlayerBotOwner != null)
                    {
                        var mcsLeadPlayer = mcsBotPlayerBotOwner.GetMcsBotPlayerData().LeadPlayer;
                        mcsBotPlayerBotOwner.TalkMsg(mcsLeadPlayer, mcsBotPlayerBotOwner.GetPlayer, new McsMsg
                        {
                            PhraseTrigger = EPhraseTrigger.EnemyDown
                        });
                    }
                }
            }

            var notMcsLeaderButIsFikaPlayer = attacker.Profile.Info.GroupId == "Fika";
            if (isMcsBotAttacker && isMcsLeadInjuredPlayer)
            {
                if (!isDead)
                {
                    return;
                }
                McsMgr.SendCompensation(___Player.ProfileId);
            }
            else if (isMcsBotInjuredPlayer && !isMcsBotAttacker && (notMcsLeaderButIsFikaPlayer || isMcsLeadAttacker))
            {
                McsMgr.AddPunish(attacker.ProfileId, isDead ? 0.1560d : 0.0107d, isDead, notMcsLeaderButIsFikaPlayer);
            }
        }
    }
}
