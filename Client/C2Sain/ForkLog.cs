// ForkLog.cs -- the raid clock the fork's diagnostic lines carry.
//
// OnGameStartedPatch (a postfix on GameWorld.OnGameStarted) starts the clock and BrainMgr.OnRaidEnded
// stops it. Every fork line written to the BepInEx log opens with Tag, "[C2/SAIN fork v7 04:14.3]"
// inside a raid (minutes:seconds since the raid started), "[C2/SAIN fork v7]" outside one.
//
// The Raid Review mod starts its own clock (a Stopwatch) in a postfix on the same method, and its
// exports time everything in milliseconds on it, so a fork line's time and a Raid Review time can be
// compared directly (within the few milliseconds between the two postfixes). Before fork v7 the
// lines carried no time, and .for-claude-to-reference/tools/raid_review_summary.py had to estimate
// it from ORBIT's frame-stamped lines (typically 1-2 s off, worse between sparse anchors).
// Reads and writes no files.

using System.Diagnostics;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>The fork's raid clock for log lines. Main thread only.</summary>
    internal static class ForkLog
    {
        private static readonly Stopwatch _raid = new();

        /// <summary>Starts the clock at zero. Called when a raid starts.</summary>
        internal static void StartRaidClock()
        {
            _raid.Reset();
            _raid.Start();
        }

        /// <summary>Stops the clock. Called when a raid ends.</summary>
        internal static void StopRaidClock()
        {
            _raid.Reset();
        }

        /// <summary>Seconds since the raid started, or -1 outside a raid.</summary>
        internal static double RaidSeconds => _raid.IsRunning ? _raid.Elapsed.TotalSeconds : -1;

        /// <summary>The bracket text every fork log line opens with; see the file header.</summary>
        internal static string Tag
        {
            get
            {
                if (!_raid.IsRunning)
                {
                    return ForkInfo.Label;
                }
                var tenths = (long)(_raid.Elapsed.TotalSeconds * 10);
                return $"{ForkInfo.Label} {tenths / 600:00}:{tenths % 600 / 10:00}.{tenths % 10}";
            }
        }
    }
}
