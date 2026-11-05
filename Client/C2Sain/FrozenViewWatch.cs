// FrozenViewWatch.cs -- diagnostics: notices carry bots whose view has not moved for a while.
//
// Called about once a second per carry bot by PlayerDataMgr.RecordAgentInfo, beside OpenGroundWatch.
// It changes nothing about how bots behave. Core/FrozenViewTracker.cs keeps the runs: a bot standing
// (moving under 0.5 m a second) whose view stays within 5 degrees for 8 s, unless it is aiming at an
// enemy SAIN can see.
//   - Always: once a run reaches 8 s, the debug dump's "Frozen View (s)" line gets those 8
//     bot-seconds, then one more per sample until the run ends, under the reason the bot's brain
//     was running (LogBuffer, shown by MenuTaskBarAwakePatch).
//   - With "Debug: diagnostic log" on: a BepInEx log line when a run reaches 8 s (where the bot
//     looks, what the guard logics last turned its head toward, what it is doing, its post) and one
//     when the bot turns, moves, starts aiming at an enemy or stops being sampled. The head's steer
//     tells a deliberate stare (facing a contact report for up to 30 s) from a scan that stopped.
// Added in fork v7: in the user's build-6 raid (2026-11-05) a carry bot looked at 198 deg for 12 s
// while a Reshala guard walked behind it, and the user had been reporting views "glued to a wall".
//
// Owns one tracker per carry bot for the raid (_trackers, keyed by profile id); only Sample reads or
// writes it, and BrainMgr.OnRaidEnded calls Clear(). Reads and writes no files.

using System;
using System.Collections.Generic;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Frozen-view diagnostics; see the file header. Main thread only.</summary>
    internal static class FrozenViewWatch
    {
        /// <summary>Seconds between two error lines from this watch, so a fault cannot flood the log.</summary>
        private const float ERROR_LOG_INTERVAL = 30f;

        private static readonly Dictionary<string, FrozenViewTracker> _trackers = new();
        private static readonly FrozenViewSettings _settings = new();
        private static float _nextErrorLog;

        /// <summary>
        /// Samples one carry bot; see the file header for what it records and logs. Never throws, so a
        /// fault cannot stop RecordAgentInfo counting the other bots.
        /// </summary>
        /// <param name="data">The carry bot's MCS data.</param>
        /// <param name="layer">Its active brain layer, as the dump records it.</param>
        /// <param name="reason">Its active logic's reason, as the dump records it.</param>
        public static void Sample(McsBotPlayerData data, string layer, string reason)
        {
            try
            {
                var bot = data?.BotOwner;
                var player = data?.Player;
                if (bot == null || player?.HealthController == null)
                {
                    return;
                }
                var id = bot.ProfileId;
                if (!player.HealthController.IsAlive)
                {
                    _trackers.Remove(id);
                    return;
                }
                if (!_trackers.TryGetValue(id, out var tracker))
                {
                    tracker = new FrozenViewTracker();
                    _trackers[id] = tracker;
                }

                var look = player.LookDirection;
                var yaw = GuardFormation.YawOf(look.x, look.z);
                var enemy = SainBridge.GetSain(bot)?.GoalEnemy;
                var aiming = enemy != null && enemy.IsVisible;
                var position = player.Position;
                var result = tracker.Sample(Time.time, new V3(position.x, position.y, position.z), yaw, aiming, _settings);

                if (result == FrozenViewEvent.Started)
                {
                    MiyakoCarryServicePlugin.LogBuffer.AddFrozenViewReason(reason, (int)_settings.ReportAfterSeconds);
                }
                else if (tracker.Reported)
                {
                    MiyakoCarryServicePlugin.LogBuffer.AddFrozenViewReason(reason, 1);
                }

                if (ForkConfig.DebugLog?.Value != true)
                {
                    return;
                }
                if (result == FrozenViewEvent.Started)
                {
                    McsLogger.LogInfo($"[{ForkLog.Tag}] frozen view: {player.Profile?.Nickname} has looked at {tracker.RunYaw:0} deg for {tracker.RunSeconds:0} s, "
                        + $"{data.Fork.DescribeLook(Time.time)}; " + OpenGroundWatch.DescribeContext(data, player, layer, reason));
                }
                else if (result == FrozenViewEvent.Ended)
                {
                    var how = tracker.LastEnd switch
                    {
                        FrozenViewEnd.Moved => "moved",
                        FrozenViewEnd.Turned => "turned",
                        FrozenViewEnd.Exempt => "started aiming at an enemy",
                        _ => "stopped being sampled",
                    };
                    McsLogger.LogInfo($"[{ForkLog.Tag}] frozen view: {player.Profile?.Nickname} {how} after {tracker.RunSeconds:0} s looking at {tracker.RunYaw:0} deg (now {layer}, {reason})");
                }
            }
            catch (Exception e)
            {
                if (Time.time >= _nextErrorLog)
                {
                    _nextErrorLog = Time.time + ERROR_LOG_INTERVAL;
                    McsLogger.LogError($"[{ForkLog.Tag}] frozen-view watch failed: {e}");
                }
            }
        }

        /// <summary>Forgets every bot. Called at raid end.</summary>
        public static void Clear()
        {
            _trackers.Clear();
        }
    }
}
