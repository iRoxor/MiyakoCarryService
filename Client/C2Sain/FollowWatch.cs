// FollowWatch.cs -- the follow code's Unity side: the shared tunables and helpers for Core/FollowTrail.cs, and the
// diagnostic lines for follow stalls and teleports. Since fork v15.
//
// Called by McsBrainLayer (GetNextAction), McsBaseLayer (EndFollowWalk, the MCS auto-teleport), GoToPointLogic (the
// speed rule) and CommandMgr.ForceTeleportCommandAction. Changes nothing about how a bot behaves except through
// the helpers, which only read.
//
// Writes, only while "Debug: diagnostic log" is on (setting McsFork/DebugLog), to the BepInEx log
// (BepInEx\LogOutput.log), each line opening with ForkLog.Tag (the raid clock):
//   follow stall: <bot> has stood still N s on its walk to you (<reason>); ...   once per stall, when the bot has
//        stood still 3 s while walking to its spot. It names the bot's distance to its spot and the spot's distance to
//        you, whether the bot counts as arrived, the navmesh path status from the bot to the spot, the mover's
//        state (path, moving, paused, sprinting, lying, blocked, state), the speed rule in force and your pose
//        and speed. v13's raid had 12 stands of 5-28 s in Mcs:TooFar 30-146 m from the player and nothing in the log
//        said why.
//   follow stall: <bot> ... (closing line)   once, when a reported stall ends: moved again, teleported, stopped,
//        interrupted; with its length, re-paths and give-ups.
//   teleport: MCS moved <bot> to you after it stood still N s, M m from you   each MCS auto-teleport
//   teleport: Force Teleport command: <bot> was M m from you; mover: ...   each Force Teleport command, so an analysis
//        can tell the two kinds apart (the user's own Force Teleports and MCS's 30 s one in the v13 raid).
// The format is read by .for-claude-to-reference/tools/mcs_follow_stats.py and mcs_fork_log_summary.py.
//
// Module state: none that lives across calls except the error-line throttle (_nextErrorLog); the per-bot stall
// state is in BotForkState.Follow.

using System;
using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Utils;
using UnityEngine;
using UnityEngine.AI;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>The follow code's shared tunables and helpers, and its diagnostic lines; see the file header. Main thread only.</summary>
    internal static class FollowWatch
    {
        /// <summary>Seconds between two error lines from this watch, so a fault cannot flood the log.</summary>
        private const float ERROR_LOG_INTERVAL = 30f;

        /// <summary>
        /// The follow tunables, the defaults of Core/FollowTrail.cs; the follow distance comes from its setting.
        /// Shared by every carry bot, read by McsBrainLayer, McsBaseLayer and GoToPointLogic.
        /// </summary>
        public static readonly FollowSettings Settings = new();

        private static float _nextErrorLog;

        /// <summary>True unless the setting "Follow: trail while you move" (McsFork/FollowTrail) is off: then the follow keeps v14's rules.</summary>
        public static bool TrailOn => ForkConfig.FollowTrail?.Value != false;

        /// <summary>The follow-distance setting (m), 8 until the settings are bound.</summary>
        public static float FollowDistance => ForkConfig.FollowDistance?.Value ?? 8f;

        /// <summary>A UnityEngine.Vector3 as the core's V3.</summary>
        /// <param name="v">The position.</param>
        public static V3 ToV3(Vector3 v) => new(v.x, v.y, v.z);

        /// <summary>The player's horizontal speed (m/s) from their velocity; 0 when the player is unknown.</summary>
        /// <param name="player">The lead player.</param>
        public static float LeadSpeed(Player player)
        {
            if (player == null)
            {
                return 0f;
            }
            var v = player.Velocity;
            return Mathf.Sqrt(v.x * v.x + v.z * v.z);
        }

        /// <summary>
        /// Writes the stall lines that are due for one bot: the stall report (once, at 3 s) and the closing line (once, when
        /// it ends). Reads and clears the bot's FollowTrail flags whether or not the diagnostic log is on. Never throws.
        /// </summary>
        /// <param name="data">The carry bot's MCS data.</param>
        /// <param name="bot">The bot.</param>
        /// <param name="reason">The running action's reason, "Mcs:TooFar" or "Mcs:TooClose".</param>
        /// <param name="target">The bot's walk target, if any.</param>
        /// <param name="arrived">BotGoToPointData.IsCome.</param>
        public static void Poll(McsBotPlayerData data, BotOwner bot, string reason, Vector3? target, bool arrived)
        {
            try
            {
                var follow = data.Fork.Follow;
                var report = follow.TakeStallReport(out var seconds);
                var ended = follow.TakeStallEnd(out var how, out var length, out var repaths, out var giveUps);
                if (ForkConfig.DebugLog?.Value != true)
                {
                    return;
                }
                if (report)
                {
                    McsLogger.LogInfo($"[{ForkLog.Tag}] follow stall: {Name(data)} has stood still {seconds:0} s on its walk to you ({reason}); "
                        + $"{DescribeWhere(data, bot, target, arrived)}; path to the spot: {DescribePath(bot, target)}; "
                        + $"mover: {DescribeMover(bot)}; speed rule: {DescribePace(data)}; you: {DescribePlayer(data.LeadPlayer)}; "
                        + $"{follow.Repaths} re-path(s) so far");
                }
                if (ended)
                {
                    McsLogger.LogInfo($"[{ForkLog.Tag}] follow stall: {Name(data)}'s stand ended after {length:0} s: {DescribeEnd(how)}; "
                        + $"{repaths} re-path(s), {giveUps} give-up(s); {Vector3.Distance(bot.Position, data.LeadPlayer != null ? data.LeadPlayer.Position : bot.Position):0} m from you now");
                }
            }
            catch (Exception e)
            {
                LogFault("follow stall", e);
            }
        }

        /// <summary>
        /// Logs MCS's own teleport of a bot that stood still for 30 s 20 m or more from the player, with "Debug: diagnostic
        /// log" on. The caller teleports; this only writes the line. Never throws.
        /// </summary>
        /// <param name="data">The carry bot's MCS data.</param>
        /// <param name="bot">The bot, before it is teleported.</param>
        /// <param name="activity">What the bot was doing: "following", "escorting" or "protecting".</param>
        /// <param name="stoodStill">Seconds since the bot last moved 2 m (MCS's stuck timer).</param>
        public static void LogAutoTeleport(McsBotPlayerData data, BotOwner bot, string activity, float stoodStill)
        {
            try
            {
                if (ForkConfig.DebugLog?.Value != true)
                {
                    return;
                }
                var distance = data.LeadPlayer != null ? Vector3.Distance(bot.Position, data.LeadPlayer.Position) : -1f;
                McsLogger.LogInfo($"[{ForkLog.Tag}] teleport: MCS moved {Name(data)} to you after it stood still {stoodStill:0} s, {distance:0} m from you "
                    + $"({activity}); mover: {DescribeMover(bot)}");
            }
            catch (Exception e)
            {
                LogFault("teleport", e);
            }
        }

        /// <summary>
        /// Logs one Force Teleport command (from the Positions menu, or the team command) with "Debug: diagnostic log" on:
        /// how far the bot was and what its mover was doing. The caller teleports afterwards. Never throws.
        /// </summary>
        /// <param name="data">The carry bot's MCS data.</param>
        /// <param name="bot">The bot, before it is teleported.</param>
        public static void LogForceTeleport(McsBotPlayerData data, BotOwner bot)
        {
            try
            {
                if (ForkConfig.DebugLog?.Value != true)
                {
                    return;
                }
                var distance = data.LeadPlayer != null ? Vector3.Distance(bot.Position, data.LeadPlayer.Position) : -1f;
                McsLogger.LogInfo($"[{ForkLog.Tag}] teleport: Force Teleport command: {Name(data)} was {distance:0} m from you; mover: {DescribeMover(bot)}");
            }
            catch (Exception e)
            {
                LogFault("teleport", e);
            }
        }

        private static string Name(McsBotPlayerData data) => data.Player?.Profile?.Nickname ?? "a carry bot";

        /// <summary>"146 m from you; 9.0 m from its spot, which is 2 m from you, not arrived" (or no spot).</summary>
        private static string DescribeWhere(McsBotPlayerData data, BotOwner bot, Vector3? target, bool arrived)
        {
            var lead = data.LeadPlayer;
            var fromYou = lead != null ? Vector3.Distance(bot.Position, lead.Position) : -1f;
            if (!target.HasValue)
            {
                return $"{fromYou:0} m from you; it has no spot to walk to";
            }
            var toSpot = Vector3.Distance(bot.Position, target.Value);
            var spotToYou = lead != null ? Vector3.Distance(target.Value, lead.Position) : -1f;
            return $"{fromYou:0} m from you; {toSpot:0.0} m from its spot, which is {spotToYou:0} m from you, {(arrived ? "counts as arrived" : "not arrived")}";
        }

        /// <summary>The navmesh path status from the bot to the spot: "complete, 12 m", "partial, ends 8 m short of the spot", "invalid".</summary>
        private static string DescribePath(BotOwner bot, Vector3? target)
        {
            if (!target.HasValue)
            {
                return "no spot";
            }
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(bot.Position, target.Value, NavMesh.AllAreas, path))
            {
                return "none found";
            }
            var corners = path.corners;
            switch (path.status)
            {
                case NavMeshPathStatus.PathComplete:
                    return $"complete, {C2Nav.PathLength(bot.Position, corners):0} m";
                case NavMeshPathStatus.PathPartial:
                    return corners.Length > 0 ? $"partial, ends {Vector3.Distance(corners[corners.Length - 1], target.Value):0} m short of the spot" : "partial, empty";
                default:
                    return "invalid";
            }
        }

        /// <summary>The bot's mover as the game reports it: path, moving, paused, sprinting, lying, blocked, state, speed, path age.</summary>
        private static string DescribeMover(BotOwner bot)
        {
            try
            {
                var m = bot.Mover;
                var now = Time.time;
                return $"path {YesNo(m.HasPathAndNoComplete)}, moving {YesNo(m.IsMoving)}, paused {YesNo(m.Pause)}"
                    + (m.Pause ? $" ({m.RemainPause:0.0} s left)" : "")
                    + $", sprinting {YesNo(m.Sprinting)}, lying {YesNo(bot.BotLay != null && bot.BotLay.IsLay)}, blocked {YesNo(m.Blocked)}, "
                    + $"sprint refused {YesNo(m.NoSprint)}, state {m.CurrentState}, target speed {m.DestMoveSpeed:0.0#}, "
                    + $"path set {now - m.LastPathSetTime:0.0} s ago, ended {now - m.LastEndPathTime:0.0} s ago, "
                    + $"mover's own target {(m.TargetPoint.HasValue ? $"{Vector3.Distance(bot.Position, m.TargetPoint.Value):0.0} m away" : "none")}";
            }
            catch (Exception e)
            {
                return $"unreadable ({e.GetType().Name})";
            }
        }

        /// <summary>The speed rule last chosen for the walk (Core FollowPace), in words; says so when none ran lately.</summary>
        private static string DescribePace(McsBotPlayerData data)
        {
            var follow = data.Fork.Follow;
            if (Time.time - follow.PaceTime > 1f)
            {
                return "none (no pace rule ran for this walk: the game's default pace)";
            }
            var distances = FollowTrail.DistancesFor(FollowDistance, Settings);
            switch (follow.Pace)
            {
                case FollowPace.CatchUp:
                    return $"catch-up sprint (full speed whatever your pose; on past {distances.Start:0} m until within {distances.Release:0.0} m)";
                case FollowPace.CopyProne:
                    return "copying you lying down (target speed 0, so standing is expected)";
                case FollowPace.CopySlow:
                    return "copying your pose and speed setting (you are crouched or have the walk speed turned down)";
                default:
                    return "your pace: you stand at the full speed setting, so the game's default (sprints when 5 m or more from its spot)";
            }
        }

        /// <summary>Your pose and speed as the movement context reports them, with your speed over the ground.</summary>
        private static string DescribePlayer(Player lead)
        {
            if (lead == null)
            {
                return "unknown";
            }
            try
            {
                var movement = lead.MovementContext;
                var pose = movement.IsInPronePose ? "prone" : movement.PoseLevel < 1f ? $"crouched ({movement.PoseLevel:0.00})" : "standing";
                var share = movement.MaxSpeed > 0f ? movement.CharacterMovementSpeed / movement.MaxSpeed : 0f;
                return $"{pose}, speed setting {share:0%} of your maximum, moving {LeadSpeed(lead):0.0} m/s";
            }
            catch (Exception e)
            {
                return $"unreadable ({e.GetType().Name})";
            }
        }

        private static string DescribeEnd(StallEnd how)
        {
            switch (how)
            {
                case StallEnd.Moved:
                    return "the bot moved again";
                case StallEnd.Teleported:
                    return "the bot was teleported";
                case StallEnd.Stopped:
                    return "the bot stopped, close to you with you slowed";
                default:
                    return "the follow ended (another layer took over, or no walk followed)";
            }
        }

        private static string YesNo(bool value) => value ? "yes" : "no";

        /// <summary>Writes one error line for a fault in this watch, at most one every 30 s.</summary>
        private static void LogFault(string what, Exception e)
        {
            if (Time.time >= _nextErrorLog)
            {
                _nextErrorLog = Time.time + ERROR_LOG_INTERVAL;
                McsLogger.LogError($"[{ForkLog.Tag}] {what} log failed: {e}");
            }
        }
    }
}
