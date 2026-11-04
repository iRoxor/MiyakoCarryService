// EngageOrder.cs -- one carry bot's Engage Enemy order: how long it lasts and its flank leg.
//
// One instance per carry bot, owned by BotForkState (C2Sain/BotForkState.cs).
// The Engage Enemy commands begin it, CommandHooks cancels it when any other
// order arrives, and EngageTactics ticks it from SAIN's decision prefix about
// ten times a second. Unity-free so the logic harness
// (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it with
// scripted times. Reads and writes no files.
//
// An order ends when:
//   - no enemy has been known for LostContactSeconds (contact lost or enemy dead),
//   - it has run MaxSeconds,
//   - another order cancels it.
// A tactical order may also run flank legs: the bot walks to a point off to the
// side of the enemy's last known position, and SAIN takes over from there. A leg
// ends on arrival, when the enemy comes into view, when the bot is shot at, or
// after FlankTimeoutSeconds. One leg is run per enemy position; a new leg needs
// the enemy's known position to move by FlankReplanDistance.

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Which Engage Enemy order a bot is carrying out.</summary>
    public enum EngageKind
    {
        None,
        /// <summary>Engage Enemy: SAIN's Chad personality, flanking and covering fire.</summary>
        Tactical,
        /// <summary>Engage Enemy Wrecklessly: SAIN's GigaChad or Wreckless personality, straight at them.</summary>
        Reckless,
    }

    /// <summary>Why an order ended, for the reply and the overlay.</summary>
    public enum EngageEnd
    {
        None,
        LostContact,
        TimedOut,
        Cancelled,
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

    /// <summary>Tunables for <see cref="EngageOrder"/>; the plugin copies them from its settings.</summary>
    public sealed class EngageSettings
    {
        /// <summary>Seconds an order lasts at most.</summary>
        public float MaxSeconds = 90f;

        /// <summary>Seconds without a known enemy before the order ends.</summary>
        public float LostContactSeconds = 8f;

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
        private float _lastContactTime;
        private bool _flankPlanned;

        /// <summary>The order being carried out; None when there is none.</summary>
        public EngageKind Kind { get; private set; }

        /// <summary>True while an order is in force.</summary>
        public bool Active => Kind != EngageKind.None;

        /// <summary>Game time the current order began.</summary>
        public float StartTime { get; private set; }

        /// <summary>How the most recent order ended (None while one is running or none has ended).</summary>
        public EngageEnd LastEnd { get; private set; }

        /// <summary>The kind of the order that ended most recently, so its personality can be restored.</summary>
        public EngageKind LastEndedKind { get; private set; }

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
        /// Starts an order, replacing any running one. Side effect: clears the flank state.
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
            _lastContactTime = now;
            LastEnd = EngageEnd.None;
            ResetFlank();
        }

        /// <summary>Ends the running order as cancelled. Returns true if one was running.</summary>
        public bool Cancel()
        {
            if (!Active)
            {
                return false;
            }
            End(EngageEnd.Cancelled);
            return true;
        }

        /// <summary>
        /// Advances the order. Returns the reason it ended on this call, or None while it continues
        /// (or when no order is running).
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="hasContact">True while the bot knows of an enemy worth engaging.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        public EngageEnd Tick(float now, bool hasContact, EngageSettings settings)
        {
            if (!Active)
            {
                return EngageEnd.None;
            }
            if (hasContact)
            {
                _lastContactTime = now;
            }
            if (now - StartTime > settings.MaxSeconds)
            {
                End(EngageEnd.TimedOut);
                return EngageEnd.TimedOut;
            }
            if (now - _lastContactTime > settings.LostContactSeconds)
            {
                End(EngageEnd.LostContact);
                return EngageEnd.LostContact;
            }
            return EngageEnd.None;
        }

        /// <summary>
        /// True when a tactical order should plan a flank leg against this enemy position: no leg is
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

        /// <summary>Ends the order with the given reason and clears the flank state.</summary>
        private void End(EngageEnd reason)
        {
            LastEndedKind = Kind;
            Kind = EngageKind.None;
            LastEnd = reason;
            ResetFlank();
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
