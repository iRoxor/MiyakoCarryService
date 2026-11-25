// ReachWatch.cs -- the reach watch's Unity side (fork v17): finds carry bots that have slipped off the walkable ground and bots that
// cannot get to the player, and moves them. The rules and timers are Core/ReachWatch.cs (ReachState, ReachSample, ReachAction).
//
// Poll is called from McsBrainLayer.IsActive for every carry bot on every brain tick, whatever layer has the bot (the C2 layer is
// asked first, so this runs even while SAIN or the Guard layer is driving). A bot is checked once a second, and no two bots in the
// same frame (Core PollGate). A check asks the navmesh three things, all cheap next to a path search:
//   - stranded: no walkable point within 3 m of the bot, or no path from the bot to that point (the same test as "no path from the
//     bot" in C2Nav.FindClosestReachable) -- only with the ReachSnap setting on;
//   - for a bot 15 m or more from the player, and with FollowCatchUp above 0: whether it has a complete path to the walkable point
//     nearest the player (C2Nav.TryNearestWalkable, C2Nav.TryCompletePath). A bot within 15 m costs nothing.
// Then ReachState.Sample judges it. What comes back is carried out through BotTeleport: Snap (move to the walkable point within
// 3 m), TeleportToPlayer (no walkable ground near and no fight) and CatchUp (could not reach the player for FollowCatchUp seconds).
// Bots that are dead, being healed, on a stationary weapon or riding the BTR (or whose player is) are not judged.
// Writes: the bot's mover and position through BotTeleport, its fork state's Reach timers and MoveResetRequested (so the C2 layer
// forgets its old walk target), its GoToSomePointData. With "Debug: diagnostic log" on it writes, to the BepInEx log:
//   reach: <bot> was off the walkable ground for <N> s at (<x>, <y>, <z>); moved <M> m back onto it
//   reach: <bot> was off the walkable ground for <N> s at (<x>, <y>, <z>); teleported to you, <K> m away
//   teleport: catch-up: <bot> could not reach you for <N> s, <M> m away; landed <K> m from you
// (read by .for-claude-to-reference/tools/mcs_follow_stats.py and mcs_fork_log_summary.py). Timed by "Timing: reach watch" (CostKind.ReachWatch).
// Module state: the shared settings and the one-bot-per-frame gate, owned here; main thread only. Reads and writes no files.
//
// Why: see Core/ReachWatch.cs.

using System;
using EFT;
using EFT.Vehicle;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Utils;
using UnityEngine;
using UnityEngine.AI;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>The reach watch; see the file header. Main thread only.</summary>
    internal static class ReachWatch
    {
        /// <summary>Metres around a bot searched for walkable ground (a snap's reach). The plan's figure, not yet measured.</summary>
        private const float SNAP_RADIUS = 3f;

        /// <summary>The reach tunables: the defaults of Core/ReachWatch.cs, with ReachSnap and FollowCatchUp copied in from their settings on each check.</summary>
        private static readonly ReachWatchSettings _settings = new();

        /// <summary>Lets one bot be checked per frame.</summary>
        private static readonly PollGate _gate = new();

        /// <summary>Reused for the stranded test so a check allocates no path.</summary>
        private static NavMeshPath _path;

        /// <summary>
        /// Checks this bot if its turn has come (once a second, and only if no other bot was checked this frame). Never throws:
        /// a fault is logged (at most every 30 s) and the bot carries on.
        /// </summary>
        /// <param name="botOwner">The carry bot.</param>
        /// <param name="data">Its MCS data.</param>
        public static void Poll(BotOwner botOwner, McsBotPlayerData data)
        {
            try
            {
                var fork = data.Fork;
                var now = Time.time;
                if (!fork.Reach.Due(now) || !_gate.TryEnter(Time.frameCount))
                {
                    return;
                }
                fork.Reach.MarkPolled(now, _settings);
                _settings.Snap = ForkConfig.ReachSnap?.Value != false;
                _settings.CatchUpSeconds = ForkConfig.FollowCatchUp?.Value ?? _settings.CatchUpSeconds;
                if (!_settings.Snap && _settings.CatchUpSeconds <= 0f)
                {
                    return;
                }
                using (ForkCost.Begin(CostKind.ReachWatch))
                {
                    Check(botOwner, data, fork, now);
                }
            }
            catch (Exception e)
            {
                FollowWatch.Fault("the reach watch", e);
            }
        }

        /// <summary>One check of one bot: fills a sample from the navmesh and the bot's state, judges it, carries out the action.</summary>
        private static void Check(BotOwner botOwner, McsBotPlayerData data, BotForkState fork, float now)
        {
            var lead = data.LeadPlayer;
            var botPlayer = botOwner.GetPlayer;
            var sample = new ReachSample();
            if (lead == null || botPlayer == null || lead.HealthController == null || !lead.HealthController.IsAlive || Skip(botOwner, data, botPlayer, lead))
            {
                sample.Skip = true;
                fork.Reach.Sample(now, sample, _settings);
                return;
            }

            var botPos = botOwner.Position;
            var leadPos = lead.Position;
            sample.BotToPlayer = Vector3.Distance(botPos, leadPos);
            var snapTarget = botPos;

            if (_settings.Snap)
            {
                if (NavMesh.SamplePosition(botPos, out var hit, SNAP_RADIUS, NavMesh.AllAreas))
                {
                    sample.WalkableNear = true;
                    snapTarget = hit.position;
                    _path ??= new NavMeshPath();
                    sample.Stranded = !NavMesh.CalculatePath(botPos, hit.position, NavMesh.AllAreas, _path) || _path.status == NavMeshPathStatus.PathInvalid;
                }
                else
                {
                    sample.Stranded = true;
                }
            }

            if (_settings.CatchUpSeconds > 0f && sample.BotToPlayer >= _settings.PathMinDistance)
            {
                sample.PathChecked = true;
                if (C2Nav.TryNearestWalkable(leadPos, out var destination))
                {
                    sample.HasDestination = true;
                    sample.DestinationToPlayer = Vector3.Distance(destination, leadPos);
                    sample.PathComplete = C2Nav.TryCompletePath(botPos, destination, out _);
                }
            }

            sample.InFight = TeamModes.Get(data.McsAILeadPlayer)?.Mode == TeamMode.Combat;
            sample.OnRecall = fork.BreakOff.Active && fork.BreakOff.Kind != RecallKind.Leash;
            sample.Busy = data.HasAnyIntent(C2Gate.C2TaskIntents);

            var action = fork.Reach.Sample(now, sample, _settings);
            if (action == ReachAction.None)
            {
                return;
            }

            var name = botPlayer.Profile?.Nickname;
            var seconds = fork.Reach.ActionSeconds;
            Vector3 landed;
            switch (action)
            {
                case ReachAction.Snap:
                    landed = BotTeleport.Move(botOwner, data, snapTarget, now, false);
                    AfterMove(botOwner, data, landed);
                    Log($"reach: {name} was off the walkable ground for {seconds:0} s at {FollowWatch.Place(botPos)}; moved {Vector3.Distance(botPos, landed):0.0} m back onto it");
                    break;
                case ReachAction.TeleportToPlayer:
                    landed = BotTeleport.Move(botOwner, data, BotTeleport.LandingNear(leadPos, name), now, false);
                    AfterMove(botOwner, data, landed);
                    Log($"reach: {name} was off the walkable ground for {seconds:0} s at {FollowWatch.Place(botPos)}; teleported to you, {sample.BotToPlayer:0} m away");
                    break;
                default:
                    landed = BotTeleport.Move(botOwner, data, BotTeleport.LandingNear(leadPos, name), now, false);
                    AfterMove(botOwner, data, landed);
                    Log($"teleport: catch-up: {name} could not reach you for {seconds:0} s, {sample.BotToPlayer:0} m away; landed {Vector3.Distance(landed, leadPos):0} m from you");
                    break;
            }
        }

        /// <summary>
        /// A bot that is not judged: dead, being healed (the game's "using meds"), holding a stationary weapon, riding the BTR or
        /// with its player in it (a fast ride is not a stranded or unreachable bot).
        /// </summary>
        private static bool Skip(BotOwner botOwner, McsBotPlayerData data, Player botPlayer, Player lead)
        {
            var health = botPlayer.HealthController;
            if (botOwner.IsDead || health == null || !health.IsAlive)
            {
                return true;
            }
            if (botOwner.Medecine != null && botOwner.Medecine.Using)
            {
                return true;
            }
            var stationary = botOwner.WeaponManager?.Stationary;
            if ((stationary != null && stationary.Taken) || data.HasAnyIntent(Intents.ShouldUseStationaryWeapon, Intents.ShouldStationaryWeaponProxyAction))
            {
                return true;
            }
            return InBtr(botPlayer) || InBtr(lead);
        }

        /// <summary>True while the player is getting into, sitting in or getting out of the BTR.</summary>
        private static bool InBtr(Player player)
        {
            var state = player.BtrState;
            return state == EPlayerBtrState.Inside || state == EPlayerBtrState.GoIn || state == EPlayerBtrState.GoOut;
        }

        /// <summary>
        /// What a teleport out of the reach watch leaves to do (the commands' teleports do it themselves): points the bot's own walk
        /// target where it stands, so it does not turn back to the spot it was walking to, and asks the C2 layer to forget its old
        /// walk target at its next decision (the same flag every command sets).
        /// </summary>
        private static void AfterMove(BotOwner botOwner, McsBotPlayerData data, Vector3 landed)
        {
            botOwner.GoToSomePointData.SetPoint(landed);
            data.MoveResetRequested = true;
        }

        /// <summary>Writes one line to the BepInEx log with the diagnostic log on.</summary>
        private static void Log(string text)
        {
            if (ForkConfig.DebugLog?.Value == true)
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] {text}");
            }
        }
    }
}
