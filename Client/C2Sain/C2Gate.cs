// C2Gate.cs -- decides, every brain tick, whether the C2 layer (McsBrainLayer)
// runs or steps aside for SAIN and the Guard layer. Also decides which brain
// layers survive MCS's layer stripping.
//
// Called from McsBrainLayer.IsActive and LayerUtils.McsRemoveNonKeepLayers.
// Writes BotForkState.C2Active / C2Reason and ends Break off (starting the
// chase leash's grace period). Reads and writes no files.
//
// Layer stack on a carry bot after injection:
//   200 C2 (McsBrainLayer)      -- this gate's verdict
//   85/80/22/20 SAIN layers     -- active when SAIN has a decision
//   80 AvoidDanger, 78 Malfunction (game layers SAIN bots keep)
//   19 Guard (McsGuardLayer)    -- the floor when nothing above is active

using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>The C2-versus-SAIN decision for one bot.</summary>
    internal static class C2Gate
    {
        /// <summary>
        /// Intents that keep the C2 layer in charge outside combat: commands with a destination
        /// or a job, and Hold Position. In combat they wait, as upstream MCS deferred them.
        /// </summary>
        private static readonly string[] C2TaskIntents =
        {
            Intents.ShouldTeleport,
            Intents.ShouldEscort,
            Intents.ShouldEscortToBtr,
            Intents.ShouldExfil,
            Intents.ShouldGoToPoint,
            Intents.ShouldHoldPosition,
            Intents.ShouldQuestProxyAction,
            Intents.ShouldLootProxyAction,
            Intents.ShouldInteractionProxyAction,
            Intents.ShouldStationaryWeaponProxyAction,
            Intents.ShouldDropTargetLoot,
            Intents.ShouldClearArea,
            Intents.ShouldUseStationaryWeapon,
        };

        /// <summary>
        /// Whether a brain layer must survive MCS's stripping: every SAIN layer except Extract
        /// (bots would leave the raid on their own) and Debug, plus the game's AvoidDanger and
        /// Malfunction layers, which normal SAIN bots keep.
        /// </summary>
        /// <param name="layerName">The layer's Name() as the brain reports it.</param>
        public static bool ShouldKeepLayer(string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
            {
                return false;
            }
            if (layerName == "AvoidDanger" || layerName == "Malfunction")
            {
                return true;
            }
            return layerName.StartsWith("SAIN : ") && !layerName.Contains("Extract") && !layerName.Contains("Debug");
        }

        /// <summary>
        /// Whether the C2 layer should be the active layer for this bot right now.
        /// Side effects: ends a finished Break off (and starts the no-chase grace), records the
        /// verdict and reason on the bot's fork state.
        /// </summary>
        /// <param name="botOwner">The carry bot.</param>
        /// <param name="data">Its MCS data; null keeps upstream behaviour (C2 always).</param>
        public static bool WantsC2(BotOwner botOwner, McsBotPlayerData data)
        {
            var result = Decide(botOwner, data, out var reason);
            if (data != null)
            {
                data.Fork.C2Active = result;
                data.Fork.C2Reason = reason;
            }
            return result;
        }

        /// <summary>The decision itself; see <see cref="WantsC2"/>.</summary>
        private static bool Decide(BotOwner botOwner, McsBotPlayerData data, out string reason)
        {
            if (data?.McsAILeadPlayer == null || data.LeadPlayer == null)
            {
                reason = "no lead";
                return true;
            }

            if (SainBridge.GetSain(botOwner) == null)
            {
                reason = "no SAIN";
                return true;
            }

            var fork = data.Fork;
            var now = Time.time;
            if (fork.BreakOff.Active)
            {
                var distance = Vector3.Distance(botOwner.Position, data.LeadPlayer.Position);
                if (fork.BreakOff.Update(now, distance, ForkConfig.BreakOffArriveRadius.Value, ForkConfig.BreakOffTimeout.Value))
                {
                    fork.Leash.StartGrace(now, ForkConfig.BreakOffGrace.Value);
                }
                else
                {
                    reason = "break off";
                    return true;
                }
            }

            var mode = TeamModes.Get(data.McsAILeadPlayer)?.Mode ?? TeamMode.C2;
            switch (mode)
            {
                case TeamMode.C2:
                    reason = "following";
                    return true;
                case TeamMode.Combat:
                    reason = "combat";
                    return false;
            }

            if (data.HasAnyIntent(C2TaskIntents))
            {
                reason = "command";
                return true;
            }
            if (data.IsLooting)
            {
                reason = "looting";
                return true;
            }
            if (NeedsHeal(botOwner))
            {
                reason = "healing";
                return true;
            }

            reason = "guarding";
            return false;
        }

        /// <summary>Same test upstream MCS uses before choosing its heal logic.</summary>
        private static bool NeedsHeal(BotOwner botOwner)
        {
            var medecine = botOwner.Medecine;
            if (medecine == null)
            {
                return false;
            }
            return (medecine.FirstAid.Damaged && medecine.FirstAid.HaveSmth2Use)
                || (medecine.SurgicalKit.Damaged && medecine.SurgicalKit.HaveSmth2Use);
        }
    }
}
