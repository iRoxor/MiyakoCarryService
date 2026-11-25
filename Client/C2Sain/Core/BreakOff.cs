// BreakOff.cs -- a recall: one carry bot ordered back to the player. Since fork v17 there are three kinds: the Break off
// command, the Regroup command and the Aggressive leash pulling a bot that strayed too far.
//
// The commands (CommandMgr.FollowMeCommandAction and RegroupCommandAction) and the leash pull (C2Sain/C2Gate.cs, with
// Core/LeashRules.cs LeashPull) begin it through C2Gate.BeginRecall. The C2 layer gate (C2Sain/C2Gate.cs) keeps the
// Command-and-Control brain in charge while it is active, so the bot runs back to the player instead of fighting, and
// calls Update every brain tick. Unity-free so the logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic)
// can drive it. Reads and writes no files.
//
// When a recall ends (fork v17):
//   - Arrived: the bot is within max(the arrival setting, ArriveFloor) of the player (a Leash recall: within its own
//     radius, RangeLimit x ReturnShare);
//   - GaveUp: the bot has not got ProgressMetres closer than its credited distance for Timeout seconds. The clock
//     restarts every time it does, so a bot that is making its way back is never cut off, however far away it is;
//   - cancelled: another command gave the bot a task (C2Gate cancels it; Cancel).
// Before v17 a recall ended after Timeout seconds whatever the distance. In the user's build 16 raid (2026-11-24) two
// recalls of an Aggressive bot 108 m and 158 m out each ended after about 20 s with the bot still 80-160 m away, and
// the leash's return state was cleared by the end (evidence/2026-11-24-build-16-customs-factory/build-16-analysis.txt).

using System;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Why a bot is being recalled. Append new kinds at the end: the names appear in log lines the tools read.</summary>
    public enum RecallKind
    {
        /// <summary>The Break off from combat command.</summary>
        BreakOff,

        /// <summary>The Regroup command (since fork v17 it recalls too; before it only set follow intents, which a fight ignored).</summary>
        Regroup,

        /// <summary>An Aggressive bot that stayed beyond the leash range for LeashRuleSettings.PullSeconds.</summary>
        Leash,
    }

    /// <summary>How a recall ended on one <see cref="BreakOff.Update"/> call.</summary>
    public enum RecallEnd
    {
        /// <summary>Still running (or none was running).</summary>
        None,

        /// <summary>The bot reached the arrival radius.</summary>
        Arrived,

        /// <summary>The bot made no progress for the timeout.</summary>
        GaveUp,
    }

    /// <summary>Tunables for <see cref="BreakOff"/>; the plugin copies the first two from its settings and the last from the stance's leash.</summary>
    public sealed class RecallSettings
    {
        /// <summary>The "Break off: arrival radius" setting (m). The recall counts as arrived within the larger of this and <see cref="ArriveFloor"/>.</summary>
        public float ArriveRadius = 3f;

        /// <summary>
        /// A Break off or Regroup recall arrives within this many metres at least. The recall walk's spot lies up to 4 m from the
        /// player (C2Nav.TryPickFollowSpot), so a 3 m test could not reliably be met.
        /// </summary>
        public float ArriveFloor = 5f;

        /// <summary>The "Break off: give up after" setting: seconds without getting closer before the recall gives up.</summary>
        public float Timeout = 20f;

        /// <summary>Metres closer than its credited distance a bot must get for that to count as progress.</summary>
        public float ProgressMetres = 2f;

        /// <summary>A Leash recall arrives within this many metres: the stance's range times its return share (set by the caller).</summary>
        public float LeashArriveRadius = 50f;

        /// <summary>False (always, in the plugin): a Leash recall starts no grace. True is a deliberately wrong value for the harness.</summary>
        public bool LeashGetsGrace;

        /// <summary>False (always, in the plugin): only an arrived recall clears the leash's return state. True (fork v16's rule) is a deliberately wrong value for the harness.</summary>
        public bool GiveUpClearsReturning;
    }

    /// <summary>Per-bot recall state. Main-thread only.</summary>
    public sealed class BreakOff
    {
        private float _credited;

        /// <summary>True while the bot is running back to the player.</summary>
        public bool Active { get; private set; }

        /// <summary>Why the current (or most recent) recall began.</summary>
        public RecallKind Kind { get; private set; }

        /// <summary>Game time the current (or most recent) recall started.</summary>
        public float StartedAt { get; private set; }

        /// <summary>Metres between the bot and the player when it started.</summary>
        public float StartDistance { get; private set; }

        /// <summary>The closest the bot has been to the player in this recall (m).</summary>
        public float BestDistance { get; private set; }

        /// <summary>Game time the bot last got <see cref="RecallSettings.ProgressMetres"/> closer (or the start).</summary>
        public float ImprovedAt { get; private set; }

        /// <summary>The log's name for a kind: "Break off", "Regroup" or "leash" (read by the tools).</summary>
        public static string Label(RecallKind kind)
        {
            return kind switch
            {
                RecallKind.BreakOff => "Break off",
                RecallKind.Regroup => "Regroup",
                _ => "leash",
            };
        }

        /// <summary>Starts a recall, or restarts one that is running with the new kind (the clocks and the best distance start over).</summary>
        /// <param name="kind">Why the bot is recalled.</param>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="distanceToPlayer">Metres between the bot and the player now.</param>
        public void Begin(RecallKind kind, float now, float distanceToPlayer)
        {
            Active = true;
            Kind = kind;
            StartedAt = now;
            StartDistance = distanceToPlayer;
            BestDistance = distanceToPlayer;
            _credited = distanceToPlayer;
            ImprovedAt = now;
        }

        /// <summary>Ends a recall without a grace period, because another command replaced it. Returns true when one was running.</summary>
        public bool Cancel()
        {
            var was = Active;
            Active = false;
            return was;
        }

        /// <summary>The metres within which a recall of this kind counts as arrived.</summary>
        /// <param name="kind">The recall's kind.</param>
        /// <param name="settings">Tunables.</param>
        public static float ArrivalRadius(RecallKind kind, RecallSettings settings)
        {
            return kind == RecallKind.Leash ? settings.LeashArriveRadius : Math.Max(settings.ArriveRadius, settings.ArriveFloor);
        }

        /// <summary>
        /// Checks whether the recall has finished. Progress is credited first (so a bot that has just got closer is not given
        /// up on in the same call), then arrival, then the timeout.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="distanceToPlayer">Metres between the bot and the player now.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        /// <returns>Arrived or GaveUp only on the call where it ends, so the caller starts the grace period once; None otherwise.</returns>
        public RecallEnd Update(float now, float distanceToPlayer, RecallSettings settings)
        {
            if (!Active)
            {
                return RecallEnd.None;
            }

            if (distanceToPlayer < BestDistance)
            {
                BestDistance = distanceToPlayer;
            }
            if (distanceToPlayer <= _credited - settings.ProgressMetres)
            {
                _credited = distanceToPlayer;
                ImprovedAt = now;
            }

            if (distanceToPlayer <= ArrivalRadius(Kind, settings))
            {
                Active = false;
                return RecallEnd.Arrived;
            }
            if (now - ImprovedAt >= settings.Timeout)
            {
                Active = false;
                return RecallEnd.GaveUp;
            }
            return RecallEnd.None;
        }
    }

    /// <summary>What a finished recall does to the chase leash. Pure functions, so the harness can drive the rules the gate applies.</summary>
    public static class RecallRules
    {
        /// <summary>
        /// Seconds of no chasing and no flanking after a recall of this kind: the "Break off: no-chase time" setting for a Break off or
        /// Regroup recall, none after a Leash recall (the bot is only being pulled back within its range).
        /// </summary>
        /// <param name="kind">The recall's kind.</param>
        /// <param name="graceSetting">The "Break off: no-chase time" setting (s).</param>
        /// <param name="settings">Tunables.</param>
        public static float GraceSeconds(RecallKind kind, float graceSetting, RecallSettings settings)
        {
            return kind == RecallKind.Leash && !settings.LeashGetsGrace ? 0f : graceSetting;
        }

        /// <summary>
        /// Applies a recall's end to the bot's chase leash: starts the grace (unless the kind has none) and, only when the bot
        /// ARRIVED, clears the leash's return state. A recall that gave up leaves it set, so the bot stays barred from chasing
        /// until it is back near the player (fork v16 cleared it on any end, timed out or not).
        /// </summary>
        /// <param name="end">How it ended (Arrived or GaveUp; None changes nothing).</param>
        /// <param name="kind">The recall's kind.</param>
        /// <param name="leash">The bot's chase leash.</param>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="graceSetting">The "Break off: no-chase time" setting (s).</param>
        /// <param name="settings">Tunables.</param>
        /// <returns>The grace seconds started (0 when none).</returns>
        public static float OnEnd(RecallEnd end, RecallKind kind, ChaseLeash leash, float now, float graceSetting, RecallSettings settings)
        {
            if (end == RecallEnd.None)
            {
                return 0f;
            }
            var grace = GraceSeconds(kind, graceSetting, settings);
            if (grace > 0f)
            {
                leash.StartGrace(now, grace);
            }
            if (end == RecallEnd.Arrived || settings.GiveUpClearsReturning)
            {
                leash.ClearReturning();
            }
            return grace;
        }
    }
}
