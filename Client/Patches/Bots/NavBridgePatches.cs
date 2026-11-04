using System.Reflection;
using EFT;
using EFT.ObstacleCollision;
using HarmonyLib;
using MiyakoCarryService.Client.Bots.Navigation;
using SPT.Reflection.Patching;
using UnityEngine;

namespace MiyakoCarryService.Client.Patches.Bots
{
    /// <summary>
    /// 用于实现护航的各种翻越、跨越边缘能力
    /// </summary>
    public class BotMoverHopGuardPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotMover), "method_12");

        [PatchPrefix]
        public static bool Prefix(BotMover __instance)
        {
            var botOwner = __instance._owner;
            if (botOwner == null || !NavGapExecutor.IsHopping(botOwner))
            {
                return true;
            }

            return false;
        }
    }

    public class PlayerTeleportPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.Teleport));

        [PatchPrefix]
        public static bool Prefix(Player __instance, Vector3 position)
        {
            var botOwner = __instance.AIData?.BotOwner;
            if (botOwner == null)
            {
                return true;
            }

            if (NavGapExecutor.RescueInProgress)
            {
                return true;
            }

            if (NavGapExecutor.IsHopping(botOwner))
            {
                return false;
            }

            var distance = (position - botOwner.Position).magnitude;
            if (NavGapExecutor.EndedHopRecently(botOwner) && distance > 2f)
            {
                return false;
            }

            return true;
        }
    }

    public class CastFromPosPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotMover), nameof(BotMover.CastFromPos));

        [PatchPrefix]
        public static bool Prefix(BotMover __instance, Vector3 posiblePos, ref EBotLinkResult __result)
        {
            if (NavGapExecutor.RescueInProgress)
            {
                return true;
            }

            var owner = __instance._owner;
            if (owner == null)
            {
                return true;
            }

            if (NavGapExecutor.IsHopping(owner))
            {
                __result = EBotLinkResult.superFail;
                return false;
            }

            if (NavGapExecutor.RelinkInProgress && posiblePos.y - owner.Position.y > 0.5f)
            {
                __result = EBotLinkResult.fail;
                return false;
            }

            if (NavGapExecutor.IsApproaching(owner) && (posiblePos - owner.Position).magnitude > 0.6f)
            {
                __result = EBotLinkResult.fail;
                return false;
            }

            return true;
        }
    }

    public class FindBetterPositionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotMover), nameof(BotMover.FindBetterPosition));

        [PatchPrefix]
        public static bool Prefix(BotMover __instance, Vector3 castPoint, ref Vector3 __result)
        {
            if (NavGapExecutor.RescueInProgress || !NavGapExecutor.IsApproaching(__instance._owner))
            {
                return true;
            }

            __result = castPoint;
            return false;
        }
    }

    public class SetPlayerToNavMeshPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotMover), nameof(BotMover.SetPlayerToNavMesh));

        [PatchPrefix]
        public static void Prefix(BotMover __instance, ref Vector3 castPoint)
        {
            if (NavGapExecutor.RescueInProgress || !NavGapExecutor.IsApproaching(__instance._owner))
            {
                return;
            }

            var body = __instance._owner.Position;
            var toCast = castPoint - body;
            toCast.y = 0f;
            if (toCast.magnitude <= 0.3f)
            {
                return;
            }

            var clamped = body + toCast.normalized * 0.3f;
            clamped.y = castPoint.y;
            castPoint = clamped;
        }
    }

    public class NavBridgeObstaclePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ObstacleCollisionFacade), nameof(ObstacleCollisionFacade.RecalculateCollision));

        [PatchPrefix]
        public static bool Prefix(ObstacleCollisionFacade __instance)
        {
            var position = __instance._playerTransform.position;
            if (!NavGapExecutor.IsHoppingNear(position))
            {
                return true;
            }

            return false;
        }
    }

    public class BotMoverFixedUpdatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotMover), nameof(BotMover.ManualFixedUpdate));

        [PatchPostfix]
        public static void Postfix(BotMover __instance)
        {
            var botOwner = __instance._owner;
            if (botOwner == null)
            {
                return;
            }

            // Fork: vault/drop shortcuts and their navmesh warps are off by default.
            if (!C2Sain.ForkConfig.GapCrossing.Value)
            {
                return;
            }

            NavGapExecutor.OnMoverTick(botOwner);
        }
    }
}