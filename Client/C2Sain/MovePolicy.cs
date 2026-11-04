// MovePolicy.cs -- when a carry bot copies the player's pose and speed while it moves.
//
// Called every frame by MCS's three movement logics that copy the player's crouch, prone and
// slow walk: GoToPointLogic (following, Go To Point, Clear Area waypoints, Break off),
// EscortToPointByWayLogic and GoToExcuteProxyActionLogic. Reads the bot's MCS intents and
// fork state and the squad's mode; reads and writes no files.
//
// Upstream copied the player's pose whenever the game's own enemy memory was empty and the bot
// was within 50 m, whatever the bot was doing. Under SAIN that memory is unreliable, which made
// the copying look random, and it made a bot clearing an area crouch because the player did.
// Rule here: copy only while moving with the player -- following, or escorting -- and never in
// combat (SAIN has the bot then anyway). A bot sent off on a job of its own (Clear Area or
// Clear Target Area, Go To Point, a loot, quest, switch or stationary-weapon task, dropping
// loot) moves at its own pace, and Break off from combat always runs.

using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Utils;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>The pose-copying rule; see the file header.</summary>
    internal static class MovePolicy
    {
        /// <summary>Intents that send a bot away from the player on a job of its own.</summary>
        private static readonly string[] OwnJobIntents =
        {
            Intents.ShouldClearArea,
            Intents.ShouldGoToPoint,
            Intents.ShouldQuestProxyAction,
            Intents.ShouldLootProxyAction,
            Intents.ShouldInteractionProxyAction,
            Intents.ShouldStationaryWeaponProxyAction,
            Intents.ShouldDropTargetLoot,
        };

        /// <summary>
        /// True when the bot should copy the player's pose and speed right now.
        /// </summary>
        /// <param name="botOwner">The moving carry bot (unused today; kept so callers need not change if the rule grows).</param>
        /// <param name="data">Its MCS data; null never copies.</param>
        /// <param name="withinRange">Upstream's range test: the bot is within 50 m of the player.</param>
        public static bool CopiesLeaderPose(BotOwner botOwner, McsBotPlayerData data, bool withinRange)
        {
            if (!withinRange || data == null || data.Fork.BreakOff.Active || data.HasAnyIntent(OwnJobIntents))
            {
                return false;
            }
            var mode = data.McsAILeadPlayer != null ? TeamModes.Get(data.McsAILeadPlayer)?.Mode : null;
            return mode != TeamMode.Combat;
        }
    }
}
