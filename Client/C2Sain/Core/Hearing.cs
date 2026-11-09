// Hearing.cs -- the Unity-free rules behind the fork's hearing (fork v8).
//
// Used by C2Sain/FootstepFeed.cs (when an AI enemy's footsteps are handed to carry bots, and how far
// they carry), C2Sain/Patches/SainHearingPatches.cs (the gunshot range), C2Sain/HeardWatch.cs (when a
// heard enemy may alert the squad or make a bot say "I hear something.") and TeamModes (which guards
// turn toward a threat). Unity-free so the logic harness
// (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it with scripted times, speeds and
// angles. Reads and writes no files.
//
// Why: SAIN creates footstep sounds only for human players (SAIN/Classes/Player/
// PlayerSoundController.cs returns early for AI), so no carry bot could hear an AI enemy walk. The
// fork makes those steps itself and hands them to carry bots only, so other bots hear nothing new.
// SAIN still decides, per bot, whether a step is heard (range, walls, headphones, chance).

using System;
using System.Collections.Generic;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>What kind of step a moving player makes.</summary>
    public enum StepKind
    {
        None,
        Walk,
        Sprint,
    }

    /// <summary>Tunables for <see cref="FootstepCadence"/>; the plugin uses the defaults.</summary>
    public sealed class FootstepSettings
    {
        /// <summary>Slowest speed (m/s) that makes footsteps; slower creeping is silent.</summary>
        public float MinSpeed = 0.5f;

        /// <summary>Seconds between two steps while walking or running.</summary>
        public float WalkInterval = 0.55f;

        /// <summary>Seconds between two steps while sprinting.</summary>
        public float SprintInterval = 0.35f;

        /// <summary>A gap between samples longer than this (s) restarts the step timer.</summary>
        public float MaxSampleGap = 2f;
    }

    /// <summary>
    /// Per-player step counter: a moving player makes one step per WalkInterval (SprintInterval while
    /// sprinting) seconds on average. Steps owed carry over between samples, so a 4 Hz sampler still
    /// gives a sprinter its faster rate (a plain "next step at" timer rounded every step up to the next
    /// sample: 0.5 s instead of 0.35). The first moving sample after a stop steps at once. One instance
    /// per enemy player, owned by FootstepFeed. Main thread only.
    /// </summary>
    public sealed class FootstepCadence
    {
        /// <summary>Most steps that can be owed, so a long sample gap cannot release a burst.</summary>
        private const float MAX_OWED = 1.5f;

        private bool _hasLast;
        private float _lastTime;
        private V3 _lastPosition;
        private float _owed = 1f;

        /// <summary>Speed (m/s) measured at the last sample.</summary>
        public float Speed { get; private set; }

        /// <summary>
        /// Takes one sample and says whether the player made a step since the last one, and what kind.
        /// The first sample (and the first after a gap) only records the position.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="position">The player's position.</param>
        /// <param name="sprinting">True when the game says the player is sprinting.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        public StepKind Sample(float now, V3 position, bool sprinting, FootstepSettings settings)
        {
            var dt = now - _lastTime;
            var continuous = _hasLast && dt > 0f && dt <= settings.MaxSampleGap;
            Speed = continuous ? V3.Distance(position, _lastPosition) / dt : 0f;
            _hasLast = true;
            _lastTime = now;
            _lastPosition = position;
            if (!continuous || Speed < settings.MinSpeed)
            {
                _owed = 1f;
                return StepKind.None;
            }
            var interval = Math.Max(0.05f, sprinting ? settings.SprintInterval : settings.WalkInterval);
            _owed = Math.Min(_owed + dt / interval, MAX_OWED);
            if (_owed < 1f)
            {
                return StepKind.None;
            }
            _owed -= 1f;
            return sprinting ? StepKind.Sprint : StepKind.Walk;
        }
    }

    /// <summary>Tunables for <see cref="HearingFilter"/>; the plugin copies its BepInEx settings in.</summary>
    public sealed class HearingFilterSettings
    {
        /// <summary>Gunshots farther than this (m) are not heard by carry bots; 0 = no limit.</summary>
        public float GunshotRange = 25f;

        /// <summary>While the squad follows the player (C2), footsteps farther than this (m) are not handed over; 0 = no limit.</summary>
        public float FollowFootstepRange = 30f;
    }

    /// <summary>The distance limits on what carry bots hear.</summary>
    public static class HearingFilter
    {
        /// <summary>True when a carry bot may hear a gunshot this far away.</summary>
        public static bool HearsGunshot(float distance, HearingFilterSettings settings)
        {
            return settings.GunshotRange <= 0f || distance <= settings.GunshotRange;
        }

        /// <summary>
        /// True when a footstep this far away is handed to a carry bot: within the sound's own range
        /// (SAIN's base range times the step's volume), and, while the squad follows the player,
        /// within FollowFootstepRange.
        /// </summary>
        /// <param name="distance">Metres from the bot to the stepping player.</param>
        /// <param name="soundRange">The sound's range with its volume applied (m).</param>
        /// <param name="following">True while the squad is in C2 (following the player).</param>
        /// <param name="settings">Tunables.</param>
        public static bool HandsOverFootstep(float distance, float soundRange, bool following, HearingFilterSettings settings)
        {
            if (distance > soundRange)
            {
                return false;
            }
            return !following || settings.FollowFootstepRange <= 0f || distance <= settings.FollowFootstepRange;
        }
    }

    /// <summary>
    /// Which guards turn toward a threat. Turning the whole squad toward one point left every back
    /// open in the build-7 raids (2026-11-05 Woods: up to 28 s, 301 s in all), so only the guards
    /// whose sector covers the threat face it; the others keep watching their own sectors.
    /// </summary>
    public static class ThreatSectors
    {
        /// <summary>Degrees either side of a guard's watch direction that count as its sector.</summary>
        public const float DEFAULT_HALF_WIDTH = 80f;

        /// <summary>
        /// True when guard <paramref name="me"/> should face the threat: its sector covers the threat,
        /// or no guard's sector does and its own watch direction is the closest to it, so somebody
        /// always faces a threat.
        /// </summary>
        /// <param name="offsets">Per guard, degrees (0-180) between its watch direction and the direction to the threat.</param>
        /// <param name="me">This guard's index in offsets.</param>
        /// <param name="halfWidth">Degrees either side of the watch direction that the sector covers.</param>
        public static bool ShouldFace(IReadOnlyList<float> offsets, int me, float halfWidth)
        {
            if (me < 0 || me >= offsets.Count)
            {
                return false;
            }
            if (offsets[me] <= halfWidth)
            {
                return true;
            }
            var closest = 0;
            for (var i = 0; i < offsets.Count; i++)
            {
                if (offsets[i] <= halfWidth)
                {
                    return false;
                }
                if (offsets[i] < offsets[closest])
                {
                    closest = i;
                }
            }
            return closest == me;
        }
    }

    /// <summary>
    /// Spaces out a repeated event: Try(now) is true at most once per interval. Used per squad for
    /// "I hear something." and for alerts from heard enemies. Main thread only.
    /// </summary>
    public sealed class RateGate
    {
        private float _next = float.MinValue;

        /// <summary>True (and starts a new interval) when the last success was at least interval seconds ago.</summary>
        public bool Try(float now, float interval)
        {
            if (now < _next)
            {
                return false;
            }
            _next = now + Math.Max(0f, interval);
            return true;
        }
    }
}
