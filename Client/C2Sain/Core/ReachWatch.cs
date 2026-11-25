// ReachWatch.cs -- the rules and timers of the reach watch (fork v17): a carry bot that has slipped off the walkable ground, and a
// bot that cannot get to the player. The Unity side, which asks the navmesh and moves the bots, is C2Sain/ReachWatch.cs.
//
// What the Unity side does, once a second per bot, polled from McsBrainLayer.IsActive (so whatever layer has the bot):
//   1. it asks the navmesh whether the bot is stranded (no walkable point within 3 m of it, or no path from it to that
//      point; the 3 m is the Unity side's own constant) and, for a bot PathMinDistance or more from the player, whether it has a
//      complete path to the walkable point nearest the player; it fills a <see cref="ReachSample"/> and calls <see cref="ReachState.Sample"/>;
//   2. it carries out the <see cref="ReachAction"/> that comes back, through C2Sain/BotTeleport.cs, and writes the log line.
// This file holds only the decisions, so the logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive them
// with scripted samples. One ReachState per carry bot, held in C2Sain/BotForkState.cs; one PollGate for the whole plugin, held
// by C2Sain/ReachWatch.cs. Reads and writes no files.
//
// Why (the user's build 16 raids, 2026-11-24; evidence/2026-11-24-build-16-customs-factory/build-16-analysis.txt, 5b and 5c):
//   - a bot stranded off the navmesh after a fight (Factory night raid 3, "reach: ... no path from the bot") sat crouched in
//     SAIN's cover for 3 minutes until the user's Force Teleport; nothing in the fork moved it, and SAIN's mover had no path;
//   - on Factory 12 walks logged "no path to you" and three bots fell from 10-14 m to 38-41 m behind, their guard posts
//     unreachable, until the user teleported all four.
//
// Rules, in the order the samples are judged:
//   Skip      -- a bot that is dead, being healed, on a stationary weapon or riding the BTR is not judged at all, and its timers clear.
//   Stranded  -- the bot has been stranded in a row for StrandedSeconds. With walkable ground within 3 m it is moved
//                there (Snap), in any squad mode; with none, it is teleported to the player when the squad is not in a fight
//                (TeleportToPlayer); in a fight it tries again every RetrySeconds. The ReachSnap setting turns all of this off.
//   Catch-up  -- the bot is PathMinDistance or more from the player and has had no complete path to the player for
//                CatchUpSeconds (the FollowCatchUp setting, 0 = off), the squad is not in a fight (or the bot is on a Break off or
//                Regroup recall), it has no command of its own, the walkable point nearest the player is at least CatchUpGain closer
//                to the player than the bot is, and its last catch-up was CatchUpCooldown or more ago: teleported to that point.
//
// Provenance of the numbers: all of them are the plan's (miyako-carry-service/PLAN-BUILD-17.md, written 2026-11-24 from the
// raids above); none is measured yet. The falsifiers are in the ForkInfo v17 entry.

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>What the Unity side must do for a bot after a sample.</summary>
    public enum ReachAction
    {
        None,

        /// <summary>Move the bot to the walkable point within SnapRadius of it.</summary>
        Snap,

        /// <summary>No walkable ground near it: teleport it to the player (the walkable point nearest the player when the player is off the navmesh).</summary>
        TeleportToPlayer,

        /// <summary>It could not reach the player for CatchUpSeconds: teleport it to the walkable point nearest the player.</summary>
        CatchUp,
    }

    /// <summary>Tunables for <see cref="ReachState"/>; the plugin copies ReachSnap into <see cref="Snap"/> and FollowCatchUp into <see cref="CatchUpSeconds"/>.</summary>
    public sealed class ReachWatchSettings
    {
        /// <summary>Seconds between two checks of one bot.</summary>
        public float PollSeconds = 1f;

        /// <summary>Seconds a bot must be stranded in a row before it is moved.</summary>
        public float StrandedSeconds = 4f;

        /// <summary>Seconds between tries for a stranded bot that has no walkable ground near it while the squad is in a fight.</summary>
        public float RetrySeconds = 4f;

        /// <summary>The ReachSnap setting: false turns the stranded-bot rule off.</summary>
        public bool Snap = true;

        /// <summary>
        /// A bot closer to the player than this (m) is not checked for a path (it costs nothing), and its no-path clock is cleared,
        /// so the catch-up teleport can only happen to a bot at least this far away.
        /// </summary>
        public float PathMinDistance = 15f;

        /// <summary>The FollowCatchUp setting: seconds without a complete path to the player before the catch-up teleport; 0 = off.</summary>
        public float CatchUpSeconds = 10f;

        /// <summary>The catch-up's landing point must be at least this much (m) closer to the player than the bot is.</summary>
        public float CatchUpGain = 10f;

        /// <summary>Seconds between two catch-ups of one bot.</summary>
        public float CatchUpCooldown = 30f;

        /// <summary>True (always, in the plugin): a fight holds the catch-up back. False is a deliberately wrong value for the harness.</summary>
        public bool FightBlocksCatchUp = true;

        /// <summary>True (always, in the plugin): a Break off or Regroup recall lifts the fight hold, so a recalled bot that cannot get back is teleported. False is a deliberately wrong value for the harness.</summary>
        public bool RecallLiftsFightHold = true;
    }

    /// <summary>One check of one bot, as the Unity side fills it. A plain bundle of facts; the rules are in <see cref="ReachState.Sample"/>.</summary>
    public struct ReachSample
    {
        /// <summary>The bot is dead, being healed by MCS's heal logic, on a stationary weapon or riding the BTR: it is not judged.</summary>
        public bool Skip;

        /// <summary>No walkable point lies within 3 m of the bot, or NavMesh.CalculatePath from it to that point finds no path.</summary>
        public bool Stranded;

        /// <summary>A walkable point lies within 3 m of the bot (so a snap has somewhere to go).</summary>
        public bool WalkableNear;

        /// <summary>The squad's team mode is Combat.</summary>
        public bool InFight;

        /// <summary>The bot is on a Break off or Regroup recall (a Leash recall does not count).</summary>
        public bool OnRecall;

        /// <summary>The bot has a command of its own: Go To Point, Hold Position, an escort, a loot, quest or proxy task, Clear Area or a stationary weapon.</summary>
        public bool Busy;

        /// <summary>Metres between the bot and the player.</summary>
        public float BotToPlayer;

        /// <summary>The path check ran this sample (only for a bot PathMinDistance or more from the player).</summary>
        public bool PathChecked;

        /// <summary>The check found a complete path from the bot to the walkable point nearest the player.</summary>
        public bool PathComplete;

        /// <summary>A walkable point near the player was found (the catch-up's landing point).</summary>
        public bool HasDestination;

        /// <summary>Metres between that landing point and the player.</summary>
        public float DestinationToPlayer;
    }

    /// <summary>Per-bot reach watch timers; see the file header. Main-thread only.</summary>
    public sealed class ReachState
    {
        private float _nextPoll;
        private float _strandedSince = -1f;
        private float _nextStrandedTry = -1f;
        private float _noPathSince = -1f;
        private float _lastCatchUp = -1000f;

        /// <summary>
        /// Seconds of the condition that triggered the last action returned by <see cref="Sample"/> (how long the bot had been
        /// stranded, or without a path), for the log line. 0 after a sample that returned None.
        /// </summary>
        public float ActionSeconds { get; private set; }

        /// <summary>Seconds the bot has been stranded in a row at <paramref name="now"/>; 0 when it is not.</summary>
        public float StrandedFor(float now)
        {
            return _strandedSince >= 0f ? now - _strandedSince : 0f;
        }

        /// <summary>Seconds the bot has gone without a complete path to the player at <paramref name="now"/>; 0 when it has one (or was not checked).</summary>
        public float NoPathFor(float now)
        {
            return _noPathSince >= 0f ? now - _noPathSince : 0f;
        }

        /// <summary>True when this bot's next check is due (the first one is due at once).</summary>
        /// <param name="now">Game time in seconds.</param>
        public bool Due(float now)
        {
            return now >= _nextPoll;
        }

        /// <summary>Records that the bot was checked at <paramref name="now"/>; its next check is PollSeconds later.</summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="settings">Tunables.</param>
        public void MarkPolled(float now, ReachWatchSettings settings)
        {
            _nextPoll = now + settings.PollSeconds;
        }

        /// <summary>Forgets every timer (a new raid, or the bot's lead is gone).</summary>
        public void Reset()
        {
            _nextPoll = 0f;
            _strandedSince = -1f;
            _nextStrandedTry = -1f;
            _noPathSince = -1f;
            _lastCatchUp = -1000f;
            ActionSeconds = 0f;
        }

        /// <summary>
        /// Judges one check of the bot and says what to do. The stranded rule goes first; when it acts, the catch-up is not looked at.
        /// An action clears the timers it used (a bot that was moved is judged afresh).
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="sample">What the Unity side found.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        public ReachAction Sample(float now, in ReachSample sample, ReachWatchSettings settings)
        {
            ActionSeconds = 0f;
            if (sample.Skip)
            {
                _strandedSince = -1f;
                _nextStrandedTry = -1f;
                _noPathSince = -1f;
                return ReachAction.None;
            }

            if (sample.Stranded)
            {
                if (_strandedSince < 0f)
                {
                    _strandedSince = now;
                }
            }
            else
            {
                _strandedSince = -1f;
                _nextStrandedTry = -1f;
            }

            if (sample.BotToPlayer < settings.PathMinDistance)
            {
                _noPathSince = -1f;
            }
            else if (sample.PathChecked)
            {
                if (sample.PathComplete)
                {
                    _noPathSince = -1f;
                }
                else if (_noPathSince < 0f)
                {
                    _noPathSince = now;
                }
            }

            if (settings.Snap && _strandedSince >= 0f && now - _strandedSince >= settings.StrandedSeconds && now >= _nextStrandedTry)
            {
                if (sample.WalkableNear || !sample.InFight)
                {
                    ActionSeconds = now - _strandedSince;
                    _strandedSince = -1f;
                    _nextStrandedTry = -1f;
                    _noPathSince = -1f;
                    return sample.WalkableNear ? ReachAction.Snap : ReachAction.TeleportToPlayer;
                }
                _nextStrandedTry = now + settings.RetrySeconds;
            }

            var fightHolds = settings.FightBlocksCatchUp && sample.InFight && !(settings.RecallLiftsFightHold && sample.OnRecall);
            if (settings.CatchUpSeconds > 0f
                && _noPathSince >= 0f
                && now - _noPathSince >= settings.CatchUpSeconds
                && !fightHolds
                && !sample.Busy
                && sample.HasDestination
                && sample.BotToPlayer - sample.DestinationToPlayer >= settings.CatchUpGain
                && now - _lastCatchUp >= settings.CatchUpCooldown)
            {
                ActionSeconds = now - _noPathSince;
                _lastCatchUp = now;
                _noPathSince = -1f;
                return ReachAction.CatchUp;
            }
            return ReachAction.None;
        }
    }

    /// <summary>
    /// Lets one bot be checked per frame, so the checks of several bots never land in the same frame. The bot whose check is due
    /// asks for the frame; if another bot took it, it asks again next frame (its check stays due). Main-thread only.
    /// </summary>
    public sealed class PollGate
    {
        private int _frame = int.MinValue;

        /// <summary>True when the frame was free (and is now taken); false when another bot was checked in it.</summary>
        /// <param name="frame">Unity's Time.frameCount.</param>
        public bool TryEnter(int frame)
        {
            if (frame == _frame)
            {
                return false;
            }
            _frame = frame;
            return true;
        }
    }
}
