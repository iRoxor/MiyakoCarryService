// OpenGroundWatch.cs -- diagnostics: notices carry bots standing still in the open.
//
// Called about once a second per carry bot by PlayerDataMgr.RecordAgentInfo, the loop that counts
// brains, layers and reasons for MCS's debug dump. It changes nothing about how bots behave.
//
// A bot counts as in the open when nothing solid (walls, trunks, rocks, vehicles: the
// HighPolyCollider layer, terrain left out) is within OPEN_SOLID_RADIUS of it between knee and head
// height, and no bush (Foliage) is within OPEN_BUSH_RADIUS. It counts as still when it moved under
// 0.5 m since the previous sample; Core/OpenGroundTracker.cs keeps the runs.
//   - Always: each sample that finds a bot still in the open adds one to the debug dump's
//     "Still in the open" count under the reason its brain was running (LogBuffer, shown by
//     MenuTaskBarAwakePatch). A sample is about one second, so the counts are bot-seconds.
//   - With "Debug: diagnostic log" on: one BepInEx log line when a bot has stood still in the open
//     for 5 s (what it was doing, the squad's mode, its guard post and what cover that post was
//     planned with) and one when it moves, reaches cover or stops being sampled.
//
// Owns one tracker per carry bot for the raid (_trackers, keyed by profile id); only Sample reads
// or writes it, and BrainMgr.OnRaidEnded calls Clear(). Reads and writes no files.

using System;
using System.Collections.Generic;
using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Still-in-the-open diagnostics; see the file header. Main thread only.</summary>
    internal static class OpenGroundWatch
    {
        /// <summary>
        /// Solid cover closer than this (m) means the bot is not in the open. 1.5 m covers a bot
        /// crouched behind a trunk or rock, where the guard planner puts it (0.6 m past the object's
        /// bounds); farther away the object no longer shields a crouching body from most angles.
        /// </summary>
        private const float OPEN_SOLID_RADIUS = 1.5f;

        /// <summary>A bush closer than this (m) means the bot is not in the open.</summary>
        private const float OPEN_BUSH_RADIUS = 1f;

        /// <summary>Seconds between two error lines from this watch, so a fault cannot flood the log.</summary>
        private const float ERROR_LOG_INTERVAL = 30f;

        private static readonly Dictionary<string, OpenGroundTracker> _trackers = new();
        private static readonly OpenGroundSettings _settings = new();
        private static float _nextErrorLog;

        /// <summary>
        /// Samples one carry bot; see the file header for what it records and logs. Never throws:
        /// a fault here must not stop RecordAgentInfo counting the other bots.
        /// </summary>
        /// <param name="data">The carry bot's MCS data.</param>
        /// <param name="layer">Its active brain layer, as the dump records it.</param>
        /// <param name="reason">Its active logic's reason, as the dump records it.</param>
        public static void Sample(McsBotPlayerData data, string layer, string reason)
        {
            try
            {
                var botOwner = data?.BotOwner;
                var player = data?.Player;
                if (botOwner == null || player?.HealthController == null)
                {
                    return;
                }
                var id = botOwner.ProfileId;
                if (!player.HealthController.IsAlive)
                {
                    _trackers.Remove(id);
                    return;
                }
                if (!_trackers.TryGetValue(id, out var tracker))
                {
                    tracker = new OpenGroundTracker();
                    _trackers[id] = tracker;
                }

                var position = botOwner.Position;
                var inOpen = IsInOpen(position);
                var result = tracker.Sample(Time.time, new V3(position.x, position.y, position.z), inOpen, _settings);
                if (tracker.StillInOpen)
                {
                    MiyakoCarryServicePlugin.LogBuffer.AddOpenGroundReason(reason);
                }
                if (ForkConfig.DebugLog?.Value != true)
                {
                    return;
                }
                if (result == OpenGroundEvent.Started)
                {
                    McsLogger.LogInfo(DescribeStart(data, player, tracker.RunSeconds, layer, reason));
                }
                else if (result == OpenGroundEvent.Ended)
                {
                    var how = tracker.LastEnd switch
                    {
                        OpenGroundEnd.Moved => "moved",
                        OpenGroundEnd.Covered => "reached cover",
                        _ => "stopped being sampled",
                    };
                    McsLogger.LogInfo($"[{ForkLog.Tag}] open ground: {player.Profile?.Nickname} {how} after {tracker.RunSeconds:0} s still in the open (now {layer}, {reason})");
                }
            }
            catch (Exception e)
            {
                if (Time.time >= _nextErrorLog)
                {
                    _nextErrorLog = Time.time + ERROR_LOG_INTERVAL;
                    McsLogger.LogError($"[{ForkLog.Tag}] open-ground watch failed: {e}");
                }
            }
        }

        /// <summary>Forgets every bot. Called at raid end.</summary>
        public static void Clear()
        {
            _trackers.Clear();
        }

        /// <summary>
        /// True when no solid object (HighPolyCollider: walls, trunks, rocks, vehicles) reaches into a
        /// box OPEN_SOLID_RADIUS m either side of the bot between 0.5 m and 1.5 m up, and no bush is
        /// within OPEN_BUSH_RADIUS of its chest. Terrain is left out, so a hillside never counts as cover.
        /// </summary>
        private static bool IsInOpen(Vector3 position)
        {
            var halfExtents = new Vector3(OPEN_SOLID_RADIUS, 0.5f, OPEN_SOLID_RADIUS);
            if (Physics.CheckBox(position + Vector3.up, halfExtents, Quaternion.identity, LayersMaskController.HighPolyCollider, QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            return !Physics.CheckSphere(position + Vector3.up * 0.9f, OPEN_BUSH_RADIUS, LayersMaskController.Foliage, QueryTriggerInteraction.Collide);
        }

        /// <summary>The log line for a bot that has just reached 5 s still in the open.</summary>
        private static string DescribeStart(McsBotPlayerData data, Player player, float seconds, string layer, string reason)
        {
            return $"[{ForkLog.Tag}] open ground: {player.Profile?.Nickname} has stood still in the open for {seconds:0} s; "
                + $"{DescribeContext(data, player, layer, reason)}; nothing solid within {OPEN_SOLID_RADIUS:0.0} m, no bush within {OPEN_BUSH_RADIUS:0.0} m";
        }

        /// <summary>
        /// What a carry bot is doing, for diagnostic lines (here and FrozenViewWatch): "squad Guard, C2
        /// stepped aside (guarding); McsGuardLayer, C2Sain:Watch; post detail 6 m from you, planned
        /// without cover (nothing), bot 0.7 m from it; standing, 6 m from you". Casts the cover rays
        /// again when the bot has a guard post.
        /// </summary>
        internal static string DescribeContext(McsBotPlayerData data, Player player, string layer, string reason)
        {
            var fork = data.Fork;
            var lead = data.LeadPlayer;
            var team = data.McsAILeadPlayer != null ? TeamModes.Get(data.McsAILeadPlayer) : null;
            var position = player.Position;

            var post = "no guard post";
            if (!fork.C2Active && fork.HasGuardSpot)
            {
                var fromPlayer = lead != null ? Vector3.Distance(fork.GuardSpot, lead.Position).ToString("0") + " m from you" : "?";
                post = $"post {fork.GuardPostKind} {fromPlayer}, planned {(fork.GuardSpotCovered ? "with" : "without")} cover "
                    + $"({C2Nav.DescribeCover(fork.GuardSpot, fork.GuardOutward)}), bot {Vector3.Distance(position, fork.GuardSpot):0.0} m from it";
            }

            var pose = player.MovementContext != null && player.MovementContext.PoseLevel < 0.9f
                ? $"crouched ({player.MovementContext.PoseLevel:0.0})"
                : "standing";
            var toPlayer = lead != null ? $"{Vector3.Distance(position, lead.Position):0} m from you" : "player unknown";
            var squad = team != null ? team.Mode.ToString() : "-";
            return $"squad {squad}, C2 {(fork.C2Active ? "in charge" : "stepped aside")} ({fork.C2Reason}); {layer}, {reason}; {post}; {pose}, {toPlayer}";
        }
    }
}
