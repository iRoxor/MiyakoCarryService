// ForkCost.cs -- times the fork's own heavy work, frame by frame, and logs it (fork v12).
//
// Wrapped around the fork's heavy calls: ForkCost.Begin(kind) in a using statement. The kinds
// (Core/FrameCost.cs, CostKind) are the guard planner's slices (TeamModes.Pump), Defend Me plans and shifts
// (TeamModes), and, inside C2Nav, the flank spot search, the closest-reachable fallback and the follow-spot
// pick, and in the two Harmony patches that run inside SAIN, the bush sight patch and the decision filter.
// Each kind has its own switch in the F12 menu (ForkConfig "Timing: ..."), off by default, so any
// combination can be recorded, and nothing is timed unless "Debug: diagnostic log" is on as well. A kind
// that is off costs one array read per call.
//
// GameLoop.Update calls Tick once a frame: it copies the switches into the recorder and writes the frames
// that were heavy ("fork cost: frame 123456, 6.1 ms: planner 3.2 ms x1, follow-spot 2.9 ms x3"). The
// frame number is Unity's Time.frameCount, the one the Frame Hitch Recorder writes as "frame" (give or
// take one), so a frame in its records can be looked up here. OnGameStartedPatch calls StartRaid and
// BrainMgr.OnRaidEnded calls EndRaid, which writes the raid summary ("fork cost summary: ..."). The log
// summary tool (.for-claude-to-reference/tools/mcs_fork_log_summary.py) reads both. Owner of the
// recorder and the pending reports; main thread only. Reads and writes no files besides the BepInEx log.

using System.Collections.Generic;
using System.Diagnostics;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>A timing scope; dispose it (a using statement) to end the timed call. The default value times nothing.</summary>
    internal readonly struct CostScope : System.IDisposable
    {
        private readonly CostToken _token;

        internal CostScope(CostToken token)
        {
            _token = token;
        }

        /// <summary>Ends the timed call and adds its time to the current frame.</summary>
        public void Dispose()
        {
            if (_token.Active)
            {
                ForkCost.End(_token);
            }
        }
    }

    /// <summary>The fork's per-frame cost accounting; see the file header.</summary>
    internal static class ForkCost
    {
        private static readonly FrameCostRecorder _recorder = new(Stopwatch.GetTimestamp, Stopwatch.Frequency / 1000.0);
        private static readonly FrameCostSettings _settings = new();
        private static readonly List<FrameCostReport> _reports = new();
        private static int _raidStartFrame;
        private static bool _loggedFault;

        /// <summary>
        /// Starts timing a call of this kind. Use as <c>using (ForkCost.Begin(CostKind.Flank)) { ... }</c> (or a
        /// using declaration); the time of the block is added to the frame when it ends, on any exit.
        /// </summary>
        public static CostScope Begin(CostKind kind)
        {
            return new CostScope(_recorder.Begin(kind));
        }

        /// <summary>Called by <see cref="CostScope.Dispose"/>.</summary>
        internal static void End(CostToken token)
        {
            try
            {
                _recorder.End(token, Time.frameCount, ForkLog.RaidSeconds, _settings);
            }
            catch (System.Exception e)
            {
                LogFaultOnce(e);
            }
        }

        /// <summary>
        /// Once a frame (GameLoop.Update): copies the switches and threshold from the settings, closes the
        /// previous frame and writes its report when it was heavy. Writes to the BepInEx log.
        /// </summary>
        internal static void Tick()
        {
            try
            {
                Refresh();
                _recorder.CloseFrame(Time.frameCount, _settings);
                WriteReports();
            }
            catch (System.Exception e)
            {
                LogFaultOnce(e);
            }
        }

        /// <summary>
        /// An exception here must never reach the code being timed (some of it runs inside SAIN), so the timing
        /// swallows it and logs the first one.
        /// </summary>
        private static void LogFaultOnce(System.Exception e)
        {
            if (!_loggedFault)
            {
                _loggedFault = true;
                McsLogger.LogError(e);
            }
        }

        /// <summary>A raid has started: forgets the last one's counts and notes the frame it started on.</summary>
        internal static void StartRaid()
        {
            Refresh();
            _recorder.Reset();
            _raidStartFrame = Time.frameCount;
        }

        /// <summary>
        /// The raid is over: writes the last heavy frame and the summary (nothing when no kind was switched
        /// on), then forgets the counts. Call before ForkLog.StopRaidClock so the lines carry the raid clock.
        /// Does nothing outside a raid.
        /// </summary>
        internal static void EndRaid()
        {
            var seconds = ForkLog.RaidSeconds;
            if (seconds < 0)
            {
                return;
            }
            _recorder.Flush(_settings);
            WriteReports();
            foreach (var line in _recorder.Summary(Time.frameCount - _raidStartFrame, seconds, _settings))
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] {line}");
            }
            _recorder.Reset();
        }

        /// <summary>Copies the per-kind switches (each needs "Debug: diagnostic log" on too) and the threshold into the recorder.</summary>
        private static void Refresh()
        {
            var logging = ForkConfig.DebugLog?.Value == true;
            _settings.ThresholdMs = ForkConfig.CostThresholdMs?.Value ?? 2f;
            for (var i = 0; i < CostNames.Count; i++)
            {
                var kind = (CostKind)i;
                _recorder.SetEnabled(kind, logging && ForkConfig.CostSwitch(kind)?.Value == true);
            }
        }

        /// <summary>Writes the reports of the frames that were heavy.</summary>
        private static void WriteReports()
        {
            _reports.Clear();
            _recorder.TakeReports(_reports);
            foreach (var report in _reports)
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] fork cost: {report.Format()}");
            }
            _reports.Clear();
        }
    }
}
