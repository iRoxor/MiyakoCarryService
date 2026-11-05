// DefendShuffle.cs -- when a bot on Defend Me moves to a new spot near the player.
//
// One instance per carry bot, owned by BotForkState (C2Sain/BotForkState.cs) and ticked by
// TeamModes.GetDefendPost each time the Guard layer asks for the bot's Defend Me spot (about twice
// a second). Unity-free so the logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can
// drive it with scripted times and random numbers. Reads and writes no files.
//
// Every MinRollSeconds-MaxRollSeconds a bot standing at its spot rolls MoveChance, or SoloMoveChance
// when it is the only carry bot left alive. On success it moves to another spot in its sector
// (GuardPlanner.ShiftDefend picks it). A roll that falls due while the bot is still walking is
// skipped, so a bot only shifts from a spot it reached. The numbers are the user's design for Defend
// Me (plan of 2026-11-04): a bodyguard that keeps changing angle rather than freezing in place, and
// a lone bot that moves much more often because nobody else covers the other angles.

using System;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Tunables for <see cref="DefendShuffle"/>; the plugin uses the defaults.</summary>
    public sealed class DefendShuffleSettings
    {
        /// <summary>Shortest time between two rolls, in seconds.</summary>
        public float MinRollSeconds = 2f;

        /// <summary>Longest time between two rolls, in seconds.</summary>
        public float MaxRollSeconds = 3f;

        /// <summary>Chance per roll to move while two or more carry bots are alive.</summary>
        public float MoveChance = 0.25f;

        /// <summary>Chance per roll to move for the last carry bot alive.</summary>
        public float SoloMoveChance = 0.8f;
    }

    /// <summary>Per-bot Defend Me reposition timer. Main thread only.</summary>
    public sealed class DefendShuffle
    {
        private readonly Func<float> _random01;
        private float _nextRoll;
        private bool _started;

        /// <summary>Creates a timer.</summary>
        /// <param name="random01">Returns a random number in [0, 1); used for roll times and chances.</param>
        public DefendShuffle(Func<float> random01)
        {
            _random01 = random01;
        }

        /// <summary>Game time of the next roll; meaningful once <see cref="Tick"/> has run.</summary>
        public float NextRoll => _nextRoll;

        /// <summary>
        /// Advances the timer and says whether the bot should move to a new spot now. The first call
        /// only schedules the first roll. A due roll schedules the next one, then succeeds only when
        /// the bot is at its spot and the chance roll comes in under the chance for the squad's size.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="atSpot">True when the bot stands at its current spot.</param>
        /// <param name="livingBots">Carry bots alive in the squad, this one included.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        public bool Tick(float now, bool atSpot, int livingBots, DefendShuffleSettings settings)
        {
            if (!_started)
            {
                _started = true;
                _nextRoll = now + RollGap(settings);
                return false;
            }
            if (now < _nextRoll)
            {
                return false;
            }
            _nextRoll = now + RollGap(settings);
            if (!atSpot)
            {
                return false;
            }
            var chance = livingBots <= 1 ? settings.SoloMoveChance : settings.MoveChance;
            return _random01() < chance;
        }

        /// <summary>Forgets the schedule, so the next <see cref="Tick"/> starts a fresh one (a new Defend Me order).</summary>
        public void Reset()
        {
            _started = false;
        }

        /// <summary>A random gap between MinRollSeconds and MaxRollSeconds.</summary>
        private float RollGap(DefendShuffleSettings settings)
        {
            return settings.MinRollSeconds + (settings.MaxRollSeconds - settings.MinRollSeconds) * _random01();
        }
    }
}
