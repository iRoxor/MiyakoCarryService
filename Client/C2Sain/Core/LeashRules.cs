// LeashRules.cs -- how the Aggressive leash holds: when a flank leg may be planned, where its end may lie, and when a bot
// that strayed beyond the leash range is pulled back. Fork v17.
//
// Callers: C2Sain/EngageTactics.cs (CanPlanFlank before it plans a leg, FlankSpotLimit for the spot it asks C2Nav for),
// C2Sain/C2Nav.cs (FlankSpotAllowed, for each flank point candidate) and C2Sain/C2Gate.cs (LeashPull, once per brain tick per
// bot, then a Leash recall through BreakOff). One LeashPull per carry bot, held in C2Sain/BotForkState.cs. Unity-free so the
// logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it. Reads and writes no files.
//
// "RangeLimit" is the stance's leash range from the player (Core/Stance.cs StanceRules.RangeLimit): 100 m for Aggressive
// by default, float.PositiveInfinity for Hyper-aggressive (no leash), 0 for Passive (fork v18: no chase at all), Reactive and
// Defensive (the timed leash, which has no range, so none of the range tests below apply).
//
// Why (the user's build 16 raids, 2026-11-24; miyako-carry-service/evidence/2026-11-24-build-16-customs-factory/
// build-16-analysis.txt, section 5a): an Aggressive bot ran flank legs that began 90 m beyond its last, was 105 m out when
// one ended, began a NEW leg with the bot 137 m out, and was 239 m away at the raid's end. The range leash only vetoed
// chase decisions; a flank leg sets the decisions to None, so neither it nor the Break off grace ever saw the leg.

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Tunables for <see cref="LeashRules"/> and <see cref="LeashPull"/>.</summary>
    public sealed class LeashRuleSettings
    {
        /// <summary>No flank leg is planned for a bot farther from the player than this share of RangeLimit.</summary>
        public float FlankBotShare = 0.8f;

        /// <summary>No flank leg is planned against an enemy whose last known position is farther from the player than this share of RangeLimit.</summary>
        public float FlankEnemyShare = 1f;

        /// <summary>A flank leg's end may lie at most this share of RangeLimit from the player.</summary>
        public float FlankSpotShare = 0.8f;

        /// <summary>Seconds a bot must stay beyond RangeLimit before the leash recalls it.</summary>
        public float PullSeconds = 2f;

        /// <summary>True (always, in the plugin): the Break off grace stops a flank leg being planned. False is a deliberately wrong value for the harness.</summary>
        public bool GraceBlocksFlank = true;

        /// <summary>True (always, in the plugin): a bot barred from chasing (the leash's return state) plans no flank leg either. False is a deliberately wrong value for the harness.</summary>
        public bool ReturningBlocksFlank = true;
    }

    /// <summary>The flank rules near the leash range. Pure functions.</summary>
    public static class LeashRules
    {
        /// <summary>
        /// Whether a flank leg may be planned now: not while the Break off grace runs, not while the leash bars the bot from chasing,
        /// not when the bot is already beyond FlankBotShare of the range from the player, and not against an enemy last known
        /// beyond FlankEnemyShare of the range from the player. A range of 0 (the timed leash) skips the two distance tests.
        /// </summary>
        /// <param name="graceRunning">The chase leash's grace period is running (a Break off or Regroup recall just ended).</param>
        /// <param name="returning">The chase leash is making the bot come back (ChaseLeash.Returning).</param>
        /// <param name="botToPlayer">Metres from the bot to the player.</param>
        /// <param name="enemyToPlayer">Metres from the enemy's last known position to the player.</param>
        /// <param name="rangeLimit">The stance's leash range (m).</param>
        /// <param name="settings">Tunables.</param>
        public static bool CanPlanFlank(bool graceRunning, bool returning, float botToPlayer, float enemyToPlayer, float rangeLimit, LeashRuleSettings settings)
        {
            if (settings.GraceBlocksFlank && graceRunning)
            {
                return false;
            }
            if (settings.ReturningBlocksFlank && returning)
            {
                return false;
            }
            if (rangeLimit > 0f)
            {
                if (botToPlayer > rangeLimit * settings.FlankBotShare)
                {
                    return false;
                }
                if (enemyToPlayer > rangeLimit * settings.FlankEnemyShare)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>The farthest from the player (m) a flank leg's end may lie: FlankSpotShare of the range, no limit when the range is 0 or infinite.</summary>
        /// <param name="rangeLimit">The stance's leash range (m).</param>
        /// <param name="settings">Tunables.</param>
        public static float FlankSpotLimit(float rangeLimit, LeashRuleSettings settings)
        {
            return rangeLimit > 0f ? rangeLimit * settings.FlankSpotShare : float.PositiveInfinity;
        }

        /// <summary>True when a flank point candidate lies no farther from the player than the limit (C2Nav.TryPickFlankSpot skips the others).</summary>
        /// <param name="spotToPlayer">Metres from the candidate to the player.</param>
        /// <param name="limit">What <see cref="FlankSpotLimit"/> returned.</param>
        public static bool FlankSpotAllowed(float spotToPlayer, float limit)
        {
            return spotToPlayer <= limit;
        }

        /// <summary>The metres within which a Leash recall counts as arrived: the range times its return share (50 m of a 100 m leash).</summary>
        /// <param name="rangeLimit">The stance's leash range (m).</param>
        /// <param name="returnShare">StanceSettings.ReturnShare.</param>
        public static float PullArriveRadius(float rangeLimit, float returnShare)
        {
            return rangeLimit * returnShare;
        }
    }

    /// <summary>
    /// One bot's "stayed beyond the leash range" timer. C2Sain/C2Gate.cs updates it once per brain tick; when it says true the
    /// gate begins a Leash recall. Main-thread only.
    /// </summary>
    public sealed class LeashPull
    {
        private float _beyondSince = -1f;

        /// <summary>Forgets the timer (the bot is recalled already, or the raid is over).</summary>
        public void Reset()
        {
            _beyondSince = -1f;
        }

        /// <summary>
        /// Updates the timer. It runs while the bot is more than a finite RangeLimit from the player and the bot is eligible, and
        /// starts over whenever either stops being true.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="distanceToPlayer">Metres from the bot to the player.</param>
        /// <param name="rangeLimit">The stance's leash range (m); 0 (the timed leash) and infinity (Hyper-aggressive) never pull.</param>
        /// <param name="eligible">False while a recall runs or the bot is on a command of its own (Go To Point, Hold Position, an escort, a loot or proxy task, Clear Area, a stationary weapon).</param>
        /// <param name="settings">Tunables; read on every call.</param>
        /// <returns>True on the call where the bot has been beyond the range for PullSeconds: begin a Leash recall. The timer then starts over.</returns>
        public bool Update(float now, float distanceToPlayer, float rangeLimit, bool eligible, LeashRuleSettings settings)
        {
            if (!eligible || rangeLimit <= 0f || float.IsPositiveInfinity(rangeLimit) || distanceToPlayer <= rangeLimit)
            {
                _beyondSince = -1f;
                return false;
            }
            if (_beyondSince < 0f)
            {
                _beyondSince = now;
                return false;
            }
            if (now - _beyondSince >= settings.PullSeconds)
            {
                _beyondSince = -1f;
                return true;
            }
            return false;
        }
    }
}
