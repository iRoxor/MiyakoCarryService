// FrozenViewTracker.cs -- notices a carry bot whose view has not moved for a while.
//
// One instance per carry bot, owned by C2Sain/FrozenViewWatch.cs, which samples every carry bot about
// once a second (PlayerDataMgr.RecordAgentInfo). Unity-free so the logic harness
// (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it with scripted positions, view
// directions and times. Reads and writes no files.
//
// A run is a stretch of samples in which the bot stays within StillDistance of the previous sample,
// its view stays within YawTolerance degrees of where it looked when the run began, and it is not
// exempt (the plugin exempts a bot aiming at an enemy it can see). A run is reported once it reaches
// ReportAfterSeconds, and its end is reported with the cause: the bot moved, turned, became exempt,
// or was not sampled for more than MaxSampleGap seconds. Added in fork v7 after the user's build-6
// raid (2026-11-05), where a carry bot looked at the same spot for 12 s while an enemy walked past.

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Tunables for <see cref="FrozenViewTracker"/>; the plugin uses the defaults.</summary>
    public sealed class FrozenViewSettings
    {
        /// <summary>Metres a bot may move between two samples and still count as standing.</summary>
        public float StillDistance = 0.5f;

        /// <summary>Degrees the view may drift from where the run began and still count as frozen.</summary>
        public float YawTolerance = 5f;

        /// <summary>Seconds of frozen view before the run is reported.</summary>
        public float ReportAfterSeconds = 8f;

        /// <summary>A gap between samples longer than this (s) ends the run.</summary>
        public float MaxSampleGap = 3f;
    }

    /// <summary>What a sample changed.</summary>
    public enum FrozenViewEvent
    {
        None,
        Started,
        Ended,
    }

    /// <summary>Why a reported run ended.</summary>
    public enum FrozenViewEnd
    {
        None,
        Moved,
        Turned,
        Exempt,
        Gap,
    }

    /// <summary>Per-bot frozen-view state. Main thread only.</summary>
    public sealed class FrozenViewTracker
    {
        private bool _hasLast;
        private float _lastTime;
        private V3 _lastPosition;
        private bool _inRun;
        private float _runStart;
        private float _lastFrozen;

        /// <summary>Where the bot looked when the current run began, in degrees.</summary>
        public float RunYaw { get; private set; }

        /// <summary>Seconds in the current run, or in the run that just ended.</summary>
        public float RunSeconds { get; private set; }

        /// <summary>True once the current run has been reported.</summary>
        public bool Reported { get; private set; }

        /// <summary>Why the last reported run ended.</summary>
        public FrozenViewEnd LastEnd { get; private set; }

        /// <summary>
        /// Takes one sample and says whether a run was reported or a reported run ended; see the
        /// file header for the rules.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="position">The bot's position.</param>
        /// <param name="yaw">Where the bot looks: degrees clockwise from +Z.</param>
        /// <param name="exempt">True when a fixed view is expected now (aiming at a visible enemy).</param>
        /// <param name="settings">Tunables; read on every call.</param>
        public FrozenViewEvent Sample(float now, V3 position, float yaw, bool exempt, FrozenViewSettings settings)
        {
            var continuous = _hasLast && now - _lastTime <= settings.MaxSampleGap;
            var moved = _hasLast && V3.Distance(position, _lastPosition) > settings.StillDistance;
            var turned = _inRun && GuardFormation.AngleBetween(yaw, RunYaw) > settings.YawTolerance;
            var result = FrozenViewEvent.None;

            if (_inRun && (!continuous || moved || turned || exempt))
            {
                if (Reported)
                {
                    result = FrozenViewEvent.Ended;
                    LastEnd = !continuous ? FrozenViewEnd.Gap : exempt ? FrozenViewEnd.Exempt : moved ? FrozenViewEnd.Moved : FrozenViewEnd.Turned;
                    RunSeconds = _lastFrozen - _runStart;
                }
                _inRun = false;
                Reported = false;
            }

            if (!_inRun)
            {
                if (!exempt)
                {
                    _inRun = true;
                    _runStart = now;
                    _lastFrozen = now;
                    RunYaw = yaw;
                    if (result == FrozenViewEvent.None)
                    {
                        RunSeconds = 0f;
                    }
                }
            }
            else
            {
                _lastFrozen = now;
                RunSeconds = now - _runStart;
                if (!Reported && RunSeconds >= settings.ReportAfterSeconds)
                {
                    Reported = true;
                    result = FrozenViewEvent.Started;
                }
            }

            _hasLast = true;
            _lastTime = now;
            _lastPosition = position;
            return result;
        }
    }
}
