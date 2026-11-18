// PlanSlicer.cs -- runs one long job a few milliseconds per frame instead of all at once (fork v12).
//
// Used by C2Sain/GuardPlanner.cs (a GuardPlanJob wraps the planner's steps in a PlanSlicer) and
// C2Sain/TeamModes.cs (Pump gives each squad's job its frame budget and formats the plan's timing
// line). Unity-free so the logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can
// drive it with scripted step costs and a fake clock. Reads and writes no files.
//
// Why it exists: the fork v9 indoor planner (GuardPlanner.TryApproachPosts) ran as one synchronous
// call inside the frame that asked for a plan. In the two Reserve raids of 2026-11-09 (build 11;
// evidence/2026-11-09-build-11-reserve) each indoor plan took a median of 20 and 23 ms (max 29 and
// 33) of a frame that has about 16-25 ms in all, and plans came 7-14 times a minute while the squad
// guarded or fought. Now the planner is written as a sequence of small steps (an iterator that yields
// after each route trace, each batch of candidate spots and so on) and this class runs as many of
// them per frame as fit in a time budget, so no frame spends more than a few milliseconds on it.
// The bots keep their current posts (or follow spots) until the finished plan replaces them.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>
    /// What a planning step spent its time on, for the timing line. A step yields the phase of the work
    /// it just finished; the last stretch of work (after the final yield) is <see cref="Finish"/>.
    /// </summary>
    public enum PlanPhase
    {
        /// <summary>Collecting the cover objects near the player.</summary>
        Setup = 0,

        /// <summary>Choosing the navmesh points the indoor routes lead to.</summary>
        Scan = 1,

        /// <summary>Tracing and walking the indoor routes.</summary>
        Routes = 2,

        /// <summary>Searching spots beside an indoor approach.</summary>
        Posts = 3,

        /// <summary>Searching spots in a sector (the even detail, contact-report fans, gap and solo posts).</summary>
        Spots = 4,

        /// <summary>Whatever ran after the last step.</summary>
        Finish = 5,
    }

    /// <summary>Tunables for <see cref="PlanSlicer"/>; the plugin changes only <see cref="BudgetMs"/>.</summary>
    public sealed class SliceSettings
    {
        /// <summary>
        /// Milliseconds one frame may spend on one plan job. Default 3: a plan that took 20-30 ms in
        /// one frame (the fork v9-v11 indoor planner) then takes about 8-10 frames, and no frame is
        /// held for more than about 3 ms plus the step that crossed the line. Larger finishes the plan
        /// sooner but costs more per frame; 30 or more runs a whole plan in one frame again.
        /// </summary>
        public double BudgetMs = 3.0;

        /// <summary>
        /// Steps a frame always runs when the job is not finished, whatever the budget, so a job always
        /// completes. Default 1.
        /// </summary>
        public int MinStepsPerFrame = 1;

        /// <summary>
        /// True (the default): a frame also stops before a step that, going by the cost of the step before
        /// it, would carry the frame past the budget. False: it stops only once the budget is spent, so
        /// a frame can overrun by a whole step.
        /// </summary>
        public bool PredictOverrun = true;
    }

    /// <summary>What a job has cost so far; filled in by <see cref="PlanSlicer"/> (or by hand for a job run in one go).</summary>
    public sealed class SliceStats
    {
        /// <summary>Frames in which the job ran at least one step.</summary>
        public int Frames;

        /// <summary>Steps run, the last stretch after the final yield included.</summary>
        public int Steps;

        /// <summary>Milliseconds spent in all those steps.</summary>
        public double TotalMs;

        /// <summary>Most milliseconds the job spent in one frame.</summary>
        public double WorstFrameMs;

        /// <summary>Most milliseconds one step took (the part of a frame a budget cannot split).</summary>
        public double WorstStepMs;

        /// <summary>Milliseconds per <see cref="PlanPhase"/>, indexed by the enum's value.</summary>
        public readonly double[] PhaseMs = new double[Enum.GetValues(typeof(PlanPhase)).Length];

        /// <summary>The cost of work done in one frame without slicing (a Defend Me plan), for the same timing line.</summary>
        /// <param name="ms">Milliseconds it took.</param>
        /// <param name="phase">What it was spent on.</param>
        public static SliceStats OneFrame(double ms, PlanPhase phase)
        {
            var stats = new SliceStats { Frames = 1, Steps = 1, TotalMs = ms, WorstFrameMs = ms, WorstStepMs = ms };
            stats.PhaseMs[(int)phase] = ms;
            return stats;
        }
    }

    /// <summary>One plan job's steps, run a few milliseconds per frame. Not thread-safe; the game's main thread only.</summary>
    public sealed class PlanSlicer
    {
        private readonly IEnumerator<PlanPhase> _steps;
        private readonly Func<double> _clockMs;
        private double _lastStepMs;

        /// <summary>The wall clock in milliseconds, from the high-resolution timer. The plugin's clock.</summary>
        public static double SystemClockMs()
        {
            return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>Wraps a job. Nothing runs until <see cref="RunFrame"/>.</summary>
        /// <param name="steps">The job: each yield ends one step and names the phase of the work it did.</param>
        /// <param name="clockMs">A monotonic clock in milliseconds (the harness passes a scripted one).</param>
        public PlanSlicer(IEnumerable<PlanPhase> steps, Func<double> clockMs)
        {
            _steps = steps.GetEnumerator();
            _clockMs = clockMs;
        }

        /// <summary>True once the job has finished or been abandoned.</summary>
        public bool Done { get; private set; }

        /// <summary>What the job has cost so far.</summary>
        public SliceStats Stats { get; } = new();

        /// <summary>
        /// Runs steps until the frame's budget is spent: a frame runs at least
        /// <see cref="SliceSettings.MinStepsPerFrame"/> steps, then stops once its time reaches the
        /// budget or, with <see cref="SliceSettings.PredictOverrun"/>, once its time plus the cost of the
        /// step before would pass it. A step is never split, so a frame can run over the budget by the
        /// cost of one step. An exception from a step propagates; the steps run before it stay counted
        /// and the job is not finished (the caller abandons it).
        /// </summary>
        /// <param name="settings">The budget and rules; read on every call.</param>
        /// <returns>Milliseconds this call spent in steps.</returns>
        public double RunFrame(SliceSettings settings)
        {
            if (Done)
            {
                return 0.0;
            }
            var spent = 0.0;
            var ran = 0;
            var last = _clockMs();
            try
            {
                while (!Done)
                {
                    if (ran >= settings.MinStepsPerFrame
                        && (spent >= settings.BudgetMs || (settings.PredictOverrun && spent + _lastStepMs > settings.BudgetMs)))
                    {
                        break;
                    }
                    var more = _steps.MoveNext();
                    var now = _clockMs();
                    var cost = now - last;
                    last = now;
                    var phase = more ? _steps.Current : PlanPhase.Finish;
                    Stats.PhaseMs[(int)phase] += cost;
                    Stats.TotalMs += cost;
                    Stats.Steps++;
                    Stats.WorstStepMs = Math.Max(Stats.WorstStepMs, cost);
                    _lastStepMs = cost;
                    spent += cost;
                    ran++;
                    if (!more)
                    {
                        Done = true;
                    }
                }
            }
            finally
            {
                if (ran > 0)
                {
                    Stats.Frames++;
                    Stats.WorstFrameMs = Math.Max(Stats.WorstFrameMs, spent);
                }
            }
            return spent;
        }

        /// <summary>Gives up on the job: it counts as done and its remaining steps never run.</summary>
        public void Abandon()
        {
            if (!Done)
            {
                Done = true;
                _steps.Dispose();
            }
        }
    }

    /// <summary>The wording of a plan's timing, read by .for-claude-to-reference/tools/mcs_fork_log_summary.py.</summary>
    public static class SliceReport
    {
        /// <summary>
        /// "22 ms in 8 frame(s), worst frame 3.4 ms (scan 2.1, routes 17.4, posts 2.5)": the work in
        /// all, how many frames it was spread over, the most any one frame spent, and the phases that
        /// took at least 0.05 ms. The first number is the work, comparable with the "; N ms" that fork
        /// v9-v11 logged for a plan made in one frame; the third is what a frame actually felt.
        /// </summary>
        public static string Format(SliceStats stats)
        {
            // Invariant culture: a tool parses this text, and a comma decimal separator would break it.
            var text = new StringBuilder();
            text.Append(FormattableString.Invariant($"{stats.TotalMs:0} ms in {stats.Frames} frame(s), worst frame {stats.WorstFrameMs:0.0} ms"));
            var phases = new StringBuilder();
            foreach (PlanPhase phase in Enum.GetValues(typeof(PlanPhase)))
            {
                var ms = stats.PhaseMs[(int)phase];
                if (ms < 0.05)
                {
                    continue;
                }
                phases.Append(phases.Length == 0 ? "" : ", ").Append(FormattableString.Invariant($"{phase.ToString().ToLowerInvariant()} {ms:0.0}"));
            }
            if (phases.Length > 0)
            {
                text.Append(" (").Append(phases).Append(')');
            }
            return text.ToString();
        }
    }
}
