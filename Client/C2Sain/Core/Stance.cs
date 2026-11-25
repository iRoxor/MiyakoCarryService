// Stance.cs -- the five combat stances (fork v11, split in v18): what each one allows, Reactive's hold-fire
// gate, and the one-time migration of a saved Passive.
//
// The stance replaces the Engage Enemy orders and the allowed-personalities setting (the user's spec,
// 2026-11-08, with their answers of 2026-11-09). Fork v18 renamed the v11 Passive to Reactive and added a
// new Passive (the user's request and answers of 2026-11-24; the rules are in HoldFire.cs). Callers:
//   - ForkConfig.Stance holds the squad stance; the Team ▸ Combat ▸ Stance commands write it, so the
//     last squad stance carries over to the next raid. BotForkState.StanceOverride holds a stance set
//     from one bot's own menu, for that raid only (carry bots expire and are rebuilt, so nothing is
//     stored against one). StanceRules.Effective combines the two. ForkConfig.MigrateDefaults calls
//     StanceMigration once to turn a Passive saved before v18 into Reactive.
//   - DecisionPolicy asks what the stance allows on every SAIN decision; EngageTactics lends the
//     stance's personality and runs the Aggressive and Hyper-aggressive tactics; SainGrenadePatch
//     blocks SAIN's grenade throws where the stance forbids them.
//   - TeamModes keeps one ReactiveGate per squad and ticks it twice a second.
// Unity-free so the logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it.
// Reads and writes no files.

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>
    /// A carry bot's combat stance. The names are also the values of the "Stance" BepInEx setting, so
    /// a name is never renamed without a migration that moves the saved value (a renamed value would
    /// reset everyone's saved stance). Fork v18 did exactly that once: the v11 Passive became
    /// Reactive and the name Passive was given to a new stance, so <see cref="StanceMigration"/> moves
    /// a Passive saved before v18 to Reactive. The order here is the order of the settings drop-down.
    /// </summary>
    public enum CarryStance
    {
        /// <summary>
        /// Fork v18. Never fires at an enemy farther than the Passive range, whatever happens; within it fires only
        /// at an enemy that attacked the squad first (HoldFire.cs). Never chases, flanks, suppresses or throws grenades.
        /// </summary>
        Passive,

        /// <summary>
        /// Holds fire and lets enemies pass until a bot or the player is shot at (what Passive was before fork v18,
        /// unchanged), then fights as Defensive until the fight ends.
        /// </summary>
        Reactive,

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
        /// <summary>The timed chase leash (3-5 s per chase, then back to the player): Reactive and Defensive.</summary>
        Timed,

        /// <summary>Chase freely within StanceSettings.AggressiveLeash metres of the player.</summary>
        Range,

        /// <summary>No limit.</summary>
        None,

        /// <summary>Fork v18, the new Passive: every chase is vetoed, always (ChaseLeashSettings.NoChase).</summary>
        NoChase,
    }

    /// <summary>Tunables for the stances; the plugin copies the user-facing ones from its settings.</summary>
    public sealed class StanceSettings
    {
        /// <summary>
        /// Reactive only (the setting keeps its v11 name, "PassiveEngagesWhenYouShoot"): whether the player firing at an
        /// enemy releases the squad, as well as a bot or the player being shot at. Default on: when the player starts a
        /// fight, the bots join it. The new Passive ignores the player's own shots.
        /// </summary>
        public bool ReactiveEngagesWhenPlayerShoots = true;

        /// <summary>
        /// Passive only (fork v18): metres from the bot beyond which it never fires at an enemy, and within which it
        /// fires only at a hostile one (HoldFire.cs). The setting's range is 5-100; default 15.
        /// </summary>
        public float PassiveRange = 15f;

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

        /// <summary>
        /// The stance's chase limit: none allowed for Passive (fork v18), timed for Reactive and Defensive, a range for
        /// Aggressive, none for Hyper-aggressive.
        /// </summary>
        public static StanceLeash Leash(CarryStance stance)
        {
            return stance switch
            {
                CarryStance.Passive => StanceLeash.NoChase,
                CarryStance.Aggressive => StanceLeash.Range,
                CarryStance.HyperAggressive => StanceLeash.None,
                _ => StanceLeash.Timed,
            };
        }

        /// <summary>
        /// Fork v18: whether the stance fires only by the hold-fire rules (HoldFire.cs: hostile enemies within the Passive
        /// range). True for Passive only.
        /// </summary>
        public static bool HoldsFireByRange(CarryStance stance)
        {
            return stance == CarryStance.Passive;
        }

        /// <summary>
        /// Fork v18: whether the stance holds fire until the squad is released (ReactiveGate). True for Reactive only.
        /// </summary>
        public static bool HoldsFireUntilReleased(CarryStance stance)
        {
            return stance == CarryStance.Reactive;
        }

        /// <summary>
        /// Whether a bot may chase an enemy it never saw itself (heard, or reported by a squadmate or the
        /// player). Passive, Reactive and Defensive may not, as before v11; the aggressive stances may, as the
        /// Engage orders could.
        /// </summary>
        public static bool ChasesUnseen(CarryStance stance)
        {
            return stance is CarryStance.Aggressive or CarryStance.HyperAggressive;
        }

        /// <summary>
        /// The stance's leash range (m) from the player: 0 for the timed leash and for Passive's no-chase leash, the
        /// Aggressive range, or float.PositiveInfinity for none. Since fork v17 the flank rules and the leash pull read
        /// it too (C2Sain/EngageTactics.RangeLimit).
        /// </summary>
        public static float RangeLimit(CarryStance stance, StanceSettings settings)
        {
            return Leash(stance) switch
            {
                StanceLeash.Range => settings.AggressiveLeash,
                StanceLeash.None => float.PositiveInfinity,
                _ => 0f,
            };
        }

        /// <summary>
        /// Fills the chase leash's range fields for the stance: 0 for the timed leash, the Aggressive
        /// range, or float.PositiveInfinity for none; and, since fork v18, the flag that vetoes every chase (Passive).
        /// </summary>
        public static void ApplyLeash(CarryStance stance, StanceSettings settings, ChaseLeashSettings leash)
        {
            leash.RangeLimit = RangeLimit(stance, settings);
            leash.ReturnShare = settings.ReturnShare;
            leash.NoChase = Leash(stance) == StanceLeash.NoChase;
        }
    }

    /// <summary>What a ReactiveGate update changed.</summary>
    public enum ReactiveEvent
    {
        None,
        /// <summary>The squad was just released to fight.</summary>
        Released,
        /// <summary>The fight ended; the squad holds fire again.</summary>
        HoldingAgain,
    }

    /// <summary>Why a ReactiveGate released the squad.</summary>
    public enum ReactiveCause
    {
        None,
        BotShotAt,
        PlayerShotAt,
        PlayerFired,
    }

    /// <summary>
    /// One squad's Reactive hold-fire state (the v11 "PassiveGate", renamed in fork v18 with the stance; its rules did not
    /// change). The squad holds fire until a carry bot or the player is shot at, or (with ReactiveEngagesWhenPlayerShoots)
    /// the player fires at an enemy. It is then released until the squad leaves combat, and holds fire again after that.
    /// Tracked whatever the stance, so a switch to Reactive mid-fight keeps fighting a fight that already provoked it. Not
    /// thread-safe; main thread only.
    /// </summary>
    public sealed class ReactiveGate
    {
        /// <summary>True while the squad is released to fight.</summary>
        public bool Released { get; private set; }

        /// <summary>What released it, while released.</summary>
        public ReactiveCause Cause { get; private set; }

        /// <summary>
        /// Updates the gate.
        /// </summary>
        /// <param name="inCombat">True while the squad's team mode is Combat.</param>
        /// <param name="botShotAt">True when a carry bot was hit or shot at just now.</param>
        /// <param name="playerShotAt">True when the player was hit just now.</param>
        /// <param name="playerFired">True when the player fired at an enemy just now.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        public ReactiveEvent Update(bool inCombat, bool botShotAt, bool playerShotAt, bool playerFired, StanceSettings settings)
        {
            var cause = botShotAt ? ReactiveCause.BotShotAt
                : playerShotAt ? ReactiveCause.PlayerShotAt
                : playerFired && settings.ReactiveEngagesWhenPlayerShoots ? ReactiveCause.PlayerFired
                : ReactiveCause.None;
            if (!Released)
            {
                if (cause == ReactiveCause.None)
                {
                    return ReactiveEvent.None;
                }
                Released = true;
                Cause = cause;
                return ReactiveEvent.Released;
            }
            if (!inCombat && cause == ReactiveCause.None)
            {
                Released = false;
                Cause = ReactiveCause.None;
                return ReactiveEvent.HoldingAgain;
            }
            return ReactiveEvent.None;
        }
    }

    /// <summary>
    /// Fork v18's one-time move of a saved stance from the old names to the new ones. Pure; ForkConfig.MigrateDefaults is the
    /// only caller.
    /// </summary>
    public static class StanceMigration
    {
        /// <summary>
        /// The settings version (ForkConfig's hidden SettingsVersion) at which Passive became Reactive and the name Passive was
        /// given to the new stance. A file whose version is below it was written by fork v17 or earlier.
        /// </summary>
        public const int PassiveSplitVersion = 16;

        /// <summary>
        /// The stance to keep after loading a file written at <paramref name="savedSettingsVersion"/>: a Passive saved before
        /// the split was the old stance, so it becomes Reactive; a Passive saved at or after it is the new stance and stays;
        /// every other stance is unchanged.
        /// </summary>
        /// <param name="saved">The stance read from the file.</param>
        /// <param name="savedSettingsVersion">The file's SettingsVersion before this build raised it.</param>
        public static CarryStance Migrate(CarryStance saved, int savedSettingsVersion)
        {
            return savedSettingsVersion < PassiveSplitVersion && saved == CarryStance.Passive ? CarryStance.Reactive : saved;
        }
    }
}
