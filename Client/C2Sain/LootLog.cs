// LootLog.cs -- diagnostics: a log line when a carry bot picks something to loot on its own, and one
// when it takes the item or finds no room for it.
//
// Called from McsBotPlayerData.SetLootingTarget and TrySetEmergencyLootingTarget (the bot chose a
// target) and from GoToLootTargetLogic.StartLooting (how the pickup went). Writes BepInEx log lines
// only while "Debug: diagnostic log" is on, and changes nothing about how bots loot. Reads and writes
// no files.
//
// MCS's own search (PlayerDataMgr.RefreshMcsBotPlayersInterestingLoop, every 10 s) covers every item
// owner within 30 m of the bot: loose items, containers, and dead bodies, whose gear counts like a
// container's contents. Living players' gear is left out. These lines name which of those a target
// lies in, so a raid log shows whether bots resupply from bodies.
//
// Line formats, read by .for-claude-to-reference/tools/mcs_fork_log_summary.py:
//   "<bot>: loot target: <item> (<why>) <where>, <N> m away"
//       why:   "short of magazines" | "short of ammo" | "short of meds" (emergency resupply, which runs
//              whatever Enable Looting says) | "worth <price> <currency>" | "a better container"
//       where: "on the body of <name>" | "in <container or item>" | "on the ground"
//   "<bot>: loot taken: <item>"   or   "<bot>: loot not taken: <item> (no room)"

using System;
using EFT.InventoryLogic;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Enums;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Loot diagnostics; see the file header.</summary>
    internal static class LootLog
    {
        /// <summary>Seconds between two error lines from this log, so a fault cannot flood it.</summary>
        private const float ERROR_LOG_INTERVAL = 30f;

        private static float _nextErrorLog;

        /// <summary>The "why" text for an emergency resupply target.</summary>
        internal static string NeedText(ELootNeedType need)
        {
            return need switch
            {
                ELootNeedType.Magazine => "short of magazines",
                ELootNeedType.Ammo => "short of ammo",
                ELootNeedType.Meds => "short of meds",
                _ => "short of " + need,
            };
        }

        /// <summary>Logs a target the bot chose. Never throws.</summary>
        /// <param name="data">The carry bot.</param>
        /// <param name="target">The item it chose.</param>
        /// <param name="why">The reason, in the header's "why" wording.</param>
        internal static void Target(McsBotPlayerData data, LootData target, string why)
        {
            if (ForkConfig.DebugLog?.Value != true || data == null || target == null)
            {
                return;
            }
            try
            {
                var player = data.Player;
                var at = target.RootTransform;
                var distance = player != null && at != null ? Vector3.Distance(player.Position, at.position).ToString("0") : "?";
                McsLogger.LogInfo($"[{ForkInfo.Label}] {player?.Profile?.Nickname}: loot target: {ItemName(target)} ({why}) {Where(target)}, {distance} m away");
            }
            catch (Exception e)
            {
                LogFault(e);
            }
        }

        /// <summary>Logs how a pickup went: taken, or no room for it. Never throws.</summary>
        internal static void Result(McsBotPlayerData data, LootData target, bool taken)
        {
            if (ForkConfig.DebugLog?.Value != true || data == null || target == null)
            {
                return;
            }
            try
            {
                var nickname = data.Player?.Profile?.Nickname;
                McsLogger.LogInfo(taken
                    ? $"[{ForkInfo.Label}] {nickname}: loot taken: {ItemName(target)}"
                    : $"[{ForkInfo.Label}] {nickname}: loot not taken: {ItemName(target)} (no room)");
            }
            catch (Exception e)
            {
                LogFault(e);
            }
        }

        /// <summary>The item's display name.</summary>
        private static string ItemName(LootData target)
        {
            return target.Item?.Name?.McsLocalized() ?? "?";
        }

        /// <summary>Where the item lies: on a body, inside a container or another item, or on its own on the ground.</summary>
        private static string Where(LootData target)
        {
            var item = target.Item;
            var root = item?.GetRootItem();
            if (root == null)
            {
                return "somewhere unknown";
            }
            if (root.GetData() is PlayerData body)
            {
                return $"on the body of {body.Player?.Profile?.Nickname ?? "someone"}";
            }
            if (root != item)
            {
                return $"in {root.Name?.McsLocalized() ?? "a container"}";
            }
            return "on the ground";
        }

        /// <summary>Writes an error line at most every ERROR_LOG_INTERVAL seconds.</summary>
        private static void LogFault(Exception e)
        {
            if (Time.time >= _nextErrorLog)
            {
                _nextErrorLog = Time.time + ERROR_LOG_INTERVAL;
                McsLogger.LogError($"[{ForkInfo.Label}] loot log failed: {e}");
            }
        }
    }
}
