// OffMeshWatch.cs -- diagnostics: notices a player standing off the navmesh, where carry bots cannot
// path to them. Since fork v10.
//
// Called about once a second per carry bot by PlayerDataMgr.RecordAgentInfo, with that bot's player;
// a player sampled under 0.5 s ago is skipped, so a squad of three costs one sample per second. It
// changes nothing about how bots behave (the fallback itself is in McsBaseLayer.UpdateLeadNearMoveTarget
// and CommandMgr.ForceTeleportCommandAction).
//
// Logs, whether or not "Debug: diagnostic log" is on, one BepInEx line when the player has been off the
// navmesh for 3 s (how far the nearest walkable ground is) and one when they are back. These lines are
// the only record of why bots stopped following, and an episode is rare, so they are not gated.
//
// Owns one Core/OffMesh.cs OffMeshTracker per player for the raid (_trackers, keyed by profile id) and
// the time each was last sampled (_lastSample); only Sample reads or writes them, and BrainMgr.OnRaidEnded
// calls Clear(). Reads and writes no files.

using System;
using System.Collections.Generic;
using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Off-navmesh diagnostics; see the file header. Main thread only.</summary>
    internal static class OffMeshWatch
    {
        /// <summary>A player sampled more recently than this (s) is skipped, so one sample serves the whole squad.</summary>
        private const float MIN_SAMPLE_INTERVAL = 0.5f;

        /// <summary>Seconds between two error lines from this watch, so a fault cannot flood the log.</summary>
        private const float ERROR_LOG_INTERVAL = 30f;

        private static readonly Dictionary<string, OffMeshTracker> _trackers = new();
        private static readonly Dictionary<string, float> _lastSample = new();
        private static float _nextErrorLog;

        /// <summary>
        /// Samples one player; see the file header for what it logs. Never throws: a fault here must not
        /// stop RecordAgentInfo counting the bots.
        /// </summary>
        /// <param name="player">The carry bot's lead player.</param>
        public static void Sample(Player player)
        {
            try
            {
                if (player == null || player.HealthController == null || !player.HealthController.IsAlive)
                {
                    return;
                }
                var id = player.ProfileId;
                var now = Time.time;
                if (_lastSample.TryGetValue(id, out var last) && now - last < MIN_SAMPLE_INTERVAL)
                {
                    return;
                }
                _lastSample[id] = now;
                if (!_trackers.TryGetValue(id, out var tracker))
                {
                    tracker = new OffMeshTracker();
                    _trackers[id] = tracker;
                }

                var position = player.Position;
                var onMesh = C2Nav.IsOnNavMesh(position);
                var nearest = float.PositiveInfinity;
                if (!onMesh && C2Nav.TryNearestWalkable(position, out var walkable))
                {
                    nearest = Vector3.Distance(position, walkable);
                }

                switch (tracker.Sample(now, onMesh, nearest, C2Nav.OffMesh))
                {
                    case OffMeshEvent.WentOff:
                        McsLogger.LogInfo($"[{ForkLog.Tag}] off navmesh: {player.Profile?.Nickname} has been off the bots' walkable ground for "
                            + $"{tracker.Seconds:0} s at ({position.x:0.0}, {position.y:0.0}, {position.z:0.0}); {DescribeNearest(nearest)}");
                        break;
                    case OffMeshEvent.CameBack:
                        McsLogger.LogInfo($"[{ForkLog.Tag}] off navmesh: {player.Profile?.Nickname} is back on walkable ground after "
                            + $"{tracker.Seconds:0} s off it (farthest: {DescribeDistance(tracker.Farthest)})");
                        break;
                    case OffMeshEvent.Lost:
                        McsLogger.LogInfo($"[{ForkLog.Tag}] off navmesh: {player.Profile?.Nickname} stopped being sampled after {tracker.Seconds:0} s off walkable ground");
                        break;
                }
            }
            catch (Exception e)
            {
                if (Time.time >= _nextErrorLog)
                {
                    _nextErrorLog = Time.time + ERROR_LOG_INTERVAL;
                    McsLogger.LogError($"[{ForkLog.Tag}] off-navmesh watch failed: {e}");
                }
            }
        }

        /// <summary>
        /// The plan log's note when a guard or Defend plan came up short: "; you are off the navmesh
        /// (nearest walkable ground 12 m)" while the player is off it, otherwise "". One or more navmesh
        /// queries; called only with "Debug: diagnostic log" on.
        /// </summary>
        /// <param name="position">The player's position.</param>
        internal static string PlanNote(Vector3 position)
        {
            if (C2Nav.IsOnNavMesh(position))
            {
                return "";
            }
            var nearest = C2Nav.TryNearestWalkable(position, out var walkable) ? Vector3.Distance(position, walkable) : float.PositiveInfinity;
            return $"; you are off the navmesh (nearest walkable ground: {DescribeDistance(nearest)})";
        }

        /// <summary>The WentOff line's ending: how far walkable ground is and what the bots do about it.</summary>
        private static string DescribeNearest(float nearest)
        {
            return float.IsPositiveInfinity(nearest)
                ? $"no walkable ground within {C2Nav.OffMesh.SearchRadii[C2Nav.OffMesh.SearchRadii.Length - 1]:0} m, so the bots cannot come closer than they are"
                : $"nearest walkable ground {nearest:0} m away; the bots go to the closest spot they can reach";
        }

        /// <summary>"12 m", or "none within 32 m" for float.PositiveInfinity.</summary>
        private static string DescribeDistance(float distance)
        {
            return float.IsPositiveInfinity(distance)
                ? $"none within {C2Nav.OffMesh.SearchRadii[C2Nav.OffMesh.SearchRadii.Length - 1]:0} m"
                : $"{distance:0} m";
        }

        /// <summary>Forgets every player. Called at raid end.</summary>
        public static void Clear()
        {
            _trackers.Clear();
            _lastSample.Clear();
        }
    }
}
