// TrailGate.cs -- how often a walking carry bot writes its "trail:" diagnostic line, and the speeds on it. Fork v17.
//
// C2Sain/FollowWatch.cs (LogTrail) asks it once a frame, from McsBaseLayer.EndGoToPoint, while a bot walks to the player on a
// follow walk (Mcs:TooFar) or a recall walk (C2Sain:BreakOff); it answers true about every Interval seconds while the bot is
// MinDistance or more from the player, and hands back the speeds over the seconds since its last answer so the line can say
// whether the bot or the player was the one that changed. One TrailGate per carry bot, held in C2Sain/BotForkState.cs.
// Unity-free so the logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it. Reads and writes no files.
//
// Why: in the user's build 16 raid (Customs raid 2, 2026-11-24, 16:38.1-16:55.7) a bot sprinting toward a spot 11.5 m ahead of it
// fell from 16 m to 40 m behind in 17.6 s while its squadmate closed from 11 m to 2 m, and no log line fell between those two
// moments (evidence/2026-11-24-build-16-customs-factory/build-16-analysis.txt, 5d). A line every 2 s shows which of the two
// speeds, the spot or the path was at fault the next time.

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Tunables for <see cref="TrailGate"/>.</summary>
    public sealed class TrailSettings
    {
        /// <summary>Seconds between two trail lines for one bot.</summary>
        public float Interval = 2f;

        /// <summary>A line is written only while the bot is at least this far (m) from the player.</summary>
        public float MinDistance = 12f;

        /// <summary>A silence longer than this many intervals (the walk ended, or the bot was not watched) starts the bot's measurement over.</summary>
        public float MaxGapIntervals = 3f;
    }

    /// <summary>Per-bot trail line timer; see the file header. Main-thread only.</summary>
    public sealed class TrailGate
    {
        private bool _active;
        private float _nextAt;
        private float _lastTime;
        private V3 _lastBot;
        private V3 _lastLead;

        /// <summary>Forgets the measurement (the walk ended, or a new raid).</summary>
        public void Reset()
        {
            _active = false;
        }

        /// <summary>
        /// Says whether a trail line is due now. The first call of a walk only starts the measurement, so the first line comes
        /// Interval seconds into the walk, with real speeds.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="walking">True while the bot walks to the player (a follow walk or a recall walk).</param>
        /// <param name="botToPlayer">Metres from the bot to the player.</param>
        /// <param name="bot">The bot's position.</param>
        /// <param name="lead">The player's position.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        /// <param name="seconds">When true: the seconds the speeds below were measured over.</param>
        /// <param name="botSpeed">When true: the bot's average speed over those seconds (m/s, over the ground).</param>
        /// <param name="leadSpeed">When true: the player's average speed over those seconds (m/s, over the ground).</param>
        public bool Due(float now, bool walking, float botToPlayer, V3 bot, V3 lead, TrailSettings settings,
            out float seconds, out float botSpeed, out float leadSpeed)
        {
            seconds = 0f;
            botSpeed = 0f;
            leadSpeed = 0f;
            if (!walking || botToPlayer < settings.MinDistance || (_active && now - _lastTime > settings.Interval * settings.MaxGapIntervals))
            {
                _active = false;
                if (!walking || botToPlayer < settings.MinDistance)
                {
                    return false;
                }
            }
            if (!_active)
            {
                _active = true;
                _nextAt = now + settings.Interval;
                _lastTime = now;
                _lastBot = bot;
                _lastLead = lead;
                return false;
            }
            if (now < _nextAt)
            {
                return false;
            }
            seconds = now - _lastTime;
            if (seconds > 0f)
            {
                botSpeed = Horizontal(bot, _lastBot) / seconds;
                leadSpeed = Horizontal(lead, _lastLead) / seconds;
            }
            _nextAt = now + settings.Interval;
            _lastTime = now;
            _lastBot = bot;
            _lastLead = lead;
            return true;
        }

        /// <summary>Horizontal distance between two positions (m).</summary>
        private static float Horizontal(V3 a, V3 b)
        {
            var dx = a.X - b.X;
            var dz = a.Z - b.Z;
            return (float)System.Math.Sqrt(dx * dx + dz * dz);
        }
    }
}
