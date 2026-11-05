// LookScan.cs -- where a carry bot looks while it holds still without an enemy to watch.
//
// One instance per carry bot, owned by BotForkState. GuardWatchLogic ticks it every frame while the
// bot watches from its spot, and GuardMoveLogic does too while a walk has stalled. Unity-free so the
// logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it with scripted
// random numbers and sight lines. Reads and writes no files.
//
// Each look holds MinHoldSeconds-MaxHoldSeconds, at an offset within ArcDegrees either side of the
// bot's watch direction. Every (LooksBetweenChecksBehind + 1)th look checks behind instead: within
// BehindArcDegrees of the opposite direction, held BehindHoldSeconds. Each look tries Candidates
// random offsets and keeps the one with the longest clear sight line (capped at ViewCapMetres), so a
// bot posted beside a wall looks down the hallway, doorway or stairs rather than at the wall.
// Fork v2-v6 picked one random offset within +/-70 degrees and never looked behind. In the user's
// build-6 raid (2026-11-05) a Reshala guard walked behind a carry bot unseen; the user had been
// reporting bots whose view stayed "glued to a wall".

using System;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Tunables for <see cref="LookScan"/>; the plugin uses the defaults.</summary>
    public sealed class LookScanSettings
    {
        /// <summary>Degrees either side of the watch direction an ordinary look may take.</summary>
        public float ArcDegrees = 70f;

        /// <summary>Shortest and longest time an ordinary look holds, in seconds.</summary>
        public float MinHoldSeconds = 2f;
        public float MaxHoldSeconds = 4f;

        /// <summary>Ordinary looks between two checks behind; 0 never checks behind.</summary>
        public int LooksBetweenChecksBehind = 3;

        /// <summary>Degrees either side of the direction opposite the watch direction a check behind may take.</summary>
        public float BehindArcDegrees = 30f;

        /// <summary>Seconds a check behind holds.</summary>
        public float BehindHoldSeconds = 1.5f;

        /// <summary>Random offsets tried per look; the one with the longest clear sight line wins.</summary>
        public int Candidates = 5;

        /// <summary>Sight lines longer than this (m) count as equally open, so open directions still vary.</summary>
        public float ViewCapMetres = 20f;
    }

    /// <summary>Per-bot look schedule. Main thread only.</summary>
    public sealed class LookScan
    {
        private readonly Func<float> _random01;
        private float _nextLook;
        private int _looks;
        private bool _started;

        /// <summary>Creates a scan.</summary>
        /// <param name="random01">Returns a random number in [0, 1); used for offsets and hold times.</param>
        public LookScan(Func<float> random01)
        {
            _random01 = random01;
        }

        /// <summary>The current look: degrees clockwise from the watch direction, in (-180, 180].</summary>
        public float Offset { get; private set; }

        /// <summary>True when the current look is a check behind.</summary>
        public bool Behind { get; private set; }

        /// <summary>Game time when the current look ends.</summary>
        public float NextLook => _nextLook;

        /// <summary>
        /// Picks a new look when the current one has run out (or on the first call) and returns true
        /// then; otherwise returns false and keeps <see cref="Offset"/>.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="clearDistance">Metres of clear view at a given offset from the watch direction.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        public bool Tick(float now, Func<float, float> clearDistance, LookScanSettings settings)
        {
            if (_started && now < _nextLook)
            {
                return false;
            }
            _started = true;
            _looks++;
            Behind = settings.LooksBetweenChecksBehind > 0 && _looks % (settings.LooksBetweenChecksBehind + 1) == 0;
            var centre = Behind ? 180f : 0f;
            var arc = Behind ? settings.BehindArcDegrees : settings.ArcDegrees;
            var best = centre;
            var bestScore = float.MinValue;
            for (var i = 0; i < Math.Max(1, settings.Candidates); i++)
            {
                var offset = centre + (2f * _random01() - 1f) * arc;
                var score = Math.Min(clearDistance(offset), settings.ViewCapMetres);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = offset;
                }
            }
            Offset = Wrap(best);
            _nextLook = now + (Behind ? settings.BehindHoldSeconds
                : settings.MinHoldSeconds + (settings.MaxHoldSeconds - settings.MinHoldSeconds) * _random01());
            return true;
        }

        /// <summary>Ends the current look, so the next <see cref="Tick"/> picks one at once (a new watch).</summary>
        public void Restart()
        {
            _started = false;
        }

        /// <summary>An angle wrapped into (-180, 180].</summary>
        public static float Wrap(float degrees)
        {
            var r = degrees % 360f;
            if (r > 180f)
            {
                r -= 360f;
            }
            else if (r <= -180f)
            {
                r += 360f;
            }
            return r;
        }
    }
}
