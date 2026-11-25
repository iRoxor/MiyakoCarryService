// HoldFire.cs -- the rules of the new Passive stance (fork v18): who counts as hostile, whether a bot may fire at an
// enemy, and what each SAIN decision becomes while it holds fire.
//
// The user's request (2026-11-24): bots that cannot steal long-range kills or join fights not worth having. Their
// answers: beyond the range a bot never fires, even when shot at; within it a bot fires only at an enemy that attacked
// first; it never chases. Callers:
//   - TeamModes keeps one HostileSet per squad (marked by ApplyDamagePatch, McsAILeadPlayer.MarkLeadAttacker and
//     PlayHitEffectPatch, so the set fills in every stance) and asks it from DecisionPolicy and SainShootGatePatch.
//   - DecisionPolicy calls HoldFireRules.Judge on every SAIN decision of a Passive bot; SainShootGatePatch calls
//     HoldFireRules.MayEngage before SAIN's own shooter fires at an enemy (SAIN fires at ANY visible enemy once a combat
//     action runs, so a decision about one enemy cannot stop a shot at another); McsBrainLayer asks it before the bot
//     fires from a stationary weapon.
//   - HoldFireLog is one bot's rate limiter for the "passive:" diagnostic lines (BotForkState holds it).
// Unity-free so the logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it. Reads and writes no files.

using System.Collections.Generic;
using System.Globalization;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Why an enemy became hostile to a squad.</summary>
    public enum HostileCause
    {
        /// <summary>It damaged a carry bot (ApplyDamagePatch).</summary>
        HitBot,
        /// <summary>It damaged the player (McsAILeadPlayer.MarkLeadAttacker).</summary>
        HitPlayer,
        /// <summary>Its bullet landed near a carry bot (PlayHitEffectPatch).</summary>
        ShotNearBot,
        /// <summary>Its bullet landed near the player (PlayHitEffectPatch, new in fork v18).</summary>
        ShotNearPlayer,
    }

    /// <summary>
    /// The enemies (by profile id) that attacked one squad first. An enemy stays hostile for the rest of the raid, whatever the
    /// stance; the set is dropped with the squad's entry at raid end. Main thread only.
    /// </summary>
    public sealed class HostileSet
    {
        private readonly Dictionary<string, HostileCause> _causes = new();

        /// <summary>How many enemies are hostile.</summary>
        public int Count => _causes.Count;

        /// <summary>
        /// Marks an enemy hostile. The first cause is kept; marking again changes nothing.
        /// </summary>
        /// <param name="profileId">The enemy's profile id; null or empty marks nothing.</param>
        /// <param name="cause">What the enemy did.</param>
        /// <returns>True when the enemy was not hostile before (the caller logs it once).</returns>
        public bool Mark(string profileId, HostileCause cause)
        {
            if (string.IsNullOrEmpty(profileId) || _causes.ContainsKey(profileId))
            {
                return false;
            }
            _causes.Add(profileId, cause);
            return true;
        }

        /// <summary>True when the enemy has attacked the squad.</summary>
        /// <param name="profileId">The enemy's profile id; null or empty is never hostile.</param>
        public bool IsHostile(string profileId)
        {
            return !string.IsNullOrEmpty(profileId) && _causes.ContainsKey(profileId);
        }

        /// <summary>Forgets every enemy.</summary>
        public void Clear()
        {
            _causes.Clear();
        }
    }

    /// <summary>Why a Passive bot does not fire at an enemy (None: it may).</summary>
    public enum HoldReason
    {
        /// <summary>The bot may fire.</summary>
        None,
        /// <summary>The decision names no enemy.</summary>
        NoEnemy,
        /// <summary>The enemy is farther than the range.</summary>
        Beyond,
        /// <summary>The enemy is within the range but has not attacked.</summary>
        NotHostile,
    }

    /// <summary>
    /// SAIN's combat decisions (SAIN.Preset.Shared.Enums.ECombatDecision, 4.5.1) by name, so the table below can be tested
    /// without SAIN. DecisionPolicy maps by name; a SAIN name missing here becomes Other, which is kept as it is.
    /// </summary>
    public enum HfSolo
    {
        Other, None, Retreat, Search, RunAway, DogFight, SeekCover, StandAndShoot, ThrowGrenade, ShiftCover, RushEnemy,
        MoveToEngage, ShootDistantEnemy, AvoidGrenade, Freeze, CreepOnEnemy, MeleeAttack, FightZombies, DebugNoDecision,
    }

    /// <summary>SAIN's squad decisions (ESquadDecision, 4.5.1) by name; see <see cref="HfSolo"/>.</summary>
    public enum HfSquad
    {
        Other, None, Surround, Retreat, Suppress, PushSuppressedEnemy, BoundingRetreat, Regroup, SpreadOut, HoldPositions,
        Help, Search, GroupSearch,
    }

    /// <summary>What to do with SAIN's combat decision.</summary>
    public enum SoloChange
    {
        /// <summary>Leave it as SAIN decided.</summary>
        Keep,
        /// <summary>Replace it with None, so SAIN's layers stand down and the Guard layer has the bot.</summary>
        ToNone,
        /// <summary>StandAndShoot when the enemy is visible, otherwise SeekCover (a grenade throw is not allowed).</summary>
        ToStandOrCover,
        /// <summary>StandAndShoot when the enemy is visible, otherwise None (a chase is not allowed).</summary>
        ToStandOrNone,
    }

    /// <summary>What to do with SAIN's squad decision.</summary>
    public enum SquadChange
    {
        /// <summary>Leave it as SAIN decided.</summary>
        Keep,
        /// <summary>Replace it with None.</summary>
        ToNone,
    }

    /// <summary>The answer of <see cref="HoldFireRules.Judge"/>.</summary>
    public readonly struct PassiveVerdict
    {
        /// <summary>None when the decision may proceed; otherwise why the bot holds fire.</summary>
        public readonly HoldReason Reason;

        /// <summary>What to do with the combat decision.</summary>
        public readonly SoloChange Solo;

        /// <summary>What to do with the squad decision.</summary>
        public readonly SquadChange Squad;

        /// <summary>Creates an answer.</summary>
        public PassiveVerdict(HoldReason reason, SoloChange solo, SquadChange squad)
        {
            Reason = reason;
            Solo = solo;
            Squad = squad;
        }
    }

    /// <summary>The new Passive stance's rules. Pure functions.</summary>
    public static class HoldFireRules
    {
        /// <summary>
        /// Whether a Passive bot may fire at an enemy: only a hostile enemy, and only within the range (a distance equal to the
        /// range counts as within). A distance that is not a number never qualifies.
        /// </summary>
        /// <param name="enemyDistance">Metres from the bot to the enemy's current position.</param>
        /// <param name="range">The Passive range, metres.</param>
        /// <param name="isHostile">Whether the enemy attacked the squad first.</param>
        public static bool MayEngage(float enemyDistance, float range, bool isHostile)
        {
            return isHostile && enemyDistance <= range;
        }

        /// <summary>
        /// Why a Passive bot does not fire at an enemy, or None when it may. An enemy beyond the range is named so even when it
        /// is not hostile either.
        /// </summary>
        /// <param name="enemyExists">False when the decision names no enemy.</param>
        /// <param name="enemyDistance">Metres from the bot to the enemy's current position.</param>
        /// <param name="range">The Passive range, metres.</param>
        /// <param name="isHostile">Whether the enemy attacked the squad first.</param>
        public static HoldReason Why(bool enemyExists, float enemyDistance, float range, bool isHostile)
        {
            if (!enemyExists)
            {
                return HoldReason.NoEnemy;
            }
            if (MayEngage(enemyDistance, range, isHostile))
            {
                return HoldReason.None;
            }
            return enemyDistance <= range ? HoldReason.NotHostile : HoldReason.Beyond;
        }

        /// <summary>The words the veto reason uses: "no enemy", "beyond 15 m" or "not hostile" (empty for None).</summary>
        /// <param name="reason">The verdict's reason.</param>
        /// <param name="range">The Passive range, metres.</param>
        public static string ReasonText(HoldReason reason, float range)
        {
            return reason switch
            {
                HoldReason.NoEnemy => "no enemy",
                HoldReason.Beyond => "beyond " + range.ToString("0", CultureInfo.InvariantCulture) + " m",
                HoldReason.NotHostile => "not hostile",
                _ => "",
            };
        }

        /// <summary>
        /// What a Passive bot does with one set of SAIN decisions. When the enemy is missing, beyond the range or not hostile,
        /// both decisions become None (a decision that already is None is kept). Otherwise: a grenade throw becomes StandAndShoot
        /// or SeekCover; every chase (RushEnemy, Search, MoveToEngage, CreepOnEnemy; GroupSearch, Help, PushSuppressedEnemy,
        /// Surround and the squad's Search) and ShiftCover (SAIN picks its cover point only after the decision, so it cannot be
        /// checked against the range here) are vetoed; suppressive fire becomes None; everything else stands.
        /// </summary>
        /// <param name="solo">SAIN's combat decision.</param>
        /// <param name="squad">SAIN's squad decision.</param>
        /// <param name="enemyExists">False when the decision names no enemy.</param>
        /// <param name="enemyDistance">Metres from the bot to the enemy's current position.</param>
        /// <param name="range">The Passive range, metres.</param>
        /// <param name="isHostile">Whether the enemy attacked the squad first.</param>
        public static PassiveVerdict Judge(HfSolo solo, HfSquad squad, bool enemyExists, float enemyDistance, float range, bool isHostile)
        {
            var reason = Why(enemyExists, enemyDistance, range, isHostile);
            if (reason != HoldReason.None)
            {
                return new PassiveVerdict(reason,
                    solo == HfSolo.None ? SoloChange.Keep : SoloChange.ToNone,
                    squad == HfSquad.None ? SquadChange.Keep : SquadChange.ToNone);
            }

            SoloChange soloChange;
            switch (solo)
            {
                case HfSolo.ThrowGrenade:
                    soloChange = SoloChange.ToStandOrCover;
                    break;
                case HfSolo.RushEnemy:
                case HfSolo.Search:
                case HfSolo.MoveToEngage:
                case HfSolo.CreepOnEnemy:
                case HfSolo.ShiftCover:
                    soloChange = SoloChange.ToStandOrNone;
                    break;
                default:
                    soloChange = SoloChange.Keep;
                    break;
            }

            var squadChange = squad switch
            {
                HfSquad.Suppress or HfSquad.GroupSearch or HfSquad.Help or HfSquad.PushSuppressedEnemy
                    or HfSquad.Surround or HfSquad.Search => SquadChange.ToNone,
                _ => SquadChange.Keep,
            };
            return new PassiveVerdict(HoldReason.None, soloChange, squadChange);
        }

        /// <summary>
        /// Whether a manual or suppressive shot of SAIN's (ManualShootClass.TryShoot) may happen for a Passive bot: suppressive
        /// reasons never; the others by <see cref="MayEngage"/>. SAIN's reason codes are passed as booleans so this stays free
        /// of SAIN's types.
        /// </summary>
        /// <param name="isSuppression">True for SAIN's squad-suppressing, walk-to-cover-suppress and suppress reasons.</param>
        /// <param name="enemyDistance">Metres from the bot to the enemy's current position.</param>
        /// <param name="range">The Passive range, metres.</param>
        /// <param name="isHostile">Whether the enemy attacked the squad first.</param>
        public static bool MayShootManually(bool isSuppression, float enemyDistance, float range, bool isHostile)
        {
            return !isSuppression && MayEngage(enemyDistance, range, isHostile);
        }
    }

    /// <summary>
    /// One carry bot's limiter for the new Passive's diagnostic lines: a "held fire" line at most once per enemy every 10 s, and
    /// an "engaged" line only for the first allowed decision against each enemy. Kept in BotForkState, so it ends with the bot.
    /// Main thread only.
    /// </summary>
    public sealed class HoldFireLog
    {
        /// <summary>Seconds between "held fire at" lines about the same enemy.</summary>
        public const float HELD_INTERVAL = 10f;

        private readonly Dictionary<string, float> _lastHeld = new();
        private readonly HashSet<string> _engaged = new();

        /// <summary>True when a "held fire at" line about this enemy is due (none was written in the last 10 s); records the time.</summary>
        /// <param name="enemyId">The enemy's profile id.</param>
        /// <param name="now">Game time in seconds.</param>
        public bool HeldFireDue(string enemyId, float now)
        {
            if (enemyId == null)
            {
                return false;
            }
            if (_lastHeld.TryGetValue(enemyId, out var last) && now - last < HELD_INTERVAL)
            {
                return false;
            }
            _lastHeld[enemyId] = now;
            return true;
        }

        /// <summary>True the first time it is called for this enemy: the bot's first allowed decision against it.</summary>
        /// <param name="enemyId">The enemy's profile id.</param>
        public bool FirstEngagement(string enemyId)
        {
            return enemyId != null && _engaged.Add(enemyId);
        }
    }
}
