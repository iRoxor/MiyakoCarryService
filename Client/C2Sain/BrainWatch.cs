// BrainWatch.cs -- diagnostics: each carry bot's brain over time.
//
// PlayerDataMgr.RecordBrainTimeline calls Sample for every carry bot four times a second with the
// bot's active brain layer and that layer's reason, read through BigBrain the way MCS's debug dump
// reads them. With "Debug: diagnostic log" on, it writes:
//   - a line whenever a bot's layer or reason changes:
//       "brain: <bot>: <old layer> (<old reason>) -> <layer> (<reason>); squad Guard, C2 stepped
//        aside (guarding); 2 m from you, facing 198; SAIN Search/None/None"
//     plus "-> <decision> [<why>]" when the fork's decision policy changed SAIN's decision;
//   - "death: <bot> died" when a carry bot dies.
// Neither SAIN (its decision-reason logging is compiled into debug builds only) nor BigBrain logs
// layer changes, and Raid Review records SAIN's decision but not the layer, so this is the only
// timeline of which brain drove a carry bot. Added in fork v7 at the user's request (2026-11-05).
// Samples four times a second, so a layer held for under a quarter second can be missed.
//
// Owns the last state per bot and the bots seen dead (keyed by profile id) for the raid; only Sample
// reads or writes them, and BrainMgr.OnRaidEnded calls Clear(). Reads and writes no files.

using System;
using System.Collections.Generic;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Brain-timeline diagnostics; see the file header. Main thread only.</summary>
    internal static class BrainWatch
    {
        /// <summary>Seconds between two error lines from this watch, so a fault cannot flood the log.</summary>
        private const float ERROR_LOG_INTERVAL = 30f;

        private static readonly Dictionary<string, string> _last = new();
        private static readonly HashSet<string> _dead = new();
        private static float _nextErrorLog;

        /// <summary>
        /// Samples one carry bot; see the file header for what it logs. Does nothing while "Debug:
        /// diagnostic log" is off. Never throws.
        /// </summary>
        /// <param name="data">The carry bot's MCS data.</param>
        /// <param name="layer">Its active brain layer, as BigBrain names it.</param>
        /// <param name="reason">Its active logic's reason.</param>
        public static void Sample(McsBotPlayerData data, string layer, string reason)
        {
            if (ForkConfig.DebugLog?.Value != true)
            {
                return;
            }
            try
            {
                var player = data?.Player;
                var bot = data?.BotOwner;
                if (player == null || bot == null || player.HealthController == null)
                {
                    return;
                }
                var id = bot.ProfileId;
                var name = player.Profile?.Nickname;
                if (!player.HealthController.IsAlive)
                {
                    if (_dead.Add(id))
                    {
                        McsLogger.LogInfo($"[{ForkLog.Tag}] death: {name} died");
                    }
                    return;
                }

                var state = $"{layer} ({reason})";
                _last.TryGetValue(id, out var before);
                if (before == state)
                {
                    return;
                }
                _last[id] = state;

                var fork = data.Fork;
                var lead = data.LeadPlayer;
                var mode = data.McsAILeadPlayer != null ? TeamModes.Get(data.McsAILeadPlayer)?.Mode.ToString() ?? "-" : "-";
                var look = player.LookDirection;
                var facing = GuardFormation.YawOf(look.x, look.z);
                var distance = lead != null ? $"{Vector3.Distance(player.Position, lead.Position):0} m from you" : "player unknown";
                var sain = string.IsNullOrEmpty(fork.RawDecision) ? "" : $"; SAIN {fork.RawDecision}"
                    + (fork.FinalDecision != fork.RawDecision ? $" -> {fork.FinalDecision} [{fork.VetoReason}]" : "");
                McsLogger.LogInfo($"[{ForkLog.Tag}] brain: {name}: {before ?? "start"} -> {state}; squad {mode}, "
                    + $"C2 {(fork.C2Active ? "in charge" : "stepped aside")} ({fork.C2Reason}); {distance}, facing {facing:0}{sain}");
            }
            catch (Exception e)
            {
                if (Time.time >= _nextErrorLog)
                {
                    _nextErrorLog = Time.time + ERROR_LOG_INTERVAL;
                    McsLogger.LogError($"[{ForkLog.Tag}] brain watch failed: {e}");
                }
            }
        }

        /// <summary>Forgets every bot. Called at raid end.</summary>
        public static void Clear()
        {
            _last.Clear();
            _dead.Clear();
        }
    }
}
