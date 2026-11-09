// OffMesh.cs -- the rules for a player standing off the navmesh, where carry bots cannot path to them.
//
// Since fork v10. Two users:
//   - C2Nav.TryClosestReachable (called by McsBaseLayer.UpdateLeadNearMoveTarget when the normal
//     follow finds no path) asks OffMeshRules.WorthMoving whether the closest spot a bot can reach is
//     worth walking to;
//   - C2Sain/OffMeshWatch.cs samples each player about once a second and keeps one OffMeshTracker per
//     player, which says when to log that the player has left the navmesh and when they are back.
// Unity-free so the logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it with
// scripted samples. Reads and writes no files.
//
// Why: on 2026-11-09 Infil spawned the player at Lighthouse point #13 ("Tunnel"). The three carry bots
// started 30 m away on one spot (every earlier raid logged 0 m), never moved for the whole raid, and
// all 13 guard plans found 0 posts. Both fit the player being off the navmesh: MCS only sets a follow
// target when it finds a path to the player, and the guard planner needs the player within 1.5 m of
// the navmesh. Evidence: miyako-carry-service/evidence/2026-11-09-build-9-stuck-spawn. That reading is
// inferred from the log, not proven; this file's watch exists to confirm or refute it next time.

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Tunables for the off-navmesh fallback and watch.</summary>
    public sealed class OffMeshSettings
    {
        /// <summary>
        /// The player counts as on the navmesh when a navmesh point lies within this (m). Matches the
        /// guard planner's test (C2Nav.IsNearPlayerOnFoot, IsInPlayersSpace), so the watch says "off"
        /// exactly when the planner cannot plan.
        /// </summary>
        public float OnMeshRadius = 1.5f;

        /// <summary>
        /// Radii (m) searched in turn for the walkable point nearest the player, smallest first, so the
        /// common case (a few metres off) costs one small query. The largest is the farthest a bot will
        /// be sent to wait; 32 m covers the 30 m the bots stood from the player in the 2026-11-09 raid.
        /// </summary>
        public float[] SearchRadii = { 4f, 8f, 16f, 32f };

        /// <summary>
        /// A fallback spot is worth walking to only if it is at least this much (m) closer to the player
        /// than the bot already is. Stops a bot already at the closest reachable spot from re-walking it.
        /// </summary>
        public float MinGain = 2f;

        /// <summary>
        /// Seconds the player must stay off the navmesh before the watch logs it. Jumps, vaults and short
        /// falls leave the navmesh for well under a second; 3 s keeps them out of the log.
        /// </summary>
        public float ReportAfterSeconds = 3f;

        /// <summary>Longest gap (s) between samples that still continues an episode.</summary>
        public float MaxSampleGap = 3f;
    }

    /// <summary>Pure decisions for the off-navmesh fallback.</summary>
    public static class OffMeshRules
    {
        /// <summary>
        /// Whether a bot should walk to the end of its fallback path: only when that end is at least
        /// MinGain closer to the player than the bot is now. Never sends a bot farther away.
        /// </summary>
        /// <param name="botToPlayer">The bot's distance from the player (m).</param>
        /// <param name="endToPlayer">The fallback path end's distance from the player (m).</param>
        public static bool WorthMoving(float botToPlayer, float endToPlayer, OffMeshSettings settings)
        {
            return endToPlayer <= botToPlayer - settings.MinGain;
        }
    }

    /// <summary>What one sample changed.</summary>
    public enum OffMeshEvent
    {
        None,
        WentOff,
        CameBack,
        Lost,
    }

    /// <summary>
    /// One player's off-navmesh episodes. An episode starts at the first sample off the navmesh (dated
    /// from the previous sample when that one was off too) and is reported once it lasts
    /// ReportAfterSeconds. It ends at the first sample back on: CameBack if it was reported, silently
    /// otherwise. A gap longer than MaxSampleGap ends it (Lost if reported), because nothing is known
    /// about the time in between. Not thread-safe; main thread only.
    /// </summary>
    public sealed class OffMeshTracker
    {
        private bool _hasLast;
        private float _lastTime;
        private bool _lastOff;
        private float _start = -1f;
        private float _lastOffTime;

        /// <summary>True once the current episode has been reported (WentOff returned).</summary>
        public bool Reported { get; private set; }

        /// <summary>True when the latest sample found the player off the navmesh.</summary>
        public bool Off { get; private set; }

        /// <summary>Length (s) of the current episode, or of the one that just ended.</summary>
        public float Seconds { get; private set; }

        /// <summary>
        /// Distance (m) to the nearest walkable point at the latest off sample; float.PositiveInfinity
        /// when none was found within the largest search radius.
        /// </summary>
        public float Nearest { get; private set; }

        /// <summary>The largest Nearest seen in the current episode, or in the one that just ended.</summary>
        public float Farthest { get; private set; }

        /// <summary>
        /// Takes one sample and says whether a reported episode started or ended with it.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="onMesh">True when a navmesh point lies within OnMeshRadius of the player.</param>
        /// <param name="nearest">Distance (m) to the nearest walkable point; ignored when on the mesh;
        /// float.PositiveInfinity when none was found.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        public OffMeshEvent Sample(float now, bool onMesh, float nearest, OffMeshSettings settings)
        {
            var continuous = _hasLast && now - _lastTime <= settings.MaxSampleGap;
            var previousTime = _lastTime;
            var previousOff = _lastOff;
            _hasLast = true;
            _lastTime = now;
            _lastOff = !onMesh;

            if (!continuous && _start >= 0f)
            {
                var wasReported = Reported;
                Seconds = _lastOffTime - _start;
                _start = -1f;
                Reported = false;
                Off = false;
                if (wasReported)
                {
                    // The gap ends the old episode; this sample starts afresh on the next call.
                    _lastOff = false;
                    return OffMeshEvent.Lost;
                }
            }

            if (!onMesh)
            {
                if (_start < 0f)
                {
                    _start = continuous && previousOff ? previousTime : now;
                    Farthest = 0f;
                }
                Off = true;
                Nearest = nearest;
                if (nearest > Farthest)
                {
                    Farthest = nearest;
                }
                _lastOffTime = now;
                Seconds = now - _start;
                if (!Reported && Seconds >= settings.ReportAfterSeconds)
                {
                    Reported = true;
                    return OffMeshEvent.WentOff;
                }
                return OffMeshEvent.None;
            }

            Off = false;
            var reported = Reported;
            if (_start >= 0f)
            {
                Seconds = _lastOffTime - _start;
            }
            _start = -1f;
            Reported = false;
            return reported ? OffMeshEvent.CameBack : OffMeshEvent.None;
        }
    }
}
