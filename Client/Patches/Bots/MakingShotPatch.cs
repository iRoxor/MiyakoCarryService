using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using JetBrains.Annotations;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Utils;
using SPT.Reflection.Patching;
using UnityEngine;

namespace MiyakoCarryService.Client.Patches.Bots
{
    /// <summary>
    /// 老板开火时护航能沿弹道方向推断交战目标
    /// </summary>
    public sealed class MakingShotPatch : ModulePatch
    {
        private static McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.OnMakingShot));

        [PatchPostfix]
        public static void Postfix(Player __instance, [NotNull] IWeapon weapon, Vector3 force)
        {
            if (__instance == null || !__instance.HealthController.IsAlive || __instance.IsAI)
            {
                return;
            }

            if (!McsMgr.IsMcsLeadPlayer(__instance.ProfileId))
            {
                return;
            }

            var mcsAILeadPlayer = McsMgr.GetMcsAILeadPlayerByMcsLeadPlayerId(__instance.ProfileId);
            if (mcsAILeadPlayer == null)
            {
                return;
            }

            var time = Time.time;
            if (time - mcsAILeadPlayer.LastLeadShotReportTime < 0.15f)
            {
                return;
            }
            mcsAILeadPlayer.LastLeadShotReportTime = time;

            var shotOrigin = __instance.Position + Vector3.up * 1.6f;
            var shotDirection = __instance.LookDirection;

            var engagementTarget = FindShotEngagementTarget(__instance, shotOrigin, shotDirection);
            if (engagementTarget == null)
            {
                return;
            }

            mcsAILeadPlayer.LastLeadEngageShotTime = time; // Fork: the player fired at an identified enemy
            mcsAILeadPlayer.CalcGoalEnemy(engagementTarget);
        }

        private static Player FindShotEngagementTarget(Player leadPlayer, Vector3 shotOrigin, Vector3 shotDirection)
        {
            Player bestTarget = null;
            var bestScore = float.MaxValue;

            foreach (var target in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                if (target == null || !target.HealthController.IsAlive || !target.IsAI)
                {
                    continue;
                }

                if (McsMgr.IsMcsBotPlayer(target.ProfileId) || McsMgr.IsMcsLeadPlayer(target.ProfileId))
                {
                    continue;
                }

                if (leadPlayer.BotsGroup == null || !leadPlayer.BotsGroup.IsEnemy(target))
                {
                    continue;
                }

                var headOffset = target.Position + Vector3.up * 1.6f - shotOrigin;
                var bodyOffset = target.Position + Vector3.up * 0.8f - shotOrigin;

                foreach (var targetOffset in new[] { headOffset, bodyOffset })
                {
                    var alongRay = Vector3.Dot(targetOffset, shotDirection);
                    if (alongRay <= 0f || alongRay > 300f)
                    {
                        continue;
                    }

                    var perpendicularDistance = (targetOffset - shotDirection * alongRay).magnitude;
                    var allowedDistance = Mathf.Clamp(0.65f + alongRay * 0.012f, 0.8f, 2f);
                    if (perpendicularDistance > allowedDistance)
                    {
                        continue;
                    }

                    var score = perpendicularDistance * perpendicularDistance + alongRay * 0.0001f;
                    if (score < bestScore)
                    {
                        bestTarget = target;
                        bestScore = score;
                    }
                }
            }

            return bestTarget;
        }
    }
}