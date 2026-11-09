// Stance.cs -- the four combat stances (fork v11): what each one allows, and Passive's hold-fire gate.
//
// The stance replaces the Engage Enemy orders and the allowed-personalities setting (the user's spec,
// 2026-11-08, with their answers of 2026-11-09). Callers:
//   - ForkConfig.Stance holds the squad stance; the Team ▸ Combat ▸ Stance commands write it, so the
//     last squad stance carries over to the next raid. BotForkState.StanceOverride holds a stance set
//     from one bot's own menu, for that raid only (carry bots expire and are rebuilt, so nothing is
//     stored against one). StanceRules.Effective combines the two.
//   - DecisionPolicy asks what the stance allows on every SAIN decision; EngageTactics lends the
//     stance's personality and runs the Aggressive and Hyper-aggressive tactics; SainGrenadePatch
//     blocks SAIN's grenade throws where the stance forbids them.
//   - TeamModes keeps one PassiveGate per squad and ticks it twice a second.
// Unity-free so the logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it.
// Reads and writes no files.

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>
    /// A carry bot's combat stance. The names are also the values of the "Stance" BepInEx setting, so
    /// they must never be renamed (a renamed value would reset everyone's saved stance).
    /// </summary>
    public enum CarryStance
    {
        /// <summary>Holds fire and lets enemies pass until a bot or the player is shot at.</summary>
        Passive,

        /// <summary>Stays close, prefers cover, fights at range: the fork's behaviour before v11.</summary>
        Defensive,

        /// <summary>Pushes and flanks with covering fire (the old Engage Enemy); chases up to a set range.</summary>
        Aggressive,

        /// <summary>Charges the enemy (the old Engage Enemy Wrecklessly); no leash.</summary>
        HyperAggressive,
    }

    /// <summary>The SAIN personality a stance lends (the user's choice, 2026-11-09).</summary>
    public enum StancePersonality
    {
        Normal,
        Chad,
        /// <summary>GigaChad or Wreckless, per the reckless-personality setting.</summary>
        Reckless,
    }

    /// <summary>How a stance limits chasing.</summary>
    public enum StanceLeash
    {
        /// <summary>The timed chase leash (3-5 s per chase, then back to the player): Passive and Defensive.</summary>
        Timed,

        /// <summary>Chase freely within StanceSettings.AggressiveLeash metres of the player.</summary>
        Range,

        /// <summary>No limit.</summary>
        None,
    }

    /// <summary>Tunables for the stances; the plugin copies the user-facing ones from its settings.</summary>
    public sealed class StanceSettings
    {
        /// <summary>
        /// Passive only: whether the player firing at an enemy releases the squad, as well as a bot or
        /// the player being shot at. Default on: when the player starts a fight, the bots join it.
        /// </summary>
        public bool PassiveEngagesWhenPlayerShoots = true;

        /// <summary>Aggressive: metres from the player within which a bot may chase freely. The user's figure: about 100.</summary>
        public float AggressiveLeash = 100f;

        /// <summary>
        /// Aggressive: a bot cut off at the leash walks back until it is within this share of the leash
        /// (0.5 = 50 m of a 100 m leash), so it does not hover at the edge chasing and turning back.
        /// </summary>
        public float ReturnShare = 0.5f;
    }

    /// <summary>What each stance allows. Pure functions.</summary>
    public static class StanceRules
    {
        /// <summary>A bot's stance: its own override when it has one, otherwise the squad's.</summary>
        public static CarryStance Effective(CarryStance squad, CarryStance? botOverride)
        {
            return botOverride ?? squad;
        }

        /// <summary>Grenades only in Aggressive and Hyper-aggressive (the user feared a throw into a wall meant for a doorway).</summary>
        public static bool AllowsGrenades(CarryStance stance)
        {
            return stance is CarryStance.Aggressive or CarryStance.HyperAggressive;
        }

        /// <summary>The personality the stance lends: Normal, Normal, Chad, GigaChad/Wreckless.</summary>
        public static StancePersonality Personality(CarryStance stance)
        {
            return stance switch
            {
                CarryStance.Aggressive => StancePersonality.Chad,
                CarryStance.HyperAggressive => StancePersonality.Reckless,
                _ => StancePersonality.Normal,
            };
        }

        /// <summary>Which Engage tactics the stance runs: Tactical (flank, covering fire), Reckless (rush) or None.</summary>
        public static EngageKind Tactics(CarryStance stance)
        {
            return stance switch
            {
                CarryStance.Aggressive => EngageKind.Tactical,
                CarryStance.HyperAggressive => EngageKind.Reckless,
                _ => EngageKind.None,
            };
        }

        /// <summary>The stance's chase limit: timed for Passive and Defensive, a range for Aggressive, none for Hyper-aggressive.</summary>
        public static StanceLeash Leash(CarryStance stance)
        {
            return stance switch
            {
                CarryStance.Aggressive => StanceLeash.Range,
                CarryStance.HyperAggressive => StanceLeash.None,
                _ => StanceLeash.Timed,
            };
        }

        /// <summary>
        /// Whether a bot may chase an enemy it never saw itself (heard, or reported by a squadmate or the
        /// player). Passive and Defensive may not, as before v11; the aggressive stances may, as the
        /// Engage orders could.
        /// </summary>
        public static bool ChasesUnseen(CarryStance stance)
        {
            return stance is CarryStance.Aggressive or CarryStance.HyperAggressive;
        }

        /// <summary>
        /// Fills the chase leash's range fields for the stance: 0 for the timed leash, the Aggressive
        /// range, or float.PositiveInfinity for none.
        /// </summary>
        public static void ApplyLeash(CarryStance stance, StanceSettings settings, ChaseLeashSettings leash)
        {
            leash.RangeLimit = Leash(stance) switch
            {
                StanceLeash.Range => settings.AggressiveLeash,
                StanceLeash.None => float.PositiveInfinity,
                _ => 0f,
            };
            leash.ReturnShare = settings.ReturnShare;
        }
    }

    /// <summary>What a PassiveGate update changed.</summary>
    public enum PassiveEvent
    {
        None,
        /// <summary>The squad was just released to fight.</summary>
        Released,
        /// <summary>The fight ended; the squad holds fire again.</summary>
        HoldingAgain,
    }

    /// <summary>Why a PassiveGate released the squad.</summary>
    public enum PassiveCause
    {
        None,
        BotShotAt,
        PlayerShotAt,
        PlayerFired,
    }

    /// <summary>
    /// One squad's Passive hold-fire state. The squad holds fire until a carry bot or the player is shot
    /// at, or (with PassiveEngagesWhenPlayerShoots) the player fires at an enemy. It is then released
    /// until the squad leaves combat, and holds fire again after that. Tracked whatever the stance, so a
    /// switch to Passive mid-fight keeps fighting a fight that already provoked it. Not thread-safe;
    /// main thread only.
    /// </summary>
    public sealed class PassiveGate
    {
        /// <summary>True while the squad is released to fight.</summary>
        public bool Released { get; private set; }

        /// <summary>What released it, while released.</summary>
        public PassiveCause Cause { get; private set; }

        /// <summary>
        /// Updates the gate.
        /// </summary>
        /// <param name="inCombat">True while the squad's team mode is Combat.</param>
        /// <param name="botShotAt">True when a carry bot was hit or shot at just now.</param>
        /// <param name="playerShotAt">True when the player was hit just now.</param>
        /// <param name="playerFired">True when the player fired at an enemy just now.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        public PassiveEvent Update(bool inCombat, bool botShotAt, bool playerShotAt, bool playerFired, StanceSettings settings)
        {
            var cause = botShotAt ? PassiveCause.BotShotAt
                : playerShotAt ? PassiveCause.PlayerShotAt
                : playerFired && settings.PassiveEngagesWhenPlayerShoots ? PassiveCause.PlayerFired
                : PassiveCause.None;
            if (!Released)
            {
                if (cause == PassiveCause.None)
                {
                    return PassiveEvent.None;
                }
                Released = true;
                Cause = cause;
                return PassiveEvent.Released;
            }
            if (!inCombat && cause == PassiveCause.None)
            {
                Released = false;
                Cause = PassiveCause.None;
                return PassiveEvent.HoldingAgain;
            }
            return PassiveEvent.None;
        }
    }
}
