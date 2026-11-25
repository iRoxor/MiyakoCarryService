// C2Gate.cs -- decides, every brain tick, whether the C2 layer (McsBrainLayer)
// runs or steps aside for SAIN and the Guard layer. Also decides which brain
// layers survive MCS's layer stripping.
//
// Called from McsBrainLayer.IsActive and LayerUtils.McsRemoveNonKeepLayers; BeginRecall is called by the Break off
// and Regroup commands (CommandMgr). Writes BotForkState.C2Active / C2Reason. Since fork v17 it also runs the bot's
// recall (Core/BreakOff.cs): it keeps the C2 layer in charge while the bot is recalled, ends the recall on arrival or
// when the bot stops making progress (starting the chase leash's no-chase grace, RecallRules.OnEnd), cancels it when
// another command gives the bot a task, and begins a Leash recall for an Aggressive bot that has been beyond its leash
// range for 2 s (Core/LeashRules.cs LeashPull), whatever layer has the bot. While an Aggressive bot walks a flank leg,
// the Guard layer keeps it whatever the squad's mode (EngageTactics.OnFlankLeg; before fork v11 any running Engage
// order did). With "Debug: diagnostic log" on it writes the "recall:" lines. Reads and writes no files.
//
// Layer stack on a carry bot after injection:
//   200 C2 (McsBrainLayer)      -- this gate's verdict
//   85/80/22/20 SAIN layers     -- active when SAIN has a decision
//   80 AvoidDanger, 78 Malfunction (game layers SAIN bots keep)
//   19 Guard (McsGuardLayer)    -- the floor when nothing above is active
//
// Log lines (read by .for-claude-to-reference/tools/mcs_follow_stats.py and mcs_fork_log_summary.py; <bot> is the nickname):
//   recall: <bot> started (<Break off|Regroup|leash>), <N> m from you
//   recall: <bot> ended after <S> s: arrived <N> m from you[; no chasing or flanking for <G> s]
//   recall: <bot> ended after <S> s: gave up <N> m from you, <T> s without getting closer (best <B> m)[; no chasing or flanking for <G> s]
//   recall: <bot> cancelled after <S> s by <what>
// A Leash recall's end line has no grace clause.

using System;
using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>The C2-versus-SAIN decision for one bot.</summary>
    internal static class C2Gate
    {
        /// <summary>
        /// Intents that keep the C2 layer in charge outside combat: commands with a destination
        /// or a job, and Hold Position. In combat they wait, as upstream MCS deferred them. Since fork v17 the same
        /// list is "a command of its own": a bot that has one is never pulled back by the leash, a recall it was
        /// given is cancelled by it, and the reach watch's catch-up teleport leaves it alone (C2Sain/ReachWatch.cs).
        /// </summary>
        internal static readonly string[] C2TaskIntents =
        {
            Intents.ShouldTeleport,
            Intents.ShouldEscort,
            Intents.ShouldEscortToBtr,
            Intents.ShouldExfil,
            Intents.ShouldGoToPoint,
            Intents.ShouldHoldPosition,
            Intents.ShouldQuestProxyAction,
            Intents.ShouldLootProxyAction,
            Intents.ShouldInteractionProxyAction,
            Intents.ShouldStationaryWeaponProxyAction,
            Intents.ShouldDropTargetLoot,
            Intents.ShouldClearArea,
            Intents.ShouldUseStationaryWeapon,
        };

        /// <summary>The recall tunables, refreshed from the settings and the bot's stance before each use. Main thread only.</summary>
        private static readonly RecallSettings _recall = new();

        /// <summary>
        /// Whether a brain layer must survive MCS's stripping: every SAIN layer except Extract
        /// (bots would leave the raid on their own) and Debug, plus the game's AvoidDanger and
        /// Malfunction layers, which normal SAIN bots keep.
        /// </summary>
        /// <param name="layerName">The layer's Name() as the brain reports it.</param>
        public static bool ShouldKeepLayer(string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
            {
                return false;
            }
            if (layerName == "AvoidDanger" || layerName == "Malfunction")
            {
                return true;
            }
            return layerName.StartsWith("SAIN : ") && !layerName.Contains("Extract") && !layerName.Contains("Debug");
        }

        /// <summary>
        /// Whether the C2 layer should be the active layer for this bot right now.
        /// Side effects: runs the bot's recall (ends or cancels it, starting the no-chase grace; may begin a Leash
        /// recall), records the verdict and reason on the bot's fork state.
        /// </summary>
        /// <param name="botOwner">The carry bot.</param>
        /// <param name="data">Its MCS data; null keeps upstream behaviour (C2 always).</param>
        public static bool WantsC2(BotOwner botOwner, McsBotPlayerData data)
        {
            var result = Decide(botOwner, data, out var reason);
            if (data != null)
            {
                data.Fork.C2Active = result;
                data.Fork.C2Reason = reason;
            }
            return result;
        }

        /// <summary>
        /// Begins a recall: the bot runs back to the player under the C2 layer until it arrives or stops making progress
        /// (a recall that is running is restarted with the new kind). A running flank leg is dropped. Called by the Break
        /// off command (CommandMgr.FollowMeCommandAction), the Regroup command (RegroupCommandAction) and, with kind Leash,
        /// by <see cref="Decide"/>. Writes the bot's recall, leash-pull and flank state and, with the diagnostic log on, the
        /// "recall: ... started" line (and a "flank leg ended: Recall" line when a leg was dropped).
        /// </summary>
        /// <param name="botOwner">The carry bot.</param>
        /// <param name="data">Its MCS data.</param>
        /// <param name="kind">Why it is recalled.</param>
        public static void BeginRecall(BotOwner botOwner, McsBotPlayerData data, RecallKind kind)
        {
            var fork = data.Fork;
            var now = Time.time;
            var lead = data.LeadPlayer;
            var distance = lead != null ? Vector3.Distance(botOwner.Position, lead.Position) : 0f;
            fork.BreakOff.Begin(kind, now, distance);
            fork.Pull.Reset();
            if (fork.Engage.EndFlank(FlankEnd.Recall) && ForkConfig.DebugLog?.Value == true)
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] {botOwner.Profile?.Nickname}: flank leg ended: {FlankEnd.Recall}");
            }
            LogRecall(botOwner, $"started ({BreakOff.Label(kind)}), {distance:0} m from you");
        }

        /// <summary>The decision itself; see <see cref="WantsC2"/>.</summary>
        private static bool Decide(BotOwner botOwner, McsBotPlayerData data, out string reason)
        {
            if (data?.McsAILeadPlayer == null || data.LeadPlayer == null)
            {
                reason = "no lead";
                return true;
            }

            if (SainBridge.GetSain(botOwner) == null)
            {
                reason = "no SAIN";
                return true;
            }

            var fork = data.Fork;
            var now = Time.time;
            var distance = Vector3.Distance(botOwner.Position, data.LeadPlayer.Position);
            var rangeLimit = EngageTactics.RangeLimit(EngageTactics.CurrentStance(fork));
            var hasRange = rangeLimit > 0f && !float.IsPositiveInfinity(rangeLimit);
            var ownJob = data.HasAnyIntent(C2TaskIntents);

            if (fork.BreakOff.Active)
            {
                if (ownJob)
                {
                    // Another command gave the bot a task (Go To Point, Hold Position, an escort, a loot or proxy task, Clear Area, a
                    // Force Teleport...). The recall commands clear every other intent when they begin, so a task intent here is new.
                    CancelRecall(botOwner, fork, now, DescribeIntent(FirstTaskIntent(data)));
                }
                else if (fork.BreakOff.Kind == RecallKind.Leash && !hasRange)
                {
                    CancelRecall(botOwner, fork, now, "a stance change (no leash range any more)");
                }
                else
                {
                    RefreshRecall(rangeLimit);
                    var end = fork.BreakOff.Update(now, distance, _recall);
                    if (end == RecallEnd.None)
                    {
                        reason = "break off";
                        return true;
                    }
                    EndRecall(botOwner, fork, end, now, distance);
                }
            }

            // Fork v17: an Aggressive bot that has stayed beyond its leash range for 2 s is recalled, in whatever layer it is. A bot on
            // a command of its own is left alone (the user sent it there).
            if (fork.Pull.Update(now, distance, rangeLimit, !fork.BreakOff.Active && !ownJob, EngageTactics.LeashTunables))
            {
                BeginRecall(botOwner, data, RecallKind.Leash);
                reason = "break off";
                return true;
            }

            // Fork v11: only a flank leg holds C2 off whatever the squad's mode. Before v11 any running Engage order did; a stance is
            // permanent, so that would stop Aggressive bots ever following.
            if (EngageTactics.OnFlankLeg(botOwner, fork, now, distance))
            {
                reason = "flanking";
                return false;
            }

            var mode = TeamModes.Get(data.McsAILeadPlayer)?.Mode ?? TeamMode.C2;
            switch (mode)
            {
                case TeamMode.C2:
                    reason = "following";
                    return true;
                case TeamMode.Combat:
                    reason = "combat";
                    return false;
            }

            if (ownJob)
            {
                reason = "command";
                return true;
            }
            if (data.IsLooting)
            {
                reason = "looting";
                return true;
            }
            if (NeedsHeal(botOwner))
            {
                reason = "healing";
                return true;
            }

            reason = mode == TeamMode.Defend ? "defending" : "guarding";
            return false;
        }

        /// <summary>
        /// Copies the arrival radius and the give-up time from their settings, and the Leash recall's arrival radius from the
        /// stance's range, into the shared recall tunables.
        /// </summary>
        /// <param name="rangeLimit">The bot's leash range (m).</param>
        private static void RefreshRecall(float rangeLimit)
        {
            if (ForkConfig.BreakOffArriveRadius != null)
            {
                _recall.ArriveRadius = ForkConfig.BreakOffArriveRadius.Value;
                _recall.Timeout = ForkConfig.BreakOffTimeout.Value;
            }
            _recall.LeashArriveRadius = LeashRules.PullArriveRadius(rangeLimit, EngageTactics.ReturnShare);
        }

        /// <summary>
        /// Ends a recall that arrived or gave up: starts the no-chase grace (not after a Leash recall), clears the leash's return
        /// state only on arrival (Core/BreakOff.cs RecallRules.OnEnd), stops SAIN's search when a grace starts, and logs the end.
        /// </summary>
        /// <param name="botOwner">The carry bot.</param>
        /// <param name="fork">Its fork state.</param>
        /// <param name="end">Arrived or GaveUp.</param>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="distance">Metres from the bot to the player now.</param>
        private static void EndRecall(BotOwner botOwner, BotForkState fork, RecallEnd end, float now, float distance)
        {
            var recall = fork.BreakOff;
            var grace = RecallRules.OnEnd(end, recall.Kind, fork.Leash, now, ForkConfig.BreakOffGrace.Value, _recall);
            if (grace > 0f)
            {
                StopSainSearch(botOwner);
            }
            var graceText = grace > 0f ? $"; no chasing or flanking for {grace:0} s" : "";
            var elapsed = now - recall.StartedAt;
            LogRecall(botOwner, end == RecallEnd.Arrived
                ? $"ended after {elapsed:0.0} s: arrived {distance:0} m from you{graceText}"
                : $"ended after {elapsed:0.0} s: gave up {distance:0} m from you, {now - recall.ImprovedAt:0.0} s without getting closer (best {recall.BestDistance:0} m){graceText}");
        }

        /// <summary>Cancels the recall (another command or a stance change replaced it): no grace, the return state untouched; logs it.</summary>
        private static void CancelRecall(BotOwner botOwner, BotForkState fork, float now, string what)
        {
            var started = fork.BreakOff.StartedAt;
            if (fork.BreakOff.Cancel())
            {
                LogRecall(botOwner, $"cancelled after {now - started:0.0} s by {what}");
            }
        }

        /// <summary>Stops SAIN's search toward the bot's current enemy once (SainBridge.StopSearch), so the bot does not walk straight back out. Never throws.</summary>
        private static void StopSainSearch(BotOwner botOwner)
        {
            try
            {
                var sain = SainBridge.GetSain(botOwner);
                if (sain != null)
                {
                    SainBridge.StopSearch(sain, sain.GoalEnemy);
                }
            }
            catch (Exception e)
            {
                FollowWatch.Fault("stopping SAIN's search at the end of a recall", e);
            }
        }

        /// <summary>The first of the bot's task intents (<see cref="C2TaskIntents"/>) it has, or null. Allocates (HasIntent takes params), so only the rare cancel path calls it.</summary>
        private static string FirstTaskIntent(McsBotPlayerData data)
        {
            foreach (var intent in C2TaskIntents)
            {
                if (data.HasIntent(intent))
                {
                    return intent;
                }
            }
            return null;
        }

        /// <summary>An intent's name for the "cancelled by" line: "Mcs.ShouldGoToPoint" reads "GoToPoint".</summary>
        private static string DescribeIntent(string intent)
        {
            if (intent == null)
            {
                return "a command";
            }
            return intent.StartsWith("Mcs.Should") ? intent.Substring("Mcs.Should".Length) : intent;
        }

        /// <summary>Writes "recall: &lt;bot&gt; &lt;text&gt;" to the BepInEx log with the diagnostic log on.</summary>
        private static void LogRecall(BotOwner botOwner, string text)
        {
            if (ForkConfig.DebugLog?.Value == true)
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] recall: {botOwner.Profile?.Nickname} {text}");
            }
        }

        /// <summary>Same test upstream MCS uses before choosing its heal logic.</summary>
        private static bool NeedsHeal(BotOwner botOwner)
        {
            var medecine = botOwner.Medecine;
            if (medecine == null)
            {
                return false;
            }
            return (medecine.FirstAid.Damaged && medecine.FirstAid.HaveSmth2Use)
                || (medecine.SurgicalKit.Damaged && medecine.SurgicalKit.HaveSmth2Use);
        }
    }
}
