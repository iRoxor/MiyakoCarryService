// EngageOrder.cs -- one carry bot's aggressive tactics: which kind is in force, and its flank leg.
//
// One instance per carry bot, owned by BotForkState (C2Sain/BotForkState.cs). Since fork v11 the
// bot's stance drives it: EngageTactics begins it when the stance is Aggressive (Tactical) or
// Hyper-aggressive (Reckless) and cancels it when the stance changes to one without tactics. Before
// v11 the Engage Enemy commands began it and it ended after 90 s or 8 s without contact; a stance
// has no such end, so those rules (Tick, MaxSeconds, LostContactSeconds) were removed. Unity-free so
// the logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it with scripted
// times. Reads and writes no files.
//
// The tactical kind may also run flank legs: the bot walks to a point off to the
// side of the enemy's last known position, and SAIN takes over from there. A leg
// ends on arrival, when the enemy comes into view, when the bot is shot at, or
// after FlankTimeoutSeconds. One leg is run per enemy position; a new leg needs
// the enemy's known position to move by FlankReplanDistance.

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Which aggressive tactics a bot is carrying out (Core/Stance.cs, StanceRules.Tactics).</summary>
    public enum EngageKind
    {
        None,
        /// <summary>Aggressive stance (the old Engage Enemy): flanking and covering fire.</summary>
        Tactical,
        /// <summary>Hyper-aggressive stance (the old Engage Enemy Wrecklessly): straight at them.</summary>
        Reckless,
    }

    /// <summary>Why a flank leg ended.</summary>
    public enum FlankEnd
    {
        None,
        Arrived,
        EnemySpotted,
        UnderFire,
        TimedOut,
    }

    /// <summary>Tunables for <see cref="EngageOrder"/>.</summary>
    public sealed class EngageSettings
    {
        /// <summary>Seconds a flank leg may take before it is abandoned.</summary>
        public float FlankTimeoutSeconds = 15f;

        /// <summary>Metres from the flank point that count as arrived.</summary>
        public float FlankArriveRadius = 2.5f;

        /// <summary>Metres the enemy's known position must move before another leg is planned.</summary>
        public float FlankReplanDistance = 10f;
    }

    /// <summary>Per-bot order state. Main thread only.</summary>
    public sealed class EngageOrder
    {
        private bool _flankPlanned;

        /// <summary>The tactics in force; None when there are none.</summary>
        public EngageKind Kind { get; private set; }

        /// <summary>True while tactics are in force.</summary>
        public bool Active => Kind != EngageKind.None;

        /// <summary>Game time the current tactics began.</summary>
        public float StartTime { get; private set; }

        /// <summary>True while a flank leg is being walked.</summary>
        public bool FlankActive { get; private set; }

        /// <summary>Where the current or last flank leg leads.</summary>
        public V3 FlankPoint { get; private set; }

        /// <summary>The enemy position the current or last leg was planned against.</summary>
        public V3 FlankAnchor { get; private set; }

        /// <summary>+1 or -1: which side of the enemy the leg goes round.</summary>
        public int FlankSide { get; private set; }

        /// <summary>Game time the current leg began.</summary>
        public float FlankStartTime { get; private set; }

        /// <summary>How the most recent leg ended.</summary>
        public FlankEnd LastFlankEnd { get; private set; }

        /// <summary>
        /// Starts tactics, replacing any running ones. Side effect: clears the flank state.
        /// </summary>
        /// <param name="kind">Tactical or Reckless; None is ignored.</param>
        /// <param name="now">Game time in seconds.</param>
        public void Begin(EngageKind kind, float now)
        {
            if (kind == EngageKind.None)
            {
                return;
            }
            Kind = kind;
            StartTime = now;
            ResetFlank();
        }

        /// <summary>Ends the running tactics and clears the flank state. Returns true if any were running.</summary>
        public bool Cancel()
        {
            if (!Active)
            {
                return false;
            }
            Kind = EngageKind.None;
            ResetFlank();
            return true;
        }

        /// <summary>
        /// True when the tactical kind should plan a flank leg against this enemy position: no leg is
        /// running, and none has been run since the enemy was last this close to <paramref name="anchor"/>.
        /// </summary>
        public bool ShouldPlanFlank(V3 anchor, EngageSettings settings)
        {
            if (Kind != EngageKind.Tactical || FlankActive)
            {
                return false;
            }
            return !_flankPlanned || V3.Distance(anchor, FlankAnchor) > settings.FlankReplanDistance;
        }

        /// <summary>
        /// Records that a leg was considered for this enemy position but could not be planned (no
        /// usable point), so planning is not retried until the enemy moves.
        /// </summary>
        public void MarkFlankUnavailable(V3 anchor)
        {
            _flankPlanned = true;
            FlankAnchor = anchor;
            LastFlankEnd = FlankEnd.None;
        }

        /// <summary>Starts a flank leg toward <paramref name="point"/>.</summary>
        /// <param name="point">The NavMesh point the leg leads to.</param>
        /// <param name="anchor">The enemy position the leg was planned against.</param>
        /// <param name="side">+1 or -1, recorded for the overlay and for alternating sides.</param>
        /// <param name="now">Game time in seconds.</param>
        public void BeginFlank(V3 point, V3 anchor, int side, float now)
        {
            FlankActive = true;
            _flankPlanned = true;
            FlankPoint = point;
            FlankAnchor = anchor;
            FlankSide = side;
            FlankStartTime = now;
            LastFlankEnd = FlankEnd.None;
        }

        /// <summary>
        /// Advances a running flank leg. Returns why it ended on this call, or None while it continues
        /// (or when no leg is running).
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="distanceToPoint">Metres from the bot to <see cref="FlankPoint"/>.</param>
        /// <param name="enemyVisible">True when the bot can see its enemy.</param>
        /// <param name="underFire">True when the bot was hit or shot at just now.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        public FlankEnd UpdateFlank(float now, float distanceToPoint, bool enemyVisible, bool underFire, EngageSettings settings)
        {
            if (!FlankActive)
            {
                return FlankEnd.None;
            }
            var end = FlankEnd.None;
            if (enemyVisible)
            {
                end = FlankEnd.EnemySpotted;
            }
            else if (underFire)
            {
                end = FlankEnd.UnderFire;
            }
            else if (distanceToPoint <= settings.FlankArriveRadius)
            {
                end = FlankEnd.Arrived;
            }
            else if (now - FlankStartTime > settings.FlankTimeoutSeconds)
            {
                end = FlankEnd.TimedOut;
            }
            if (end != FlankEnd.None)
            {
                FlankActive = false;
                LastFlankEnd = end;
            }
            return end;
        }

        /// <summary>Forgets any flank leg, planned or running.</summary>
        private void ResetFlank()
        {
            FlankActive = false;
            _flankPlanned = false;
            FlankSide = 0;
            LastFlankEnd = FlankEnd.None;
        }
    }
}
