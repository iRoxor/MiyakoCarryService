// DecisionPolicy.cs -- rewrites SAIN's decisions for carry bots.
//
// Called by SainSetDecisionsPatch, a prefix on SAIN's private
// BotDecisionManager.SetDecisions, about ten times a second per bot. SAIN has
// no supported way to override decisions (its force settings are DEBUG-only in
// 4.5.1), so this is the one point that sees every decision. Writes the bot's
// fork state (leash, overlay text) and may stop SAIN's search. Reads and
// writes no files.
//
// Policy, in order (since fork v11 the bot's stance, Core/Stance.cs, decides much of it):
//   0. The bot's SAIN side is kept in line with its stance (EngageTactics.EnsureStance):
//      personality, search delay, aggressive tactics on or off.
//   1. While the C2 layer is active SAIN's decisions are not executed; the leash
//      is reset so time spent under C2 does not count as chasing.
//   2. Passive, until the squad is released (TeamModes.IsPassiveReleased: a bot or the player
//      shot at, or the player firing at an enemy): every combat and squad decision becomes None,
//      so the bot holds fire and lets enemies pass.
//   3. A grenade throw in a stance without grenades (Passive, Defensive) becomes StandAndShoot
//      (enemy visible) or SeekCover. SainGrenadePatch already stops SAIN's thrower; this is the
//      backstop for the decision itself.
//   4. Hold Position, in every stance: decisions that move the bot are replaced by StandAndShoot
//      (enemy visible) or Freeze (stay and listen).
//   5. Aggressive and Hyper-aggressive run their tactics (EngageTactics.Apply: flank legs,
//      covering fire, or the rush).
//   6. Passive and Defensive: an enemy the bot never saw itself (heard, or reported by a squadmate
//      or the player) may not be chased.
//   7. The chase leash (ChaseLeash), per stance: Passive and Defensive 3-5 s per chase, cut at once
//      if another enemy appears, then no chasing until the bot is back with the player; Aggressive
//      free within the "Aggressive: leash" range of the player; Hyper-aggressive no limit.
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
        /// <summary>The leash tunables, refreshed from the settings and the bot's stance before each use.</summary>
        private static readonly ChaseLeashSettings _leashSettings = new();

        /// <summary>The stance tunables, refreshed from the settings before each use.</summary>
        private static readonly StanceSettings _stanceSettings = new();

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
            var stance = EngageTactics.EnsureStance(bot, botOwner, fork, now);
            var leashSettings = RefreshSettings(stance);

            if (fork.C2Active)
            {
                fork.Leash.ShouldVeto(now, false, 0, 0f, leashSettings);
                fork.FinalDecision = fork.RawDecision;
                fork.VetoReason = "C2 in charge";
                return;
            }

            if (stance == CarryStance.Passive && !TeamModes.IsPassiveReleased(data.McsAILeadPlayer))
            {
                fork.Leash.ShouldVeto(now, false, 0, 0f, leashSettings);
                var holding = solo != ECombatDecision.None || squad != ESquadDecision.None;
                solo = ECombatDecision.None;
                squad = ESquadDecision.None;
                fork.FinalDecision = $"{solo}/{squad}/{self}";
                fork.VetoReason = holding ? "passive: holding fire" : "passive";
                return;
            }

            var soloChase = solo is ECombatDecision.RushEnemy or ECombatDecision.Search or ECombatDecision.MoveToEngage;
            var squadChase = squad is ESquadDecision.GroupSearch or ESquadDecision.Help or ESquadDecision.PushSuppressedEnemy;
            var visible = enemy != null && enemy.IsVisible;
            string reason = null;

            if (solo == ECombatDecision.ThrowGrenade && !StanceRules.AllowsGrenades(stance))
            {
                solo = visible ? ECombatDecision.StandAndShoot : ECombatDecision.SeekCover;
                reason = $"no grenades ({stance})";
            }

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
                if (EngageTactics.Apply(bot, botOwner, data, ref solo, ref squad, enemy, now, out var tacticsReason))
                {
                    reason = tacticsReason ?? reason;
                    soloChase = solo is ECombatDecision.RushEnemy or ECombatDecision.Search or ECombatDecision.MoveToEngage;
                    squadChase = squad is ESquadDecision.GroupSearch or ESquadDecision.Help or ESquadDecision.PushSuppressedEnemy;
                }
                var isChase = soloChase || squadChase;
                var heardOnly = enemy != null && !enemy.Seen && !StanceRules.ChasesUnseen(stance);
                var lead = data.LeadPlayer;
                var distance = lead != null ? Vector3.Distance(botOwner.Position, lead.Position) : 0f;
                var wasReturning = fork.Leash.Returning;
                // A heard-only chase is vetoed outright, so it must not start the leash's timer.
                var leashVeto = fork.Leash.ShouldVeto(now, isChase && !heardOnly, SainBridge.CountKnownEnemies(bot), distance, leashSettings);

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
                    reason = heardOnly ? "not seen by the bot"
                        : fork.Leash.GraceLeft(now) > 0f ? "break-off grace"
                        : leashSettings.RangeLimit > 0f ? $"leash {leashSettings.RangeLimit:0} m"
                        : "chase limit";

                    if (fork.Leash.Returning && !wasReturning)
                    {
                        SainBridge.StopSearch(bot, enemy);
                        if (ForkConfig.DebugLog.Value)
                        {
                            McsLogger.LogInfo($"[{ForkLog.Tag}] {botOwner.Profile?.Nickname}: chase cut at {distance:0} m from the player ({stance})");
                        }
                    }
                }
            }

            fork.FinalDecision = $"{solo}/{squad}/{self}";
            fork.VetoReason = reason ?? "";
        }

        /// <summary>
        /// Copies the BepInEx leash and stance values into the shared settings objects, sets the leash's
        /// range for <paramref name="stance"/>, and returns the leash settings.
        /// </summary>
        private static ChaseLeashSettings RefreshSettings(CarryStance stance)
        {
            if (ForkConfig.ChaseMinSeconds != null)
            {
                _leashSettings.ChaseMinSeconds = ForkConfig.ChaseMinSeconds.Value;
                _leashSettings.ChaseMaxSeconds = ForkConfig.ChaseMaxSeconds.Value;
                _leashSettings.ReturnRadius = ForkConfig.ReturnRadius.Value;
                _stanceSettings.AggressiveLeash = ForkConfig.AggressiveLeash.Value;
            }
            StanceRules.ApplyLeash(stance, _stanceSettings, _leashSettings);
            return _leashSettings;
        }
    }
}
