// ArrivalWatch.cs -- whether a carry bot walking to a spot has got there, or has stalled on the way.
//
// Two instances per carry bot, owned by BotForkState: McsGuardLayer updates Arrival with the bot's
// guard or Defend spot each time it picks the bot's next action (about twice a second), and
// GuardMoveLogic updates MoveProgress with its walking target every frame. Unity-free so the logic
// harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it with scripted positions and
// times. Reads and writes no files.
//
// A bot has arrived when it is within AtSpotMetres of its spot, or when it is within NearSpotMetres
// and has stalled: it moved less than ProgressMetres in StallSeconds. Fork v3-v6 used only the first
// rule, with 1.25 m, while the game's walking node ends a walk at its own reach distance
// (BotGoToPointData.IsCome against Move.REACH_DIST). A bot that physically stopped short, at a door
// frame, furniture or the player in a doorway, then stayed in the walk step for good, looking where
// it last walked. In the user's build-6 raid (2026-11-05) a carry bot stared at 198 deg for 12 s
// (04:10.8-04:22.8) while a Reshala guard walked behind it, and later stood 24 s in
// C2Sain:ToGuardSpot 1.3 m from its post; build 4's raids had a 2.0 m case.

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Tunables for <see cref="ArrivalWatch"/>; the plugin uses the defaults.</summary>
    public sealed class ArrivalSettings
    {
        /// <summary>Metres from the spot at which a bot has arrived, moving or not (fork v3-v6's only rule).</summary>
        public float AtSpotMetres = 1.25f;

        /// <summary>Metres from the spot within which a stalled bot also counts as arrived.</summary>
        public float NearSpotMetres = 3f;

        /// <summary>Seconds without progress after which a bot counts as stalled.</summary>
        public float StallSeconds = 2f;

        /// <summary>Metres a bot must cover since its last progress for that to count as progress again.</summary>
        public float ProgressMetres = 0.3f;

        /// <summary>Metres a spot may shift before it counts as a new spot, which starts the watch over.</summary>
        public float NewSpotMetres = 0.5f;
    }

    /// <summary>Per-bot arrival and stall state for one walk at a time. Main thread only.</summary>
    public sealed class ArrivalWatch
    {
        private V3 _spot;
        private V3 _progressAt;
        private float _progressTime;
        private bool _hasSpot;

        /// <summary>True when the bot has made no progress for StallSeconds (from the last <see cref="Update"/>).</summary>
        public bool Stalled { get; private set; }

        /// <summary>The last <see cref="Update"/>'s verdict.</summary>
        public bool Arrived { get; private set; }

        /// <summary>True when the last <see cref="Update"/> counted the bot as arrived only because it stalled near the spot.</summary>
        public bool ArrivedByStall { get; private set; }

        /// <summary>Metres from the spot at the last <see cref="Update"/>.</summary>
        public float Distance { get; private set; }

        /// <summary>
        /// Records where the bot is and returns whether it has arrived; see the file header. A spot more
        /// than NewSpotMetres from the previous one starts the progress clock over.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="bot">The bot's position.</param>
        /// <param name="spot">Where it is walking to.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        public bool Update(float now, V3 bot, V3 spot, ArrivalSettings settings)
        {
            if (!_hasSpot || V3.Distance(spot, _spot) > settings.NewSpotMetres)
            {
                _hasSpot = true;
                _spot = spot;
                _progressAt = bot;
                _progressTime = now;
            }
            else if (V3.Distance(bot, _progressAt) >= settings.ProgressMetres)
            {
                _progressAt = bot;
                _progressTime = now;
            }
            Stalled = now - _progressTime >= settings.StallSeconds;
            Distance = V3.Distance(bot, spot);
            var close = Distance <= settings.AtSpotMetres;
            ArrivedByStall = !close && Stalled && Distance <= settings.NearSpotMetres;
            Arrived = close || ArrivedByStall;
            return Arrived;
        }

        /// <summary>Forgets the spot, so the next <see cref="Update"/> starts a new walk.</summary>
        public void Clear()
        {
            _hasSpot = false;
            Stalled = false;
            Arrived = false;
            ArrivedByStall = false;
        }
    }
}
