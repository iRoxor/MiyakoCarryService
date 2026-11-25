// FrameCost.cs -- adds up, frame by frame, how long the fork's own heavy work takes (fork v12).
//
// Used by C2Sain/ForkCost.cs, which wraps the fork's heavy calls in timing scopes (the guard planner's
// slices, Defend Me plans and shifts, the flank spot search, the closest-reachable fallback, follow-spot
// picks, the two Harmony patches that run inside SAIN every frame, the carry bots' markers and, since fork v17, the reach
// watch's check of each bot), copies the "Timing:" settings
// into <see cref="FrameCostRecorder.SetEnabled"/> and logs what the recorder reports. Unity-free so the
// logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it with a scripted clock.
// Reads and writes no files.
//
// Why it exists: the Frame Hitch Recorder (a separate plugin, stutter-investigation/hitch-recorder) times
// the game's frames and each plugin's Update and each bot's update, so it can say a carry bot's update held
// a frame, but not which part of that update was the fork. This says it from the inside: for each frame
// in which a switched-on kind of work ran, the milliseconds it took. Frame numbers are Unity's
// Time.frameCount, the number the recorder writes as "frame" (give or take one), so a long frame in
// its records can be looked up here.
//
// A call's time is its own: the time of timed calls made inside it (the decision filter calls the flank
// search) is taken off the outer call and credited to the inner kind, so a frame's total never counts
// the same milliseconds twice. Only the kinds switched on are timed; with every kind off a scope costs a
// single array read.

using System;
using System.Collections.Generic;
using System.Text;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>The fork's kinds of heavy work that can be timed, each with its own switch.</summary>
    public enum CostKind
    {
        /// <summary>The guard planner: each frame's slice of every squad's plan job, and delivering the finished plan.</summary>
        Planner = 0,

        /// <summary>Defend Me plans and shifts (still made in one frame).</summary>
        Defend = 1,

        /// <summary>The flank spot search (C2Nav.TryPickFlankSpot).</summary>
        Flank = 2,

        /// <summary>The closest-reachable fallback (C2Nav.TryClosestReachable).</summary>
        Reach = 3,

        /// <summary>Follow-spot picks (C2Nav.TryPickFollowSpot).</summary>
        FollowSpot = 4,

        /// <summary>The bush sight patch inside SAIN (SainFoliageSightPatch), called about 30 times a second per bot and enemy.</summary>
        FoliageSight = 5,

        /// <summary>The decision filter inside SAIN (SainSetDecisionsPatch, DecisionPolicy).</summary>
        Decisions = 6,

        /// <summary>The carry bots' markers: their per-frame upkeep and the handler that runs before each camera draws (StrobeMarkers).</summary>
        Strobe = 7,

        /// <summary>Fork v17: the reach watch's once-a-second check of a bot (C2Sain/ReachWatch.cs: navmesh queries for a stranded bot and for a path to the player).</summary>
        ReachWatch = 8,
    }

    /// <summary>Names of the <see cref="CostKind"/>s as they appear in the log (read by .for-claude-to-reference/tools/mcs_fork_log_summary.py).</summary>
    public static class CostNames
    {
        /// <summary>Number of kinds.</summary>
        public const int Count = 9;

        private static readonly string[] _names = { "planner", "defend", "flank-spot", "closest-reachable", "follow-spot", "foliage-sight", "decisions", "strobe-markers", "reach-watch" };

        /// <summary>The log name of a kind.</summary>
        public static string Of(CostKind kind)
        {
            return _names[(int)kind];
        }
    }

    /// <summary>Tunables for <see cref="FrameCostRecorder"/>; the plugin copies the first from its settings.</summary>
    public sealed class FrameCostSettings
    {
        /// <summary>Milliseconds of timed fork work in one frame at which the frame is logged. Default 2.</summary>
        public double ThresholdMs = 2.0;

        /// <summary>
        /// True (the default): a call's time excludes the timed calls made inside it. False counts them in
        /// both, so a frame's total can be more than the time the fork spent.
        /// </summary>
        public bool SelfTime = true;
    }

    /// <summary>One frame's timed fork work.</summary>
    public sealed class FrameCostReport
    {
        /// <summary>Unity's Time.frameCount for the frame.</summary>
        public int Frame;

        /// <summary>Seconds into the raid when the frame's first timed call ended (-1 outside a raid).</summary>
        public double RaidSeconds = -1;

        /// <summary>Milliseconds of all kinds.</summary>
        public double TotalMs;

        /// <summary>Milliseconds per kind, indexed by <see cref="CostKind"/>.</summary>
        public readonly double[] Ms = new double[CostNames.Count];

        /// <summary>Timed calls per kind.</summary>
        public readonly int[] Calls = new int[CostNames.Count];

        /// <summary>
        /// "frame 123456, 6.1 ms: planner 3.2 ms x1, follow-spot 2.9 ms x3": the frame number, the total, then each
        /// kind that ran, longest first, with its calls. The wording the log summary tool reads.
        /// </summary>
        public string Format()
        {
            var order = new List<int>();
            for (var i = 0; i < CostNames.Count; i++)
            {
                if (Calls[i] > 0)
                {
                    order.Add(i);
                }
            }
            order.Sort((a, b) => Ms[a] != Ms[b] ? Ms[b].CompareTo(Ms[a]) : a.CompareTo(b));
            var text = new StringBuilder(FormattableString.Invariant($"frame {Frame}, {TotalMs:0.0} ms: "));
            for (var n = 0; n < order.Count; n++)
            {
                var i = order[n];
                text.Append(n == 0 ? "" : ", ").Append(FormattableString.Invariant($"{CostNames.Of((CostKind)i)} {Ms[i]:0.0} ms x{Calls[i]}"));
            }
            return text.ToString();
        }
    }

    /// <summary>A started timing scope; give it back to <see cref="FrameCostRecorder.End"/>. The default value is a scope that does nothing.</summary>
    public readonly struct CostToken
    {
        internal readonly CostKind Kind;
        internal readonly long Start;
        internal readonly long OuterChild;
        internal readonly bool Active;

        internal CostToken(CostKind kind, long start, long outerChild)
        {
            Kind = kind;
            Start = start;
            OuterChild = outerChild;
            Active = true;
        }
    }

    /// <summary>Adds up timed calls per frame and per raid; see the file header. Main thread only.</summary>
    public sealed class FrameCostRecorder
    {
        /// <summary>Upper edges (ms) of the buckets the summary counts frames in; a frame at or above the last edge is the last bucket.</summary>
        private static readonly double[] _bucketEdges = { 0.5, 1.0, 2.0, 5.0, 10.0 };

        private static readonly string[] _bucketNames = { "under 0.5 ms", "0.5-1 ms", "1-2 ms", "2-5 ms", "5-10 ms", "10 ms or more" };

        private readonly Func<long> _ticks;
        private readonly double _ticksPerMs;
        private readonly bool[] _enabled = new bool[CostNames.Count];
        private readonly bool[] _everEnabled = new bool[CostNames.Count];
        private readonly Queue<FrameCostReport> _reports = new();

        private FrameCostReport _open;
        private int _depth;
        private long _childTicks;

        private readonly double[] _kindMs = new double[CostNames.Count];
        private readonly int[] _kindCalls = new int[CostNames.Count];
        private readonly int[] _kindFrames = new int[CostNames.Count];
        private readonly double[] _kindWorstCall = new double[CostNames.Count];
        private readonly double[] _kindWorstFrame = new double[CostNames.Count];
        private readonly int[] _buckets = new int[_bucketNames.Length];
        private int _framesTimed;
        private int _framesOver;
        private double _totalMs;
        private double _worstFrameMs;
        private int _worstFrame;
        private double _worstFrameAt = -1;

        /// <summary>Creates a recorder timing with <paramref name="ticks"/> (a monotonic counter) at <paramref name="ticksPerMs"/> ticks per millisecond.</summary>
        public FrameCostRecorder(Func<long> ticks, double ticksPerMs)
        {
            _ticks = ticks;
            _ticksPerMs = ticksPerMs;
        }

        /// <summary>Switches timing of a kind on or off. A kind that was ever on appears in the raid summary.</summary>
        public void SetEnabled(CostKind kind, bool on)
        {
            _enabled[(int)kind] = on;
            _everEnabled[(int)kind] |= on;
        }

        /// <summary>True when the kind is being timed.</summary>
        public bool IsEnabled(CostKind kind)
        {
            return _enabled[(int)kind];
        }

        /// <summary>
        /// Starts timing a call of the given kind: an inactive token when the kind is off. A scope started
        /// inside another is that scope's child (see the file header).
        /// </summary>
        public CostToken Begin(CostKind kind)
        {
            if (!_enabled[(int)kind])
            {
                return default;
            }
            var outer = _depth == 0 ? 0L : _childTicks;
            _depth++;
            _childTicks = 0;
            return new CostToken(kind, _ticks(), outer);
        }

        /// <summary>
        /// Ends a timed call and adds its time to the frame <paramref name="frame"/>. The first call of a
        /// later frame closes the one before it (<see cref="TakeReports"/> then has its report when it was
        /// heavy enough).
        /// </summary>
        /// <param name="token">What <see cref="Begin"/> returned.</param>
        /// <param name="frame">Unity's Time.frameCount now.</param>
        /// <param name="raidSeconds">Seconds into the raid, or -1.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        public void End(CostToken token, int frame, double raidSeconds, FrameCostSettings settings)
        {
            if (!token.Active)
            {
                return;
            }
            var elapsed = _ticks() - token.Start;
            var own = settings.SelfTime ? Math.Max(0L, elapsed - _childTicks) : elapsed;
            _depth--;
            _childTicks = token.OuterChild + elapsed;
            var ms = own / _ticksPerMs;
            var kind = (int)token.Kind;

            if (_open != null && _open.Frame != frame)
            {
                CloseOpenFrame(settings);
            }
            _open ??= new FrameCostReport { Frame = frame, RaidSeconds = raidSeconds };
            _open.Ms[kind] += ms;
            _open.Calls[kind]++;
            _open.TotalMs += ms;
            _kindMs[kind] += ms;
            _kindCalls[kind]++;
            _kindWorstCall[kind] = Math.Max(_kindWorstCall[kind], ms);
        }

        /// <summary>Closes the open frame when it is older than <paramref name="currentFrame"/> (the plugin calls this once a frame, so a heavy frame is logged promptly).</summary>
        public void CloseFrame(int currentFrame, FrameCostSettings settings)
        {
            if (_open != null && _open.Frame != currentFrame)
            {
                CloseOpenFrame(settings);
            }
        }

        /// <summary>Closes the open frame whatever its number (the raid is over).</summary>
        public void Flush(FrameCostSettings settings)
        {
            if (_open != null)
            {
                CloseOpenFrame(settings);
            }
        }

        /// <summary>Moves the reports of the frames that passed the threshold, oldest first, into <paramref name="into"/>.</summary>
        public void TakeReports(List<FrameCostReport> into)
        {
            while (_reports.Count > 0)
            {
                into.Add(_reports.Dequeue());
            }
        }

        private void CloseOpenFrame(FrameCostSettings settings)
        {
            var frame = _open;
            _open = null;
            _framesTimed++;
            _totalMs += frame.TotalMs;
            var bucket = 0;
            while (bucket < _bucketEdges.Length && frame.TotalMs >= _bucketEdges[bucket])
            {
                bucket++;
            }
            _buckets[bucket]++;
            for (var i = 0; i < CostNames.Count; i++)
            {
                if (frame.Calls[i] > 0)
                {
                    _kindFrames[i]++;
                    _kindWorstFrame[i] = Math.Max(_kindWorstFrame[i], frame.Ms[i]);
                }
            }
            if (frame.TotalMs > _worstFrameMs)
            {
                _worstFrameMs = frame.TotalMs;
                _worstFrame = frame.Frame;
                _worstFrameAt = frame.RaidSeconds;
            }
            if (frame.TotalMs >= settings.ThresholdMs)
            {
                _framesOver++;
                _reports.Enqueue(frame);
            }
        }

        /// <summary>
        /// The raid-end summary lines (empty when no kind was switched on during the raid): one line on the
        /// frames, one per kind that was switched on, one on how the timed frames were spread. The wording the
        /// log summary tool reads. Call <see cref="Flush"/> first.
        /// </summary>
        /// <param name="raidFrames">Frames the raid lasted.</param>
        /// <param name="raidSeconds">Seconds the raid lasted.</param>
        /// <param name="settings">Supplies the threshold the first line quotes.</param>
        public IReadOnlyList<string> Summary(int raidFrames, double raidSeconds, FrameCostSettings settings)
        {
            var lines = new List<string>();
            var any = false;
            for (var i = 0; i < CostNames.Count; i++)
            {
                any |= _everEnabled[i];
            }
            if (!any)
            {
                return lines;
            }
            var share = raidSeconds > 0 ? _totalMs / (raidSeconds * 1000.0) * 100.0 : 0.0;
            var worst = _framesTimed > 0 ? FormattableString.Invariant($"{_worstFrameMs:0.0} ms (frame {_worstFrame}{(_worstFrameAt >= 0 ? " at " + ClockText(_worstFrameAt) : "")})") : "none";
            lines.Add(FormattableString.Invariant(
                $"fork cost summary: {raidFrames} frame(s) in the raid, {raidSeconds:0} s; fork work was timed in {_framesTimed} of them; {_framesOver} at or over {settings.ThresholdMs:0.0#} ms; worst {worst}; {_totalMs / 1000.0:0.00} s of timed fork work in all ({share:0.00}% of the raid)"));
            for (var i = 0; i < CostNames.Count; i++)
            {
                if (!_everEnabled[i])
                {
                    continue;
                }
                var name = CostNames.Of((CostKind)i);
                lines.Add(_kindCalls[i] == 0
                    ? $"fork cost summary: {name}: no calls"
                    : FormattableString.Invariant($"fork cost summary: {name}: {_kindCalls[i]} call(s) in {_kindFrames[i]} frame(s), {_kindMs[i]:0} ms in all, mean {_kindMs[i] / _kindCalls[i]:0.00} ms a call, worst call {_kindWorstCall[i]:0.0} ms, worst frame {_kindWorstFrame[i]:0.0} ms"));
            }
            var spread = new StringBuilder("fork cost summary: timed frames by fork work in the frame: ");
            for (var b = 0; b < _buckets.Length; b++)
            {
                spread.Append(b == 0 ? "" : ", ").Append(_bucketNames[b]).Append(' ').Append(_buckets[b]);
            }
            lines.Add(spread.ToString());
            return lines;
        }

        /// <summary>Forgets everything counted (a raid starts); the switches stay as they are, and "ever switched on" restarts from them.</summary>
        public void Reset()
        {
            _open = null;
            _depth = 0;
            _childTicks = 0;
            _reports.Clear();
            Array.Clear(_kindMs, 0, _kindMs.Length);
            Array.Clear(_kindCalls, 0, _kindCalls.Length);
            Array.Clear(_kindFrames, 0, _kindFrames.Length);
            Array.Clear(_kindWorstCall, 0, _kindWorstCall.Length);
            Array.Clear(_kindWorstFrame, 0, _kindWorstFrame.Length);
            Array.Clear(_buckets, 0, _buckets.Length);
            _framesTimed = 0;
            _framesOver = 0;
            _totalMs = 0;
            _worstFrameMs = 0;
            _worstFrame = 0;
            _worstFrameAt = -1;
            for (var i = 0; i < CostNames.Count; i++)
            {
                _everEnabled[i] = _enabled[i];
            }
        }

        /// <summary>"mm:ss.s" for seconds into the raid, as the fork's log lines write it.</summary>
        private static string ClockText(double seconds)
        {
            var tenths = (long)(seconds * 10);
            return FormattableString.Invariant($"{tenths / 600:00}:{tenths % 600 / 10:00}.{tenths % 10}");
        }
    }
}
