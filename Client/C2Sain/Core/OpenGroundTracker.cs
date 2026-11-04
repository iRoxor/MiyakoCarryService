// OpenGroundTracker.cs -- notices one carry bot standing still in the open, for the diagnostics.
//
// One instance per carry bot, owned by C2Sain/OpenGroundWatch.cs, which feeds it about one sample
// a second: the bot's position and whether anything solid or a bush is close around it. It never
// changes what a bot does. Unity-free so the logic harness
// (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it with scripted samples. Reads and
// writes no files.
//
// A run starts when a sample finds the bot still (moved at most StillDistance since the previous
// sample) and in the open. It is dated from the previous sample when the bot was already there and
// in the open then, otherwise from this one. It is reported once it reaches ReportAfterSeconds, and
// ends at the first sample where the bot has moved or has cover; only a reported run reports its
// end. A gap of more than MaxSampleGap seconds between samples ends the run (cause Gap) and starts
// afresh, because nothing is known about the time in between.

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Tunables for <see cref="OpenGroundTracker"/>.</summary>
    public sealed class OpenGroundSettings
    {
        /// <summary>Metres a bot may move between two samples and still count as standing still.</summary>
        public float StillDistance = 0.5f;

        /// <summary>Seconds a bot must stand still in the open before the run is reported.</summary>
        public float ReportAfterSeconds = 5f;

        /// <summary>Longest gap (s) between samples that still continues a run.</summary>
        public float MaxSampleGap = 3f;
    }

    /// <summary>What a sample changed.</summary>
    public enum OpenGroundEvent
    {
        None,
        Started,
        Ended,
    }

    /// <summary>Why a reported run ended.</summary>
    public enum OpenGroundEnd
    {
        None,
        Moved,
        Covered,
        Gap,
    }

    /// <summary>One bot's still-in-the-open state. Not thread-safe; main thread only.</summary>
    public sealed class OpenGroundTracker
    {
        private bool _hasLast;
        private V3 _lastPosition;
        private float _lastTime;
        private bool _lastInOpen;
        private float _runStart = -1f;
        private float _lastStillTime;

        /// <summary>True when the latest sample found the bot still and in the open.</summary>
        public bool StillInOpen { get; private set; }

        /// <summary>True once the current run has been reported (Started returned).</summary>
        public bool Reported { get; private set; }

        /// <summary>Length (s) of the current run, or of the run that just ended.</summary>
        public float RunSeconds { get; private set; }

        /// <summary>Why the last reported run ended; None until one has.</summary>
        public OpenGroundEnd LastEnd { get; private set; }

        /// <summary>Forgets everything, as if no sample had been taken.</summary>
        public void Reset()
        {
            _hasLast = false;
            _lastInOpen = false;
            _runStart = -1f;
            StillInOpen = false;
            Reported = false;
            RunSeconds = 0f;
            LastEnd = OpenGroundEnd.None;
        }

        /// <summary>
        /// Takes one sample and says whether a reported run started or ended with it.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="position">The bot's position.</param>
        /// <param name="inOpen">True when nothing solid and no bush is close around the bot.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        public OpenGroundEvent Sample(float now, V3 position, bool inOpen, OpenGroundSettings settings)
        {
            var continuous = _hasLast && now - _lastTime <= settings.MaxSampleGap;
            var still = continuous && V3.Distance(position, _lastPosition) <= settings.StillDistance;
            var previousTime = _lastTime;
            var previousInOpen = _lastInOpen;
            _hasLast = true;
            _lastPosition = position;
            _lastTime = now;
            _lastInOpen = inOpen;

            if (still && inOpen)
            {
                if (_runStart < 0f)
                {
                    _runStart = previousInOpen ? previousTime : now;
                }
                StillInOpen = true;
                _lastStillTime = now;
                RunSeconds = now - _runStart;
                if (!Reported && RunSeconds >= settings.ReportAfterSeconds)
                {
                    Reported = true;
                    return OpenGroundEvent.Started;
                }
                return OpenGroundEvent.None;
            }

            StillInOpen = false;
            var wasReported = Reported;
            if (_runStart >= 0f)
            {
                RunSeconds = _lastStillTime - _runStart;
            }
            _runStart = -1f;
            Reported = false;
            if (!wasReported)
            {
                return OpenGroundEvent.None;
            }
            LastEnd = !continuous ? OpenGroundEnd.Gap : !still ? OpenGroundEnd.Moved : OpenGroundEnd.Covered;
            return OpenGroundEvent.Ended;
        }
    }
}
