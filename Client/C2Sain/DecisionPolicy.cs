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
//      is reset so time spent under C2 does not count as chasing (while a recall
//      runs, since fork v17, only the chase timer is reset: the return state stays).
//   2. Reactive, until the squad is released (TeamModes.IsReactiveReleased: a bot or the player
//      shot at, or the player firing at an enemy): every combat and squad decision becomes None,
//      so the bot holds fire and lets enemies pass. (Reactive is what Passive was before fork v18.)
//      Passive (fork v18, ApplyPassive, rules in Core/HoldFire.cs): a decision stands only when
//      its enemy is hostile (attacked the squad first) and within the Passive range of the bot;
//      otherwise it becomes None. A decision that stands loses its chases, grenade throws, cover
//      shifts and suppressive fire. SainShootGatePatch backs this up per shot.
//   3. A grenade throw in a stance without grenades (Passive, Reactive, Defensive) becomes
//      StandAndShoot (enemy visible) or SeekCover. SainGrenadePatch already stops SAIN's thrower;
//      this is the backstop for the decision itself.
//   4. Hold Position, in every stance: decisions that move the bot are replaced by StandAndShoot
//      (enemy visible) or Freeze (stay and listen).
//   5. Aggressive and Hyper-aggressive run their tactics (EngageTactics.Apply: flank legs,
//      covering fire, or the rush).
//   6. Passive, Reactive and Defensive: an enemy the bot never saw itself (heard, or reported by a
//      squadmate or the player) may not be chased.
//   7. The chase leash (ChaseLeash), per stance: Reactive and Defensive 3-5 s per chase, cut at once
//      if another enemy appears, then no chasing until the bot is back with the player; Aggressive
//      free within the "Aggressive: leash" range of the player; Hyper-aggressive no limit; Passive
//      none at all (a net behind rule 2, which has already removed the chases).
// A vetoed solo chase becomes StandAndShoot when the enemy is visible, otherwise
// None, so SAIN's layers stand down and the Guard layer walks the bot back.

using System;
using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Datas;
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
                if (fork.BreakOff.Active)
                {
                    // Fork v17: a recall holds the bot. Forget the chase timer but keep the return state: the call below, with its
                    // dummy distance of 0, cleared it on every decision, so a recall that then gave up left nothing to bar chases.
                    fork.Leash.EndChase();
                }
                else
                {
                    fork.Leash.ShouldVeto(now, false, 0, 0f, leashSettings);
                }
                fork.FinalDecision = fork.RawDecision;
                fork.VetoReason = "C2 in charge";
                return;
            }

            string passiveReason = null;
            if (StanceRules.HoldsFireByRange(stance))
            {
                if (ApplyPassive(botOwner, data, fork, now, leashSettings, ref solo, ref squad, self, enemy, out passiveReason))
                {
                    return;
                }
            }
            else if (StanceRules.HoldsFireUntilReleased(stance) && !TeamModes.IsReactiveReleased(data.McsAILeadPlayer))
            {
                fork.Leash.ShouldVeto(now, false, 0, 0f, leashSettings);
                var holding = solo != ECombatDecision.None || squad != ESquadDecision.None;
                solo = ECombatDecision.None;
                squad = ESquadDecision.None;
                fork.FinalDecision = $"{solo}/{squad}/{self}";
                fork.VetoReason = holding ? "reactive: holding fire" : "reactive";
                return;
            }

            var soloChase = solo is ECombatDecision.RushEnemy or ECombatDecision.Search or ECombatDecision.MoveToEngage;
            var squadChase = squad is ESquadDecision.GroupSearch or ESquadDecision.Help or ESquadDecision.PushSuppressedEnemy;
            var visible = enemy != null && enemy.IsVisible;
            string reason = passiveReason;

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
        /// Fork v18: the new Passive stance's rule for one set of SAIN decisions (Core/HoldFire.cs HoldFireRules.Judge). With
        /// the enemy missing, beyond the Passive range or not hostile, both decisions become None and the call returns true
        /// (the caller stops). Otherwise the decisions lose their chases, grenade throws, cover shifts and suppressive fire
        /// and the call returns false, so the rest of the policy (Hold Position, the no-chase leash) still runs. A fault in
        /// reading the enemy counts as "no enemy": the bot holds fire. Side effects: edits the decisions, writes the bot's
        /// fork state (overlay text, the leash's chase timer) and, with the diagnostic log on, writes the "passive: ... held
        /// fire at ..." and "passive: ... engaged hostile ..." lines.
        /// </summary>
        /// <param name="botOwner">The carry bot.</param>
        /// <param name="data">Its MCS data.</param>
        /// <param name="fork">Its fork state.</param>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="leashSettings">The bot's leash settings (the no-chase kind, for Passive).</param>
        /// <param name="solo">SAIN's combat decision, edited in place.</param>
        /// <param name="squad">SAIN's squad decision, edited in place.</param>
        /// <param name="self">SAIN's self-action decision, never changed.</param>
        /// <param name="enemy">The enemy the decision is about; may be null.</param>
        /// <param name="reason">The overlay text for a decision that was changed, or null.</param>
        private static bool ApplyPassive(BotOwner botOwner, McsBotPlayerData data, BotForkState fork, float now, ChaseLeashSettings leashSettings,
            ref ECombatDecision solo, ref ESquadDecision squad, ESelfActionType self, Enemy enemy, out string reason)
        {
            reason = null;
            var range = _stanceSettings.PassiveRange;
            var exists = false;
            var distance = float.PositiveInfinity;
            var hostile = false;
            string enemyId = null;
            if (enemy != null)
            {
                try
                {
                    enemyId = enemy.EnemyProfileId;
                    distance = Vector3.Distance(botOwner.Position, enemy.EnemyPosition);
                    hostile = TeamModes.IsHostile(data.McsAILeadPlayer, enemyId);
                    exists = true;
                }
                catch (Exception e)
                {
                    if (!_loggedPassiveFault)
                    {
                        _loggedPassiveFault = true;
                        McsLogger.LogError(e);
                    }
                }
            }

            var verdict = HoldFireRules.Judge(SoloKind(solo), SquadKind(squad), exists, distance, range, hostile);
            var debug = ForkConfig.DebugLog?.Value == true;
            if (verdict.Reason != HoldReason.None)
            {
                var holding = solo != ECombatDecision.None || squad != ESquadDecision.None;
                solo = ECombatDecision.None;
                squad = ESquadDecision.None;
                fork.Leash.ShouldVeto(now, false, 0, 0f, leashSettings);
                fork.FinalDecision = $"{solo}/{squad}/{self}";
                var text = HoldFireRules.ReasonText(verdict.Reason, range);
                fork.VetoReason = holding ? "passive: " + text : "passive";
                if (holding && exists && debug && fork.PassiveLog.HeldFireDue(enemyId, now))
                {
                    McsLogger.LogInfo($"[{ForkLog.Tag}] passive: {botOwner.Profile?.Nickname} held fire at {enemy.EnemyName}, {distance:0} m away ({text})");
                }
                return true;
            }

            var visible = enemy.IsVisible;
            var proceeds = solo != ECombatDecision.None || squad != ESquadDecision.None;
            if (verdict.Solo != SoloChange.Keep || verdict.Squad != SquadChange.Keep)
            {
                reason = verdict.Solo == SoloChange.ToStandOrCover ? "passive: no grenades" : "passive: no chasing or suppressing";
            }
            switch (verdict.Solo)
            {
                case SoloChange.ToNone:
                    solo = ECombatDecision.None;
                    break;
                case SoloChange.ToStandOrCover:
                    solo = visible ? ECombatDecision.StandAndShoot : ECombatDecision.SeekCover;
                    break;
                case SoloChange.ToStandOrNone:
                    solo = visible ? ECombatDecision.StandAndShoot : ECombatDecision.None;
                    break;
            }
            if (verdict.Squad == SquadChange.ToNone)
            {
                squad = ESquadDecision.None;
            }
            if (proceeds && debug && fork.PassiveLog.FirstEngagement(enemyId))
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] passive: {botOwner.Profile?.Nickname} engaged hostile {enemy.EnemyName}, {distance:0} m away");
            }
            return false;
        }

        /// <summary>SAIN's combat decisions by number, mapped by name to Core's mirror (HfSolo); built once.</summary>
        private static readonly HfSolo[] _soloKinds = BuildKinds<ECombatDecision, HfSolo>(HfSolo.Other);

        /// <summary>SAIN's squad decisions by number, mapped by name to Core's mirror (HfSquad); built once.</summary>
        private static readonly HfSquad[] _squadKinds = BuildKinds<ESquadDecision, HfSquad>(HfSquad.Other);

        /// <summary>True once a fault reading the enemy for the Passive rule was logged (the first only).</summary>
        private static bool _loggedPassiveFault;

        /// <summary>
        /// Builds the lookup from a SAIN decision's number to the Core enum value with the same name. A SAIN name Core lacks
        /// maps to <paramref name="other"/>, which the Passive rule keeps as it is (and silences with everything else when the
        /// enemy is out of bounds).
        /// </summary>
        /// <param name="other">The Core value for a name it does not know.</param>
        private static TCore[] BuildKinds<TSain, TCore>(TCore other) where TSain : struct, Enum where TCore : struct, Enum
        {
            var values = (TSain[])Enum.GetValues(typeof(TSain));
            var highest = 0;
            foreach (var value in values)
            {
                highest = Math.Max(highest, Convert.ToInt32(value));
            }
            var map = new TCore[highest + 1];
            for (var i = 0; i < map.Length; i++)
            {
                map[i] = other;
            }
            foreach (var value in values)
            {
                if (Enum.TryParse<TCore>(value.ToString(), out var mirrored))
                {
                    map[Convert.ToInt32(value)] = mirrored;
                }
            }
            return map;
        }

        /// <summary>Core's name for a SAIN combat decision (<see cref="HfSolo.Other"/> when it has none).</summary>
        private static HfSolo SoloKind(ECombatDecision decision)
        {
            var index = (int)decision;
            return index >= 0 && index < _soloKinds.Length ? _soloKinds[index] : HfSolo.Other;
        }

        /// <summary>Core's name for a SAIN squad decision (<see cref="HfSquad.Other"/> when it has none).</summary>
        private static HfSquad SquadKind(ESquadDecision decision)
        {
            var index = (int)decision;
            return index >= 0 && index < _squadKinds.Length ? _squadKinds[index] : HfSquad.Other;
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
                _stanceSettings.PassiveRange = ForkConfig.PassiveRange.Value;
            }
            StanceRules.ApplyLeash(stance, _stanceSettings, _leashSettings);
            return _leashSettings;
        }
    }
}
