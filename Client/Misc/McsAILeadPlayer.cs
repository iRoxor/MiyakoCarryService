
using System.Collections.Generic;
using EFT;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Models;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.Misc
{
    public class McsAILeadPlayer : AIBossPlayer
    {
        public McsBotPlayerConfig McsBotPlayerConfig
        {
            get
            {
                if (McsMgr.McsLeadPlayerConfigs.TryGetValue(McsLeadPlayer.ProfileId, out var mcsBotPlayerConfig))
                {
                    return mcsBotPlayerConfig;
                }
                else
                {
                    mcsBotPlayerConfig = new McsBotPlayerConfig
                    {
                        McsLeadPlayerId = McsLeadPlayer.ProfileId,
                        EnableLooting = MiyakoCarryServicePlugin.EnableLooting.Value,
                        PriceThreshold = MiyakoCarryServicePlugin.PriceThreshold.Value,
                        KeywordItemText = MiyakoCarryServicePlugin.KeywordItemText.Value,
                        LootingKeywordItem = MiyakoCarryServicePlugin.LootingKeywordItem.Value,
                        BlockItemType = (int)MiyakoCarryServicePlugin.BlockItemType.Value,
                        EnableKeepFormation = MiyakoCarryServicePlugin.EnableKeepFormation.Value,
                        FormationMatrix = MiyakoCarryServicePlugin.FormationMatrix.Value,
                        FormationSpacing = MiyakoCarryServicePlugin.FormationSpacing.Value,
                        FormationSequentialFill = MiyakoCarryServicePlugin.FormationSequentialFill.Value,
                        PhrasesSilent = MiyakoCarryServicePlugin.PhrasesSilent.Value,
                        EnableSubtitles = MiyakoCarryServicePlugin.EnableSubtitles.Value,
                    };
                    McsMgr.UpdateMcsBotPlayerConfig(mcsBotPlayerConfig.McsLeadPlayerId, mcsBotPlayerConfig);
                    return mcsBotPlayerConfig;
                }
            }
        }
        public Player McsLeadPlayer;
        public GamePlayerOwner GamePlayerOwner => McsLeadPlayer.GetGamePlayerOwner();
        public McsAILeadPlayer(Player player) : base(player)
        {
            McsLeadPlayer = player;
        }

        public Vector3 ClearAreaCacheCenter;
        public float ClearAreaCacheTime;
        public List<Player> ClearAreaCacheMembers;
        public List<List<Vector3>> ClearAreaCacheSegments;
        public const float LEAD_THREAT_WINDOW = 3f;
        public Player LastLeadAttackerPlayer = null;
        public float LastLeadAttackerTime = -999f;
        public const float LEAD_AIMING_WINDOW = 2f;
        public Player LastLeadAimingEnemyPlayer = null;
        public float LastLeadAimingTime = -999f;
        public List<Player> LeadVisibleEnemies = new();
        public float LastLeadShotReportTime = -999f;
        /// <summary>Fork: last time the player's shot matched an enemy (MakingShotPatch); read by C2Sain/TeamModes.</summary>
        public float LastLeadEngageShotTime = -999f;
        public float NextEnemyCleanupTime = -999f;

        public bool IsLeadThreatEnemy(IPlayer enemy)
        {
            if (enemy == null)
            {
                return false;
            }

            var time = Time.time;
            if (LastLeadAttackerPlayer != null
                && enemy.ProfileId == LastLeadAttackerPlayer.ProfileId
                && time - LastLeadAttackerTime <= LEAD_THREAT_WINDOW)
            {
                return true;
            }

            if (LastLeadAimingEnemyPlayer != null
                && enemy.ProfileId == LastLeadAimingEnemyPlayer.ProfileId
                && time - LastLeadAimingTime <= LEAD_AIMING_WINDOW)
            {
                return true;
            }

            return false;
        }

        public Player GetLeadThreatEnemy()
        {
            if (LastLeadAttackerPlayer != null
                && LastLeadAttackerPlayer.HealthController != null
                && LastLeadAttackerPlayer.HealthController.IsAlive
                && Time.time - LastLeadAttackerTime <= LEAD_THREAT_WINDOW)
            {
                return LastLeadAttackerPlayer;
            }

            if (LastLeadAimingEnemyPlayer != null
                && LastLeadAimingEnemyPlayer.HealthController != null
                && LastLeadAimingEnemyPlayer.HealthController.IsAlive
                && Time.time - LastLeadAimingTime <= LEAD_AIMING_WINDOW)
            {
                return LastLeadAimingEnemyPlayer;
            }

            return null;
        }

        private static McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        public void CleanupDeadEnemies()
        {
            if (LastLeadAttackerPlayer != null && !LastLeadAttackerPlayer.HealthController.IsAlive)
            {
                LastLeadAttackerPlayer = null;
            }

            if (LastLeadAimingEnemyPlayer != null && !LastLeadAimingEnemyPlayer.HealthController.IsAlive)
            {
                LastLeadAimingEnemyPlayer = null;
            }

            LeadVisibleEnemies.RemoveAll(enemy => enemy == null || enemy.HealthController == null || !enemy.HealthController.IsAlive);

            var mcsBotPlayers = McsMgr.GetAllMcsSquadMembersByMcsLeadId(McsLeadPlayer.ProfileId);
            foreach (var mcsBotPlayer in mcsBotPlayers)
            {
                var botOwner = mcsBotPlayer?.BotOwner;
                if (botOwner == null || botOwner.EnemiesController == null)
                {
                    continue;
                }

                var deadEnemies = new List<IPlayer>();
                foreach (var kvp in botOwner.EnemiesController.EnemyInfos)
                {
                    if (kvp.Key == null || kvp.Value?.Person == null || kvp.Value.Person.HealthController == null || !kvp.Value.Person.HealthController.IsAlive)
                    {
                        deadEnemies.Add(kvp.Key);
                    }
                }

                foreach (var deadEnemy in deadEnemies)
                {
                    if (botOwner.Memory.GoalEnemy?.Person == deadEnemy)
                    {
                        botOwner.Memory.GoalEnemy = null;
                    }

                    if (botOwner.EnemiesController.EnemyInfos.ContainsKey(deadEnemy))
                    {
                        botOwner.EnemiesController.Remove(deadEnemy);
                    }
                }
            }
        }


        public float LastLeadReportTime = -999f;

        public void MarkLeadAttacker(Player attacker)
        {
            if (attacker == null || Tools.IsForbiddenEnemy(attacker))
            {
                return;
            }

            LastLeadAttackerPlayer = attacker;
            LastLeadAttackerTime = Time.time;
        }

        public void MarkLeadAimingEnemy(Player aimingEnemy)
        {
            if (aimingEnemy == null || Tools.IsForbiddenEnemy(aimingEnemy))
            {
                return;
            }

            LastLeadAimingEnemyPlayer = aimingEnemy;
            LastLeadAimingTime = Time.time;
        }

        public void CalcGoalEnemy(Player seenEnemy)
        {
            CleanupDeadEnemies();

            if (seenEnemy == null || !seenEnemy.HealthController.IsAlive)
            {
                return;
            }

            if (Tools.IsForbiddenEnemy(seenEnemy))
            {
                return;
            }

            var mcsBotPlayers = McsMgr.GetAllMcsSquadMembersByMcsLeadId(McsLeadPlayer.ProfileId);
            var isLeadThreatEnemy = IsLeadThreatEnemy(seenEnemy);

            foreach (var mcsBotPlayer in mcsBotPlayers)
            {
                var botOwner = mcsBotPlayer.BotOwner;

                // 敌人表接近 EFT 硬上限时先腾位置，腾不出来就放弃这次加敌，绝不把表灌到崩溃线
                if (!Tools.TryMakeRoomForEnemy(botOwner))
                {
                    continue;
                }

                McsLeadPlayer.BotsGroup.AddEnemy(seenEnemy, EBotEnemyCause.callForHelp2);

                if (!botOwner.EnemiesController.EnemyInfos.TryGetValue(seenEnemy, out var enemyInfo))
                {
                    if (!TryRegisterEnemyForFollower(botOwner, seenEnemy, out enemyInfo))
                    {
                        continue;
                    }
                }

                if (!isLeadThreatEnemy && ShouldKeepCurrentGoalEnemy(botOwner, enemyInfo))
                {
                    continue;
                }

                enemyInfo.IsVisible = true;
                botOwner.Memory.GoalEnemy = enemyInfo;
                enemyInfo.PriorityIndex = 0;
            }
        }

        private bool TryRegisterEnemyForFollower(BotOwner botOwner, Player seenEnemy, out EnemyInfo enemyInfo)
        {
            enemyInfo = null;

            if (botOwner?.EnemiesController == null || botOwner.BotsGroup == null)
            {
                return false;
            }

            // 绕过 BotMemory.AddEnemy 自己注册敌人，必须先确保敌人表有位置
            if (!Tools.TryMakeRoomForEnemy(botOwner))
            {
                return false;
            }

            if (!McsLeadPlayer.BotsGroup.Enemies.TryGetValue(seenEnemy, out var groupInfo))
            {
                groupInfo = new BotGroupEnemyInfo(seenEnemy, McsLeadPlayer.BotsGroup, EBotEnemyCause.callForHelp2);
            }

            enemyInfo = botOwner.EnemiesController.AddNew(botOwner.BotsGroup, seenEnemy, groupInfo);
            botOwner.EnemiesController.SetInfo(seenEnemy, enemyInfo);

            // 原生 BotMemory.AddEnemy 会订阅 DiedEvent 自动回收，这里自己注册的同理补上，
            // 否则这类敌人死后永远留在敌人表里，只增不减
            seenEnemy.HealthController.DiedEvent += (EDamageType damageType) =>
            {
                if (botOwner?.EnemiesController == null || botOwner.Memory == null)
                {
                    return;
                }

                if (!botOwner.EnemiesController.EnemyInfos.ContainsKey(seenEnemy))
                {
                    return;
                }

                if (botOwner.Memory.GoalEnemy?.Person?.ProfileId == seenEnemy.ProfileId)
                {
                    botOwner.Memory.GoalEnemy = null;
                }

                botOwner.EnemiesController.Remove(seenEnemy);
            };

            var time = Time.time;
            enemyInfo.HaveSeenPersonal = true;
            enemyInfo.PersonalSeenTime = time;
            enemyInfo.PersonalLastSeenTime = time;
            enemyInfo.PersonalLastPos = seenEnemy.Position;
            if (enemyInfo.FirstTimeSeen < 0f)
            {
                enemyInfo.FirstTimeSeen = time;
            }

            var botEyePos = botOwner.Position + Vector3.up * 1.6f;
            var blocked = Physics.Linecast(
                botEyePos,
                seenEnemy.Position + Vector3.up * 1.6f,
                out _,
                LayersMaskController.HighPolyWithTerrainMask
            );
            enemyInfo.SetVisible(!blocked);
            return true;
        }

        private bool ShouldKeepCurrentGoalEnemy(BotOwner botOwner, EnemyInfo reportedEnemy)
        {
            var currentGoalEnemy = botOwner.Memory.GoalEnemy;
            if (currentGoalEnemy == null || currentGoalEnemy == reportedEnemy)
            {
                return false;
            }

            var currentPerson = currentGoalEnemy.Person;
            if (currentPerson == null || currentPerson.HealthController == null || !currentPerson.HealthController.IsAlive)
            {
                return false;
            }

            if (!currentGoalEnemy.IsVisible || !currentGoalEnemy.CanShoot)
            {
                return false;
            }

            return reportedEnemy.Distance > currentGoalEnemy.Distance * 0.75f;
        }

        public EnemyInfo GetClosestEnemy(List<EnemyInfo> enemiesInfos)
        {
            if (enemiesInfos.Count == 0)
            {
                return null;
            }

            EnemyInfo closestEnemy = null;

            var minDistance = Mathf.Infinity;
            foreach (var enemyInfo in enemiesInfos)
            {
                var distance = Position.McsSqrDistance(enemyInfo.CurrPosition);
                if (distance < minDistance)
                {
                    closestEnemy = enemyInfo;
                    minDistance = distance;
                }
            }

            return closestEnemy;
        }
    }
}