// ChaseLeash.cs -- the time-based limit on how long a carry bot may chase.
//
// One instance per carry bot, held in C2Sain/BotForkState.cs. DecisionPolicy.cs
// asks it about every SAIN decision (about ten times a second) whether a chase
// decision may stand. Unity-free so the logic harness can test it. Reads and
// writes no files.
//
// Rules (the user's spec, 2026-11-04):
//   - A chase may run for a random 3-5 s (ChaseMinSeconds..ChaseMaxSeconds).
//   - If another enemy becomes known during a chase, the chase ends at once.
//   - Once a chase is cut, chases stay vetoed until the bot is back within
//     ReturnRadius of the player.
//   - A grace period (after Break off reaches the player) vetoes chases too.
// Since fork v11 the leash depends on the bot's stance (Core/Stance.cs, StanceRules.ApplyLeash):
//   - Passive and Defensive keep the timed rules above (RangeLimit 0).
//   - Aggressive (RangeLimit > 0): chasing is free within RangeLimit metres of the player; past it the
//     chase is cut and chases stay vetoed until the bot is back within ReturnShare of the range.
//   - Hyper-aggressive (RangeLimit infinite): no limit.
//   The grace period applies in every stance.

using System;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Tunables for <see cref="ChaseLeash"/>, refreshed from BepInEx settings by the plugin.</summary>
    public sealed class ChaseLeashSettings
    {
        /// <summary>Shortest chase allowed, in seconds.</summary>
        public float ChaseMinSeconds = 3f;

        /// <summary>Longest chase allowed, in seconds.</summary>
        public float ChaseMaxSeconds = 5f;

        /// <summary>A cut-off chase stays vetoed until the bot is this close (metres) to the player.</summary>
        public float ReturnRadius = 6f;

        /// <summary>
        /// Fork v11: 0 = the timed leash; above 0 = chase freely within this many metres of the player
        /// (Aggressive); float.PositiveInfinity = no leash (Hyper-aggressive). Set per bot from its stance.
        /// </summary>
        public float RangeLimit;

        /// <summary>Fork v11, range leash: a cut chase stays vetoed until the bot is within this share of RangeLimit.</summary>
        public float ReturnShare = 0.5f;
    }

    /// <summary>Per-bot chase timer. Main-thread only.</summary>
    public sealed class ChaseLeash
    {
        private readonly Func<float> _random01;
        private float _chaseStart = -1f;
        private float _chaseLimit;
        private int _enemiesAtStart;
        private float _graceUntil = float.NegativeInfinity;

        /// <summary>
        /// Creates a leash.
        /// </summary>
        /// <param name="random01">Source of numbers in [0, 1) used to pick each chase's length;
        /// the harness passes a fixed sequence, the plugin passes UnityEngine.Random.</param>
        public ChaseLeash(Func<float> random01)
        {
            _random01 = random01;
        }

        /// <summary>True after a chase was cut and the bot has not yet come back to the player.</summary>
        public bool Returning { get; private set; }

        /// <summary>True while a chase is being timed.</summary>
        public bool Chasing => _chaseStart >= 0f;

        /// <summary>Length (seconds) picked for the current chase; 0 when not chasing.</summary>
        public float ChaseLimit => Chasing ? _chaseLimit : 0f;

        /// <summary>Seconds the current chase has run; 0 when not chasing.</summary>
        public float ChaseElapsed(float now)
        {
            return Chasing ? now - _chaseStart : 0f;
        }

        /// <summary>Seconds of grace left at <paramref name="now"/>; 0 when none.</summary>
        public float GraceLeft(float now)
        {
            return now < _graceUntil ? _graceUntil - now : 0f;
        }

        /// <summary>
        /// Starts a no-chase grace period and forgets any chase in progress.
        /// Called when Break off from combat brings the bot back to the player.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="seconds">Length of the grace period.</param>
        public void StartGrace(float now, float seconds)
        {
            _graceUntil = now + seconds;
            _chaseStart = -1f;
            Returning = false;
        }

        /// <summary>Forgets all state: chase, return and grace.</summary>
        public void Reset()
        {
            _chaseStart = -1f;
            Returning = false;
            _graceUntil = float.NegativeInfinity;
        }

        /// <summary>
        /// Decides whether a decision SAIN just made must be vetoed. Updates the
        /// chase timer as a side effect.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="isChase">True if the decision moves the bot toward an enemy.</param>
        /// <param name="knownEnemies">Number of enemies the bot currently knows about.</param>
        /// <param name="distanceToPlayer">Metres between the bot and the player.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        /// <returns>True when the chase decision must be replaced.</returns>
        public bool ShouldVeto(float now, bool isChase, int knownEnemies, float distanceToPlayer, ChaseLeashSettings settings)
        {
            if (settings.RangeLimit > 0f)
            {
                return RangeVeto(now, isChase, distanceToPlayer, settings);
            }

            if (Returning && distanceToPlayer <= settings.ReturnRadius)
            {
                Returning = false;
            }

            if (!isChase)
            {
                _chaseStart = -1f;
                return false;
            }

            if (now < _graceUntil || Returning)
            {
                return true;
            }

            if (_chaseStart < 0f)
            {
                _chaseStart = now;
                var span = Math.Max(0f, settings.ChaseMaxSeconds - settings.ChaseMinSeconds);
                _chaseLimit = settings.ChaseMinSeconds + span * _random01();
                _enemiesAtStart = knownEnemies;
                return false;
            }

            if (now - _chaseStart > _chaseLimit || knownEnemies > _enemiesAtStart)
            {
                _chaseStart = -1f;
                Returning = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// The range leash (fork v11, Aggressive and Hyper-aggressive): a chase is cut once the bot is
        /// more than RangeLimit metres from the player, and chases stay vetoed until it is back within
        /// ReturnShare of the range. No chase timer runs. The Break off grace still vetoes.
        /// </summary>
        private bool RangeVeto(float now, bool isChase, float distanceToPlayer, ChaseLeashSettings settings)
        {
            _chaseStart = -1f;
            if (Returning && distanceToPlayer <= settings.RangeLimit * settings.ReturnShare)
            {
                Returning = false;
            }
            if (!isChase)
            {
                return false;
            }
            if (now < _graceUntil || Returning)
            {
                return true;
            }
            if (distanceToPlayer > settings.RangeLimit)
            {
                Returning = true;
                return true;
            }
            return false;
        }
    }
}
