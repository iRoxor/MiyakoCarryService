// FollowTrail.cs -- how a carry bot keeps up with a player who is moving: when it starts to follow, how fast it
// goes, when it re-aims, when it may stop, and what it does when it stands still on the way.
//
// Since fork v15. One FollowTrail per carry bot, owned by BotForkState (C2Sain/BotForkState.cs, "Follow").
// Called by McsBrainLayer.GetNextAction (WantsWalk, BeginWalk, OnDecision), McsBaseLayer.EndFollowWalk (Tick, once a
// frame while a follow walk runs), GoToPointLogic.Update (ChoosePace, once a frame while the bot walks), the
// teleports (OnTeleport) and C2Sain/FollowWatch.cs (reads the stall report). Unity-free so the logic harness
// (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it with scripted positions and times. Reads and
// writes no files; its tunables are FollowSettings, whose defaults the plugin uses.
//
// Why: in the user's build 13 Interchange raid (2026-11-18, fork v13; miyako-carry-service/evidence/
// 2026-11-18-build-13-first-raid/keep-up-analysis.txt) the carry bot kept stopping and starting. Within the follow
// distance (8 m) it stood still; at 8 m it walked to ONE spot chosen when the walk began, 1.5-4 m from where the player
// stood at that moment, and never refreshed it, because BigBrain asks the layer for a new action only when the walk
// ends. It arrived 3-8 m behind a player who had walked on and stopped again: 33 entries into Mcs:TooFar in 9.8
// minutes of following (34 per 10 minutes; builds 7 to 11: 7.5-81). A player who crouches or walks with the speed
// turned down was followed at their own pace within 50 m with no sprint, and a prone player got speed 0, so the bot
// could not close any gap. After a teleport the bot stood 7 s in Mcs:TooClose, and 12 times it stood 5-28 s in
// Mcs:TooFar 30-146 m from the player; the log did not say why.
//
// What the rules do:
//   Start   -- a bot beyond the follow distance (FollowDistance, default 8 m) starts walking and keeps TRAILING.
//   Pace    -- beyond the follow distance it sprints whatever the player's pose or speed setting, and keeps doing
//              so until it is within the release distance; only then does it copy the player's pose and speed.
//   Re-aim  -- while it walks, its spot is replaced (about once a second at most) when it has gone stale, that is
//              farther than SpotMaxDistance from the player; a bot that has arrived at a stale spot gets a new one at once.
//   Stop    -- it stops only when it is within the stop distance and the player has been slower than MovingSpeed for
//              SlowSeconds (or it has arrived at a good spot while the player is slow). The player's speed is the larger
//              of the game's velocity reading and the speed measured from their position (UsePositionSpeed).
//   Stall   -- a bot that has not moved ProgressMetres in StallSeconds is re-pathed; after GiveUpStalls of them the
//              walk ends and starts again (what MCS's old 6 s exit did). A bot just teleported gets the shorter
//              TeleportStallSeconds, because it must start walking at once.

using System;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>The speed rule a following bot moves by (the "speed rule in force" of the stall log line).</summary>
    public enum FollowPace
    {
        /// <summary>Beyond the follow distance (until within the release distance): full speed, whatever the player's pose or speed setting.</summary>
        CatchUp,

        /// <summary>Close, and the player stands at the full speed setting: the game's default pace (sprints when 5 m or more from its spot).</summary>
        CopyFull,

        /// <summary>Close, and the player is crouched or walks with the speed turned down: the bot copies their pose and speed setting.</summary>
        CopySlow,

        /// <summary>Close, and the player is prone: the bot copies it, with target speed 0 (so a stand is deliberate, not a stall).</summary>
        CopyProne,
    }

    /// <summary>What <see cref="FollowTrail.Tick"/> asks the layer to do with a follow walk.</summary>
    public enum FollowStep
    {
        /// <summary>Keep walking to the current spot.</summary>
        Continue,

        /// <summary>Pick a new spot near the player where they are now, and walk to it.</summary>
        Retarget,

        /// <summary>The bot is standing still: pick a new spot and force the mover to start a new path.</summary>
        Repath,

        /// <summary>The bot is close and the player has slowed or stopped: end the walk (the bot holds).</summary>
        Stop,

        /// <summary>Re-paths did not help: end the walk so the layer starts it again from scratch (MCS's old 6 s exit).</summary>
        GiveUp,
    }

    /// <summary>How a stall that was reported ended.</summary>
    public enum StallEnd
    {
        /// <summary>The bot moved again, or reached its spot.</summary>
        Moved,

        /// <summary>The bot was teleported (MCS's own after 30 s, or the Force Teleport command).</summary>
        Teleported,

        /// <summary>The bot ended its walk because it was close and the player had slowed.</summary>
        Stopped,

        /// <summary>The follow ended without the bot moving (another layer took over, or the walk was not started again).</summary>
        Interrupted,
    }

    /// <summary>Tunables for <see cref="FollowTrail"/>; the plugin uses the defaults (FollowDistance is the one setting it reads).</summary>
    public sealed class FollowSettings
    {
        /// <summary>The bot is "too close" within this many metres of the player (McsBaseLayer.TOO_CLOSE_FROM_LEAD_DISTANCE); the stop rule then waits until it has stepped back to its spot.</summary>
        public float TooCloseMetres = 2f;

        /// <summary>The player counts as moving at or above this horizontal speed (m/s). A creeping player moves at 0.3 m/s or more.</summary>
        public float MovingSpeed = 0.25f;

        /// <summary>
        /// True also measures the player's speed from how far their position moved since the last sample (the larger of
        /// that and the game's velocity reading counts), so a velocity that reads zero in the game can never make a walking
        /// player look still. The harness cannot show what the game's velocity reads.
        /// </summary>
        public bool UsePositionSpeed = true;

        /// <summary>Seconds between position samples for <see cref="UsePositionSpeed"/>; shorter reacts sooner, longer averages out jitter.</summary>
        public float PositionSpeedWindow = 0.2f;

        /// <summary>A position sample older than this (s) is not used to measure speed: the bot was not watching in between.</summary>
        public float PositionSpeedMaxGap = 1f;

        /// <summary>The player must stay slower than <see cref="MovingSpeed"/> this long (s) before a close bot may stop, so a glance round does not stop it.</summary>
        public float SlowSeconds = 1f;

        /// <summary>The stop distance is this share of the follow distance, kept between StopMin and StopMax (m).</summary>
        public float StopShare = 0.55f;
        public float StopMin = 2.2f;
        public float StopMax = 4.5f;

        /// <summary>The distance at which a catching-up bot slows to the player's pace is this share of the follow distance, kept between ReleaseMin and ReleaseMax (m).</summary>
        public float ReleaseShare = 0.7f;
        public float ReleaseMin = 2.5f;
        public float ReleaseMax = 6.5f;

        /// <summary>A walk target farther than this from the player (m) is stale: the player has walked on. Follow spots lie 1.5-4 m from the player.</summary>
        public float SpotMaxDistance = 4.5f;

        /// <summary>Shortest time between two re-aims of a walk (s).</summary>
        public float RefreshSeconds = 1f;

        /// <summary>Shortest time between two re-aims after the bot has arrived at a stale spot (s).</summary>
        public float ArrivedRefreshSeconds = 0.25f;

        /// <summary>Seconds without progress before the walk is re-pathed (the old rule waited 6 s). Further re-paths follow every StallSeconds.</summary>
        public float StallSeconds = 2f;

        /// <summary>Metres the bot must move for that to count as progress again.</summary>
        public float ProgressMetres = 0.3f;

        /// <summary>After this many steps of StallSeconds without progress the walk gives up and restarts (3 x 2 s = the old 6 s).</summary>
        public int GiveUpStalls = 3;

        /// <summary>For this long after a teleport (s) the shorter <see cref="TeleportStallSeconds"/> applies: the bot must start walking at once.</summary>
        public float TeleportGraceSeconds = 4f;

        /// <summary>Seconds without progress before a re-path while in the teleport grace.</summary>
        public float TeleportStallSeconds = 1f;

        /// <summary>A stall is reported (one log line) once the bot has stood still this long (s).</summary>
        public float ReportStallSeconds = 3f;

        /// <summary>A walk that starts again within this many seconds of its last tick continues the same stall clock (s).</summary>
        public float RestartContinuitySeconds = 1.5f;

        /// <summary>False turns the whole v15 follow behaviour off (the "Follow: trail while you move" setting): the layer then keeps v14's rules.</summary>
        public bool Trail = true;

        /// <summary>False makes a far bot copy the player's pose and speed like v14 (a prone player then freezes it).</summary>
        public bool CatchUpSprint = true;
    }

    /// <summary>The three follow distances derived from the follow-distance setting; always Stop &lt; Release &lt; Start.</summary>
    public readonly struct FollowDistances
    {
        /// <summary>At or beyond this (m) a holding bot starts to follow and a bot starts to catch up: the follow-distance setting.</summary>
        public readonly float Start;

        /// <summary>A catching-up bot slows to the player's pace once within this (m).</summary>
        public readonly float Release;

        /// <summary>A trailing bot may stop within this (m) once the player has slowed.</summary>
        public readonly float Stop;

        /// <summary>Builds the three distances.</summary>
        public FollowDistances(float start, float release, float stop)
        {
            Start = start;
            Release = release;
            Stop = stop;
        }
    }

    /// <summary>Per-bot follow state; see the file header. Main thread only.</summary>
    public sealed class FollowTrail
    {
        // ---- walk state ----
        private float _lastRetarget;
        private float _lastTick = -1000f;

        // ---- the player's speed ----
        private bool _slow;
        private float _slowSince;
        private bool _hasLead;
        private V3 _leadAt;
        private float _leadTime;
        private float _positionSpeed;

        // ---- progress and stalls ----
        private bool _hasClock;
        private V3 _progressAt;
        private float _progressTime;
        private float _cycleStart;
        private int _cycleStep;
        private bool _reportPending;
        private bool _reported;
        private bool _endPending;
        private StallEnd _endHow;
        private float _endSeconds;
        private int _endRepaths;
        private int _endGiveUps;
        private float _teleportAt = -1000f;

        /// <summary>True while a follow walk runs: set by <see cref="BeginWalk"/>, cleared by <see cref="OnDecision"/> and <see cref="Reset"/>. McsBaseLayer.EndGoToPoint uses it to hand the walk to EndFollowWalk.</summary>
        public bool Active { get; private set; }

        /// <summary>True from the moment a bot beyond the follow distance starts walking until it stops (<see cref="Tick"/>, <see cref="WantsWalk"/>) or the follow is reset: the bot keeps walking while the player moves.</summary>
        public bool Trailing { get; private set; }

        /// <summary>The speed rule's hysteresis: on from the follow distance until the release distance (<see cref="ChoosePace"/>).</summary>
        public bool CatchingUp { get; private set; }

        /// <summary>The speed rule the last <see cref="ChoosePace"/> chose.</summary>
        public FollowPace Pace { get; private set; } = FollowPace.CopyFull;

        /// <summary>Game time of the last <see cref="ChoosePace"/> call; -1000 when none ran in this walk.</summary>
        public float PaceTime { get; private set; } = -1000f;

        /// <summary>False when the speed rule has the bot stand still on purpose (copying a prone player), so standing is not a stall.</summary>
        public bool ExpectsMotion { get; private set; } = true;

        /// <summary>Seconds the bot has stood still without being arrived or standing on purpose (0 while it moves).</summary>
        public float StalledSeconds { get; private set; }

        /// <summary>Re-paths made in the current stall (cleared when it ends).</summary>
        public int Repaths { get; private set; }

        /// <summary>Times the walk gave up in the current stall (cleared when it ends).</summary>
        public int GiveUps { get; private set; }

        /// <summary>The player's latest horizontal speed (m/s) the bot used: the game's velocity reading or the speed measured from their position, whichever is larger.</summary>
        public float LeadSpeed { get; private set; }

        /// <summary>
        /// Builds the three follow distances from the follow-distance setting: Start is the setting, Release and Stop
        /// are shares of it kept between their limits and ordered Stop &lt; Release &lt; Start with a margin.
        /// </summary>
        /// <param name="followDistance">The follow-distance setting (m).</param>
        /// <param name="settings">Tunables.</param>
        public static FollowDistances DistancesFor(float followDistance, FollowSettings settings)
        {
            var release = Math.Max(settings.ReleaseMin, Math.Min(settings.ReleaseMax, followDistance * settings.ReleaseShare));
            var stop = Math.Max(settings.StopMin, Math.Min(settings.StopMax, followDistance * settings.StopShare));
            release = Math.Min(release, followDistance - 0.5f);
            stop = Math.Min(stop, release - 0.3f);
            return new FollowDistances(followDistance, release, stop);
        }

        /// <summary>Forgets everything: the follow is over (another layer took over, or a new raid). Ends a reported stall as Interrupted.</summary>
        public void Reset()
        {
            CloseStall(StallEnd.Interrupted);
            Active = false;
            Trailing = false;
            CatchingUp = false;
            Pace = FollowPace.CopyFull;
            PaceTime = -1000f;
            ExpectsMotion = true;
            _hasClock = false;
            _slow = false;
            StalledSeconds = 0f;
            _cycleStep = 0;
        }

        /// <summary>The layer has begun choosing an action: no follow walk is running until <see cref="BeginWalk"/> says so.</summary>
        public void OnDecision()
        {
            Active = false;
        }

        /// <summary>
        /// The layer has chosen a follow walk (Mcs:TooFar or Mcs:TooClose). Marks it active, restarts its re-aim timer,
        /// and starts the stall clock, unless this walk restarts one that ended within RestartContinuitySeconds (a
        /// give-up), which keeps the same clock so one long stand is one stall.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="bot">The bot's position.</param>
        /// <param name="settings">Tunables.</param>
        public void BeginWalk(float now, V3 bot, FollowSettings settings)
        {
            Active = true;
            ExpectsMotion = true;
            PaceTime = -1000f;
            _lastRetarget = now;
            if (!_hasClock || now - _lastTick > settings.RestartContinuitySeconds)
            {
                CloseStall(StallEnd.Interrupted);
                StartClock(now, bot);
            }
            _lastTick = now;
        }

        /// <summary>
        /// The bot was teleported (MCS's 30 s teleport, or the Force Teleport command). Ends a reported stall as
        /// Teleported, forgets the progress clock and speed hysteresis, and starts the teleport grace.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        public void OnTeleport(float now)
        {
            CloseStall(StallEnd.Teleported);
            _teleportAt = now;
            _hasClock = false;
            CatchingUp = false;
            StalledSeconds = 0f;
            _cycleStep = 0;
        }

        /// <summary>True for TeleportGraceSeconds after <see cref="OnTeleport"/>: the layer starts the next walk on the navmesh route at once.</summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="settings">Tunables.</param>
        public bool InTeleportGrace(float now, FollowSettings settings)
        {
            return now - _teleportAt <= settings.TeleportGraceSeconds;
        }

        /// <summary>
        /// Records the player's horizontal speed: the game's velocity reading, or (UsePositionSpeed) the speed measured
        /// from how far their position moved over the last PositionSpeedWindow seconds when that is larger. The player
        /// counts as slow once they have been below MovingSpeed continuously (the clock restarts whenever they move).
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="speed">The player's horizontal speed (m/s) as the game reports it.</param>
        /// <param name="lead">The player's position.</param>
        /// <param name="settings">Tunables.</param>
        public void ObservePlayer(float now, float speed, V3 lead, FollowSettings settings)
        {
            if (settings.UsePositionSpeed)
            {
                var elapsed = now - _leadTime;
                if (!_hasLead || elapsed < 0f || elapsed > settings.PositionSpeedMaxGap)
                {
                    // First sample, a clock that went back, or a gap in which nobody was watching: start over.
                    _hasLead = true;
                    _leadAt = lead;
                    _leadTime = now;
                    _positionSpeed = 0f;
                }
                else if (elapsed >= settings.PositionSpeedWindow)
                {
                    var dx = lead.X - _leadAt.X;
                    var dz = lead.Z - _leadAt.Z;
                    _positionSpeed = (float)Math.Sqrt(dx * dx + dz * dz) / elapsed;
                    _leadAt = lead;
                    _leadTime = now;
                }
                speed = Math.Max(speed, _positionSpeed);
            }
            LeadSpeed = speed;
            if (speed >= settings.MovingSpeed)
            {
                _slow = false;
            }
            else if (!_slow)
            {
                _slow = true;
                _slowSince = now;
            }
        }

        /// <summary>True when the player has been slower than MovingSpeed for SlowSeconds (from the last <see cref="ObservePlayer"/>).</summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="settings">Tunables.</param>
        public bool PlayerSlow(float now, FollowSettings settings)
        {
            return _slow && now - _slowSince >= settings.SlowSeconds;
        }

        /// <summary>
        /// Whether the bot should be walking toward the player (the follow branch of McsBrainLayer.GetNextAction).
        /// A bot beyond the follow distance walks and starts trailing; a bot too close steps back; a trailing bot
        /// keeps walking until it is within the stop distance and the player has slowed; anything else holds.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="lead">The player's position.</param>
        /// <param name="distance">Metres from the bot to the player.</param>
        /// <param name="beyond">McsBaseLayer.IsBeyondFollowDistance: the bot is at or beyond the follow distance.</param>
        /// <param name="tooClose">The bot is within 2 m of the player.</param>
        /// <param name="leadSpeed">The player's horizontal speed (m/s) as the game reports it.</param>
        /// <param name="followDistance">The follow-distance setting (m).</param>
        /// <param name="settings">Tunables.</param>
        public bool WantsWalk(float now, V3 lead, float distance, bool beyond, bool tooClose, float leadSpeed, float followDistance, FollowSettings settings)
        {
            ObservePlayer(now, leadSpeed, lead, settings);
            if (beyond)
            {
                Trailing = settings.Trail;
                return true;
            }
            if (tooClose)
            {
                return true;
            }
            if (Trailing)
            {
                var distances = DistancesFor(followDistance, settings);
                if (distance <= distances.Stop && PlayerSlow(now, settings))
                {
                    Trailing = false;
                    CatchingUp = false;
                    return false;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Chooses the speed rule for this frame of a walk, with hysteresis: catch-up starts at the follow distance and
        /// lasts until the release distance; otherwise the bot copies the player. Sets <see cref="ExpectsMotion"/>
        /// (false only while copying a prone player) and remembers the choice for the stall log.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="distance">Metres from the bot to the player.</param>
        /// <param name="followDistance">The follow-distance setting (m).</param>
        /// <param name="prone">The player is prone.</param>
        /// <param name="poseLevel">The player's pose level (1 standing, lower crouched).</param>
        /// <param name="relativeSpeed">The player's speed setting as a share of their maximum (1 = full).</param>
        /// <param name="settings">Tunables.</param>
        public FollowPace ChoosePace(float now, float distance, float followDistance, bool prone, float poseLevel, float relativeSpeed, FollowSettings settings)
        {
            var distances = DistancesFor(followDistance, settings);
            if (!settings.CatchUpSprint)
            {
                CatchingUp = false;
            }
            else if (CatchingUp)
            {
                if (distance <= distances.Release)
                {
                    CatchingUp = false;
                }
            }
            else if (distance >= distances.Start)
            {
                CatchingUp = true;
            }

            ExpectsMotion = true;
            if (CatchingUp)
            {
                Pace = FollowPace.CatchUp;
            }
            else if (prone)
            {
                Pace = FollowPace.CopyProne;
                ExpectsMotion = false;
            }
            else if (poseLevel < 1f || relativeSpeed < 1f)
            {
                Pace = FollowPace.CopySlow;
            }
            else
            {
                Pace = FollowPace.CopyFull;
            }
            PaceTime = now;
            return Pace;
        }

        /// <summary>
        /// One frame of a follow walk (McsBaseLayer.EndFollowWalk). Decides, in this order: stop (close and the
        /// player slowed), stall escalation (re-path, give up), re-aim (the spot is stale), otherwise continue. Also
        /// keeps the stall clock and the stall report.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="bot">The bot's position.</param>
        /// <param name="lead">The player's position.</param>
        /// <param name="leadSpeed">The player's horizontal speed (m/s) as the game reports it.</param>
        /// <param name="arrived">The bot is at its walk target (BotGoToPointData.IsCome).</param>
        /// <param name="spotToLead">Metres from the walk target to the player; float.PositiveInfinity when there is no target.</param>
        /// <param name="followDistance">The follow-distance setting (m).</param>
        /// <param name="settings">Tunables.</param>
        public FollowStep Tick(float now, V3 bot, V3 lead, float leadSpeed, bool arrived, float spotToLead, float followDistance, FollowSettings settings)
        {
            ObservePlayer(now, leadSpeed, lead, settings);
            var distance = V3.Distance(bot, lead);
            var distances = DistancesFor(followDistance, settings);
            if (!_hasClock)
            {
                StartClock(now, bot);
            }
            _lastTick = now;

            // Close enough and the player slowed: stop. Within TooCloseMetres the bot first steps back to its spot
            // (arriving at a good spot also stops it), or it would end and restart the walk every frame.
            var slow = PlayerSlow(now, settings);
            var goodSpot = spotToLead <= settings.SpotMaxDistance;
            var close = distance <= distances.Stop && distance > settings.TooCloseMetres;
            if (slow && (close || (arrived && goodSpot)))
            {
                CloseStall(StallEnd.Stopped);
                Trailing = false;
                CatchingUp = false;
                _hasClock = false;
                return FollowStep.Stop;
            }

            if (V3.Distance(bot, _progressAt) >= settings.ProgressMetres)
            {
                CloseStall(StallEnd.Moved);
                StartClock(now, bot);
            }
            else if (!ExpectsMotion || (arrived && goodSpot))
            {
                // Standing on purpose, or at a good spot: nothing to wait for.
                CloseStall(StallEnd.Moved);
                StartClock(now, bot);
            }

            StalledSeconds = now - _progressTime;
            if (StalledSeconds >= settings.ReportStallSeconds && !_reported)
            {
                _reported = true;
                _reportPending = true;
            }

            var limit = InTeleportGrace(now, settings) ? settings.TeleportStallSeconds : settings.StallSeconds;
            var step = limit > 0f ? (int)((now - _cycleStart) / limit) : 0;
            if (step > _cycleStep)
            {
                _cycleStep = step;
                if (step >= settings.GiveUpStalls)
                {
                    GiveUps++;
                    _cycleStart = now;
                    _cycleStep = 0;
                    return FollowStep.GiveUp;
                }
                Repaths++;
                _lastRetarget = now;
                return FollowStep.Repath;
            }

            var minimum = arrived ? settings.ArrivedRefreshSeconds : settings.RefreshSeconds;
            if (!goodSpot && now - _lastRetarget >= minimum)
            {
                _lastRetarget = now;
                return FollowStep.Retarget;
            }
            return FollowStep.Continue;
        }

        /// <summary>
        /// True once per stall, when the bot has stood still for ReportStallSeconds: the layer then writes the
        /// stall line (C2Sain/FollowWatch.cs). Clears the pending flag.
        /// </summary>
        /// <param name="seconds">How long the bot has stood still.</param>
        public bool TakeStallReport(out float seconds)
        {
            seconds = StalledSeconds;
            var due = _reportPending;
            _reportPending = false;
            return due;
        }

        /// <summary>
        /// True once when a stall that was reported has ended: the layer then writes the closing line. Clears the pending flag.
        /// </summary>
        /// <param name="how">How it ended.</param>
        /// <param name="seconds">How long the bot stood still.</param>
        /// <param name="repaths">Re-paths made during the stall.</param>
        /// <param name="giveUps">Times the walk gave up during the stall.</param>
        public bool TakeStallEnd(out StallEnd how, out float seconds, out int repaths, out int giveUps)
        {
            how = _endHow;
            seconds = _endSeconds;
            repaths = _endRepaths;
            giveUps = _endGiveUps;
            var due = _endPending;
            _endPending = false;
            return due;
        }

        /// <summary>Starts the progress clock at the bot's position and the current time, and forgets any unreported stall.</summary>
        private void StartClock(float now, V3 bot)
        {
            _hasClock = true;
            _progressAt = bot;
            _progressTime = now;
            _cycleStart = now;
            _cycleStep = 0;
            StalledSeconds = 0f;
            Repaths = 0;
            GiveUps = 0;
        }

        /// <summary>When a stall had been reported, queues its closing line (how, how long, how many re-paths) and re-arms the report.</summary>
        private void CloseStall(StallEnd how)
        {
            if (_reported)
            {
                _endPending = true;
                _endHow = how;
                _endSeconds = StalledSeconds;
                _endRepaths = Repaths;
                _endGiveUps = GiveUps;
            }
            _reported = false;
            _reportPending = false;
        }
    }
}
