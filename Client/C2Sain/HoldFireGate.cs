// HoldFireGate.cs -- the Unity side of the new Passive stance's hold-fire rule (fork v18; the rule itself is Core/HoldFire.cs).
//
// Called by SainShootGatePatch (before SAIN's shooter fires at an enemy), by McsBrainLayer (before a bot fires from a
// stationary weapon, or fights with MCS's own logic because SAIN does not manage it) and by DecisionPolicy (the range). Reads
// the bot's stance, the squad's hostile set (TeamModes) and the "Stance: Passive firing range" setting; writes nothing.
//
// Why it exists beside DecisionPolicy: SAIN's combat actions (StandAndShoot, SeekCover, ShiftCover, Regroup and the rest) all
// fire through SAINShootData.ShootAnyVisibleEnemies, which tries the decision's enemy and then ANY other visible enemy. A
// decision about one enemy therefore cannot stop a shot at another, so each shot is checked on its own.

using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Extensions;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Per-shot check of the new Passive stance's rule. Main thread only.</summary>
    internal static class HoldFireGate
    {
        /// <summary>The range used before the setting is bound, and the setting's default (m).</summary>
        public const float DEFAULT_RANGE = 15f;

        /// <summary>The Passive range now (m): the "Stance: Passive firing range" setting.</summary>
        public static float Range => ForkConfig.PassiveRange?.Value ?? DEFAULT_RANGE;

        /// <summary>
        /// Whether the gate applies to this bot: a carry bot whose stance is the new Passive.
        /// </summary>
        /// <param name="botOwner">The bot; null never applies.</param>
        public static bool AppliesTo(BotOwner botOwner)
        {
            if (botOwner == null || !botOwner.IsMcsBotPlayer)
            {
                return false;
            }
            var fork = botOwner.GetMcsBotPlayerData()?.Fork;
            return fork != null && StanceRules.HoldsFireByRange(EngageTactics.CurrentStance(fork));
        }

        /// <summary>
        /// Whether a bot may fire at an enemy now. True for every bot the gate does not apply to; for a Passive carry bot, the
        /// answer of <see cref="HoldFireRules.MayEngage"/> for the enemy's current distance and the squad's hostile set.
        /// </summary>
        /// <param name="botOwner">The bot about to fire.</param>
        /// <param name="enemyProfileId">The enemy's profile id.</param>
        /// <param name="enemyPosition">The enemy's current position.</param>
        public static bool Allows(BotOwner botOwner, string enemyProfileId, Vector3 enemyPosition)
        {
            if (!AppliesTo(botOwner))
            {
                return true;
            }
            var data = botOwner.GetMcsBotPlayerData();
            var distance = Vector3.Distance(botOwner.Position, enemyPosition);
            return HoldFireRules.MayEngage(distance, Range, TeamModes.IsHostile(data?.McsAILeadPlayer, enemyProfileId));
        }

        /// <summary>
        /// Whether SAIN's manual or suppressive shot (ManualShootClass.TryShoot) may happen. True for every bot the gate does not
        /// apply to; for a Passive carry bot, <see cref="HoldFireRules.MayShootManually"/>: never a suppressive shot, and the others
        /// as <see cref="Allows(BotOwner, string, Vector3)"/>.
        /// </summary>
        /// <param name="botOwner">The bot about to fire.</param>
        /// <param name="isSuppression">True for SAIN's squad-suppressing, walk-to-cover-suppress and suppress reasons.</param>
        /// <param name="enemyProfileId">The enemy's profile id.</param>
        /// <param name="enemyPosition">The enemy's current position.</param>
        public static bool AllowsManual(BotOwner botOwner, bool isSuppression, string enemyProfileId, Vector3 enemyPosition)
        {
            if (!AppliesTo(botOwner))
            {
                return true;
            }
            var data = botOwner.GetMcsBotPlayerData();
            var distance = Vector3.Distance(botOwner.Position, enemyPosition);
            return HoldFireRules.MayShootManually(isSuppression, distance, Range, TeamModes.IsHostile(data?.McsAILeadPlayer, enemyProfileId));
        }

        /// <summary>
        /// <see cref="Allows(BotOwner, string, Vector3)"/> for an enemy the game's own bot code knows (EFT's Player).
        /// </summary>
        /// <param name="botOwner">The bot about to fire.</param>
        /// <param name="enemy">The enemy; null counts as not allowed for a Passive bot.</param>
        public static bool Allows(BotOwner botOwner, IPlayer enemy)
        {
            if (!AppliesTo(botOwner))
            {
                return true;
            }
            return enemy != null && Allows(botOwner, enemy.ProfileId, enemy.Position);
        }
    }
}
