using System.Reflection;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using MiyakoCarryService.Client.Extensions;
using SPT.Reflection.Patching;
using UnityEngine;

namespace MiyakoCarryService.Client.Patches.Bots
{
    /// <summary>
    /// 护航快速开门
    /// </summary>
    public sealed class BotDoorOpenerUpdateStatusPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotDoorOpener), nameof(BotDoorOpener.UpdateDoorInteractionStatus));

        [PatchPrefix]
        public static bool Prefix(BotDoorOpener __instance, ref DoorInteractionStatus __result)
        {
            var botOwner = __instance._owner;
            if (botOwner == null || !botOwner.IsMcsBotPlayer)
            {
                return true;
            }

            // Fork: upstream's fast doors are off by default, so the game's own door handling runs.
            if (!C2Sain.ForkConfig.McsFastDoors.Value)
            {
                return true;
            }

            if (__instance.Interacting || botOwner.Mover.CurrentState == EBotMoverState.NearDoor)
            {
                return true;
            }

            if (IsInDoorSprintZone(botOwner))
            {
                __result = DoorInteractionStatus.OpeningDoor;
                botOwner.Mover.Sprint(false);
                botOwner.Mover.SprintPause(0.5f);
                return false;
            }

            __result = DoorInteractionStatus.CanRun;
            return false;
        }

        private static bool IsInDoorSprintZone(BotOwner botOwner)
        {
            var doorLinks = botOwner.NearDoorData.CurrentDoorLinks();
            if (doorLinks.Count == 0)
            {
                return false;
            }

            var botPosition = botOwner.Position;
            var targetPoint = botOwner.Mover.TargetPoint;
            var moveDir = Vector3.zero;
            if (targetPoint.HasValue)
            {
                moveDir = targetPoint.Value - botPosition;
                moveDir.y = 0f;
                if (moveDir.sqrMagnitude < 0.01f)
                {
                    return false;
                }
                moveDir.Normalize();
            }

            for (var i = 0; i < doorLinks.Count; i++)
            {
                var doorLink = doorLinks[i];
                var door = doorLink?.Door;
                if (door == null)
                {
                    continue;
                }

                var doorState = door.DoorState;
                if (doorState == EDoorState.Open)
                {
                    if (targetPoint.HasValue)
                    {
                        var segmentClose = doorLink.SegmentClose;
                        if (AIUtility.GetCrossPoint(botPosition, targetPoint.Value, segmentClose.a, segmentClose.b) != null)
                        {
                            return true;
                        }
                    }
                    continue;
                }

                var doorPos = doorState == EDoorState.Open ? doorLink.MidClose : doorLink.MidOpen;
                if (botPosition.McsSqrDistance(doorPos) > 27.04f)
                {
                    continue;
                }

                if (targetPoint.HasValue)
                {
                    var toDoor = doorPos - botPosition;
                    toDoor.y = 0f;
                    if (toDoor.sqrMagnitude < 0.01f || Vector3.Dot(moveDir, toDoor.normalized) < 0.6f)
                    {
                        continue;
                    }
                }

                return true;
            }

            return false;
        }
    }
}