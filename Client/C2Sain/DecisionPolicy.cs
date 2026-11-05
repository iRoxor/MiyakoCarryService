// DecisionPolicy.cs -- rewrites SAIN's decisions for carry bots.
//
// Called by SainSetDecisionsPatch, a prefix on SAIN's private
// BotDecisionManager.SetDecisions, about ten times a second per bot. SAIN has
// no supported way to override decisions (its force settings are DEBUG-only in
// 4.5.1), so this is the one point that sees every decision. Writes the bot's
// fork state (leash, overlay text) and may stop SAIN's search. Reads and
// writes no files.
//
// Policy, in order:
//   0. The bot's SAIN personality is kept at what it should be (EngageTactics):
//      lent by an Engage order, else from the allowed-personalities setting.
//   1. While the C2 layer is active SAIN's decisions are not executed; the leash
//      is reset so time spent under C2 does not count as chasing.
//   2. An Engage order (EngageTactics) replaces rules 3-5 while it runs: no
//      leash, flank legs, covering fire.
//   3. Hold Position: decisions that move the bot are replaced by StandAndShoot
//      (enemy visible) or Freeze (stay and listen).
//   4. An enemy the bot never saw itself (heard, or reported by a squadmate or
//      the player) may not be chased.
//   5. The chase leash (ChaseLeash): 3-5 s per chase, cut at once if another
//      enemy appears, then no chasing until the bot is back with the player.
// A vetoed solo chase becomes StandAndShoot when the enemy is visible, otherwise
// None, so SAIN's layers stand down and the Guard layer walks the bot back.

using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Utils;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>The fork's rules for SAIN decisions on carry bots.</summary>
    internal static class DecisionPolicy
    {
        private static readonly ChaseLeashSettings _leashSettings = new();

        /// <summary>
        /// Applies the policy to one set of decisions, editing them in place.
        /// </summary>
        /// <param name="bot">SAIN's component for the bot whose decisions these are.</param>
        /// <param name="solo">SAIN's combat decision.</param>
        /// <param name="squad">SAIN's squad decision.</param>
        /// <param name="self">SAIN's self-action decision (heal, reload); never changed here.</param>
        /// <param name="enemy">The enemy the decision is about; may be null.</param>
        public static void Apply(BotComponent bot, ref ECombatDecision solo, ref ESquadDecision squad, ref ESelfActionType self, Enemy enemy)
        {
            var botOwner = bot?.BotOwner;
            if (botOwner == null || !botOwner.IsMcsBotPlayer)
            {
                return;
            }

            var data = botOwner.GetMcsBotPlayerData();
            if (data == null)
            {
                return;
            }

            var fork = data.Fork;
            var now = Time.time;
            fork.RawDecision = $"{solo}/{squad}/{self}";
            EngageTactics.EnsurePersonality(bot, fork);

            if (fork.C2Active)
            {
                fork.Leash.ShouldVeto(now, false, 0, 0f, RefreshLeashSettings());
                fork.FinalDecision = fork.RawDecision;
                fork.VetoReason = "C2 in charge";
                return;
            }

            if (EngageTactics.Apply(bot, botOwner, data, ref solo, ref squad, enemy, now, out var engageReason))
            {
                fork.FinalDecision = $"{solo}/{squad}/{self}";
                fork.VetoReason = engageReason ?? "engaging";
                return;
            }

            var soloChase = solo is ECombatDecision.RushEnemy or ECombatDecision.Search or ECombatDecision.MoveToEngage;
            var squadChase = squad is ESquadDecision.GroupSearch or ESquadDecision.Help or ESquadDecision.PushSuppressedEnemy;
            var isChase = soloChase || squadChase;
            var visible = enemy != null && enemy.IsVisible;
            string reason = null;

            if (data.HasIntent(Intents.ShouldHoldPosition))
            {
                var soloMoves = soloChase || solo is ECombatDecision.ShiftCover or ECombatDecision.SeekCover or ECombatDecision.Retreat;
                if (soloMoves)
                {
                    solo = visible ? ECombatDecision.StandAndShoot : ECombatDecision.Freeze;
                    reason = "holding position";
                }
                if (squadChase)
                {
                    squad = ESquadDecision.None;
                    reason = "holding position";
                }
            }
            else
            {
                var heardOnly = enemy != null && !enemy.Seen;
                var lead = data.LeadPlayer;
                var distance = lead != null ? Vector3.Distance(botOwner.Position, lead.Position) : 0f;
                var wasReturning = fork.Leash.Returning;
                // A heard-only chase is vetoed outright, so it must not start the leash's timer.
                var leashVeto = fork.Leash.ShouldVeto(now, isChase && !heardOnly, SainBridge.CountKnownEnemies(bot), distance, RefreshLeashSettings());

                if (isChase && (heardOnly || leashVeto))
                {
                    if (soloChase)
                    {
                        solo = visible ? ECombatDecision.StandAndShoot : ECombatDecision.None;
                    }
                    if (squadChase)
                    {
                        squad = ESquadDecision.None;
                    }
                    reason = heardOnly ? "not seen by the bot" : fork.Leash.GraceLeft(now) > 0f ? "break-off grace" : "chase limit";

                    if (fork.Leash.Returning && !wasReturning)
                    {
                        SainBridge.StopSearch(bot, enemy);
                        if (ForkConfig.DebugLog.Value)
                        {
                            McsLogger.LogInfo($"[{ForkLog.Tag}] {botOwner.Profile?.Nickname}: chase cut at {distance:0} m from the player");
                        }
                    }
                }
            }

            fork.FinalDecision = $"{solo}/{squad}/{self}";
            fork.VetoReason = reason ?? "";
        }

        /// <summary>Copies the BepInEx leash values into the shared settings object and returns it.</summary>
        private static ChaseLeashSettings RefreshLeashSettings()
        {
            if (ForkConfig.ChaseMinSeconds != null)
            {
                _leashSettings.ChaseMinSeconds = ForkConfig.ChaseMinSeconds.Value;
                _leashSettings.ChaseMaxSeconds = ForkConfig.ChaseMaxSeconds.Value;
                _leashSettings.ReturnRadius = ForkConfig.ReturnRadius.Value;
            }
            return _leashSettings;
        }
    }
}
