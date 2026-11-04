// EscortPacer.cs -- the pace of an Escort: sprint bursts, watchful walking, waiting for the player.
//
// One instance per carry bot, owned by BotForkState (C2Sain/BotForkState.cs) and
// ticked every frame by MCS's EscortToPointByWayLogic while the bot leads the
// player to an escort destination. Unity-free so the logic harness
// (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it with scripted
// times and distances. Reads and writes no files.
//
// Paces, in the order a tick resolves them:
//   Wait   -- the player is behind the bot and more than WaitDistance away. The
//             bot stops and watches until the player is within ResumeDistance,
//             then walks on.
//   Sprint -- a burst of SprintSeconds; also used without a time limit to catch
//             up when the player is ahead of the bot and more than WaitDistance
//             away.
//   Walk   -- WalkMinSeconds to WalkMaxSeconds of walking while looking around,
//             after every burst.
// Upstream MCS sprinted at almost every distance (EscortToPointByWayLogic), so an
// escorting bot outran the player, especially on stamina stims.

using System;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>How an escorting bot should move right now.</summary>
    public enum EscortPace
    {
        Sprint,
        Walk,
        Wait,
    }

    /// <summary>Tunables for <see cref="EscortPacer"/>; the plugin copies them from its settings.</summary>
    public sealed class EscortPacerSettings
    {
        /// <summary>Seconds each sprint burst lasts.</summary>
        public float SprintSeconds = 3f;

        /// <summary>Shortest walk between bursts, in seconds.</summary>
        public float WalkMinSeconds = 3f;

        /// <summary>Longest walk between bursts, in seconds.</summary>
        public float WalkMaxSeconds = 5f;

        /// <summary>Metres the player may fall behind before the bot waits for them.</summary>
        public float WaitDistance = 20f;

        /// <summary>Metres the player must close to before a waiting bot moves on.</summary>
        public float ResumeDistance = 8f;
    }

    /// <summary>Per-bot escort pace state. Main thread only.</summary>
    public sealed class EscortPacer
    {
        private readonly Func<float> _random01;
        private float _phaseEnd;
        private float _lastTick;
        private bool _started;

        /// <summary>Creates a pacer.</summary>
        /// <param name="random01">Source of uniform random numbers in [0, 1); injectable for tests.</param>
        public EscortPacer(Func<float> random01)
        {
            _random01 = random01;
        }

        /// <summary>The pace decided by the most recent <see cref="Tick"/>.</summary>
        public EscortPace Pace { get; private set; } = EscortPace.Sprint;

        /// <summary>Game time the current timed phase (sprint burst or walk) ends.</summary>
        public float PhaseEnd => _phaseEnd;

        /// <summary>
        /// True when no escort has used the pacer for <paramref name="gapSeconds"/> (or ever), so the next
        /// one should start a fresh cycle. MCS restarts the escort logic at every intermediate point, and
        /// resetting on each restart would turn the cycle back into near-constant sprinting.
        /// </summary>
        public bool IsStale(float now, float gapSeconds)
        {
            return !_started || now - _lastTick > gapSeconds;
        }

        /// <summary>Restarts the cycle with a sprint burst. Called when a new escort begins.</summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="settings">Supplies SprintSeconds.</param>
        public void Reset(float now, EscortPacerSettings settings)
        {
            Pace = EscortPace.Sprint;
            _phaseEnd = now + settings.SprintSeconds;
            _lastTick = now;
            _started = true;
        }

        /// <summary>
        /// Decides the pace for this moment.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="playerDistance">Metres between the bot and the player.</param>
        /// <param name="playerBehind">True when the player is farther from the destination than the bot.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        /// <returns>The new <see cref="Pace"/>.</returns>
        public EscortPace Tick(float now, float playerDistance, bool playerBehind, EscortPacerSettings settings)
        {
            if (!_started)
            {
                Reset(now, settings);
            }
            _lastTick = now;

            if (Pace == EscortPace.Wait)
            {
                if (playerDistance > settings.ResumeDistance && playerBehind)
                {
                    return Pace;
                }
                StartWalk(now, settings);
                return Pace;
            }

            if (playerDistance > settings.WaitDistance)
            {
                if (playerBehind)
                {
                    Pace = EscortPace.Wait;
                    return Pace;
                }
                // The player is ahead: catch up without a time limit, and walk once caught up.
                Pace = EscortPace.Sprint;
                _phaseEnd = now + settings.SprintSeconds;
                return Pace;
            }

            if (now >= _phaseEnd)
            {
                if (Pace == EscortPace.Sprint)
                {
                    StartWalk(now, settings);
                }
                else
                {
                    Pace = EscortPace.Sprint;
                    _phaseEnd = now + settings.SprintSeconds;
                }
            }
            return Pace;
        }

        /// <summary>Starts a walk of random length between WalkMinSeconds and WalkMaxSeconds.</summary>
        private void StartWalk(float now, EscortPacerSettings settings)
        {
            Pace = EscortPace.Walk;
            var span = Math.Max(0f, settings.WalkMaxSeconds - settings.WalkMinSeconds);
            _phaseEnd = now + settings.WalkMinSeconds + span * _random01();
        }
    }
}
