// ContactReports.cs -- "I see the enemy", and every sighting the player hands to carry bots.
//
// Report() is the handler of the top-level "I see the enemy" command (registered
// in CommandMgr.Start); CommandUtils.Dispatch calls it once per living carry bot,
// all in one frame. ShareSighting() is also called by McsAILeadPlayer.CalcGoalEnemy,
// MCS's once-a-second hand-over of the enemy nearest the player's aim. Writes
// SAIN's enemy records (through SainBridge), TeamModes' contact report and the
// squad's group enemy list. Reads and writes no files.
//
// A report finds the enemy nearest the crosshair ray: within MAX_RANGE m, within
// a tolerance that widens with distance, and visible from the player's eyes past
// walls and terrain (bushes do not count against the player). The enemy is handed
// to SAIN as a squadmate sighting at its real position, so bots know where it is
// but have not seen it themselves: the chase rules still apply and they shoot only
// when they see it. The squad also switches to Guard at the player with spots and
// lookouts facing the mark, which stays the guards' focus for 30 s (TeamModes).
// (Before fork v11 a setting could also give each bot Engage Enemy; the stances replaced it.)

using Comfort.Common;
using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Models;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Contact reports and sighting hand-over. Main thread only.</summary>
    internal static class ContactReports
    {
        /// <summary>Farthest enemy (m along the crosshair ray) a report can pick.</summary>
        private const float MAX_RANGE = 300f;

        /// <summary>How far (m) an enemy may be off the crosshair ray right in front of the player.</summary>
        private const float RAY_TOLERANCE = 2.5f;

        /// <summary>Extra tolerance per metre of distance (0.03 m/m, about 1.7 degrees).</summary>
        private const float RAY_TOLERANCE_PER_METRE = 0.03f;

        /// <summary>Frame of the report being handled, so the squad-wide part runs once per report.</summary>
        private static int _reportFrame = -1;

        /// <summary>The enemy the current report found (null if none) and the marked position.</summary>
        private static Player _reportEnemy;
        private static Vector3 _reportMark;

        private static McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        /// <summary>
        /// Handles "I see the enemy" for one bot. The first call in a frame finds the enemy and records
        /// the report for the squad (and the first bot answers); every call shares the sighting with
        /// its bot. Side effects: see the file header.
        /// </summary>
        /// <param name="ctx">The command context; Position is where the crosshair ray hit.</param>
        public static void Report(McsCommandContext ctx)
        {
            var lead = ctx?.McsLeadPlayer;
            var botOwner = ctx?.McsBotPlayer?.AIData?.BotOwner;
            var data = botOwner?.GetMcsBotPlayerData();
            if (lead == null || data?.McsAILeadPlayer == null)
            {
                return;
            }

            var firstForReport = Time.frameCount != _reportFrame;
            if (firstForReport)
            {
                _reportFrame = Time.frameCount;
                _reportEnemy = FindEnemyNearAim(lead);
                _reportMark = _reportEnemy != null ? _reportEnemy.Position : ctx.Position ?? lead.Position;
                if (_reportEnemy != null)
                {
                    lead.BotsGroup?.AddEnemy(_reportEnemy, EBotEnemyCause.callForHelp2);
                }
                TeamModes.ReportContact(data.McsAILeadPlayer, _reportMark, lead.Position);
                botOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.Roger,
                    Keys = [_reportEnemy != null ? "McsFork/ContactMarked" : "McsFork/WatchingSpot"],
                });
                if (ForkConfig.DebugLog?.Value == true)
                {
                    McsLogger.LogInfo($"[{ForkLog.Tag}] contact report at {_reportMark}: {(_reportEnemy != null ? _reportEnemy.Profile?.Nickname : "no enemy near the crosshair")}");
                }
            }

            if (_reportEnemy == null)
            {
                return;
            }
            var bot = SainBridge.GetSain(botOwner);
            if (bot != null)
            {
                SainBridge.ShareSighting(bot, _reportEnemy, _reportEnemy.Position);
            }
        }

        /// <summary>
        /// Hands an enemy the player can see to one carry bot. For a bot SAIN manages, it becomes a
        /// squadmate sighting at the enemy's position (and the guards' look target for a few seconds);
        /// returns true so the caller skips upstream MCS's forced "visible" goal enemy. Returns false
        /// for a bot without SAIN, leaving upstream behaviour in place.
        /// </summary>
        /// <param name="botOwner">The carry bot.</param>
        /// <param name="enemy">The enemy the player sees.</param>
        public static bool ShareSighting(BotOwner botOwner, Player enemy)
        {
            var bot = SainBridge.GetSain(botOwner);
            if (bot == null || enemy == null)
            {
                return false;
            }
            SainBridge.ShareSighting(bot, enemy, enemy.Position);
            var lead = botOwner.GetMcsBotPlayerData()?.McsAILeadPlayer;
            if (lead != null)
            {
                TeamModes.RecordSighting(lead, enemy.Position);
            }
            return true;
        }

        /// <summary>
        /// The living enemy AI closest to the player's crosshair ray, within range and tolerance and
        /// with its head or chest visible from the player's eyes; null when there is none. Skips carry
        /// bots, MCS's forbidden targets and, for a Scav player, Scavs that are not hostile.
        /// </summary>
        private static Player FindEnemyNearAim(Player lead)
        {
            var ray = lead.InteractionRay;
            var direction = ray.direction.normalized;
            Player best = null;
            var bestScore = float.MaxValue;
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld?.AllAlivePlayersList == null)
            {
                return null;
            }

            foreach (var target in gameWorld.AllAlivePlayersList)
            {
                if (target == null || target == lead || !target.IsAI || target.HealthController == null || !target.HealthController.IsAlive)
                {
                    continue;
                }
                if (McsMgr.IsMcsBotPlayer(target.ProfileId) || Tools.IsForbiddenEnemy(target))
                {
                    continue;
                }
                if (lead.Side == EPlayerSide.Savage && target.Side == EPlayerSide.Savage && lead.BotsGroup != null && !lead.BotsGroup.IsEnemy(target))
                {
                    continue;
                }

                var chest = target.Position + Vector3.up * 1.2f;
                var toTarget = chest - ray.origin;
                var along = Vector3.Dot(toTarget, direction);
                if (along < 1f || along > MAX_RANGE)
                {
                    continue;
                }
                var offRay = (toTarget - direction * along).magnitude;
                var tolerance = RAY_TOLERANCE + along * RAY_TOLERANCE_PER_METRE;
                if (offRay > tolerance)
                {
                    continue;
                }
                var mask = LayersMaskController.HighPolyWithTerrainMask;
                var head = target.Position + Vector3.up * 1.55f;
                if (Physics.Linecast(ray.origin, head, mask) && Physics.Linecast(ray.origin, chest, mask))
                {
                    continue;
                }
                var score = offRay / tolerance;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = target;
                }
            }
            return best;
        }
    }
}
