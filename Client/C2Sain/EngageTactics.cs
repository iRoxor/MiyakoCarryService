// EngageTactics.cs -- applies each carry bot's combat stance: its SAIN personality, its search delay and,
// in the aggressive stances, the flank-and-cover or rush tactics.
//
// Since fork v11 the stance (Core/Stance.cs) drives everything here; the Engage Enemy orders, the
// allowed-personalities setting and the order time limits are gone. EnsureStance and Apply are called by
// DecisionPolicy from SAIN's decision prefix, about ten times a second per bot; CurrentStance by
// DecisionPolicy, ForkCommands, SainGrenadePatch and ForkDebug. Writes the bot's fork state (applied
// stance, tactics, flank leg, role) and, through SainBridge, SAIN's personality and search delay for the
// bot. Reads and writes no files.
//
// Per stance (the user's spec, 2026-11-08, and mapping choice, 2026-11-09):
//   - Passive (fork v18), Reactive (the v11 Passive) and Defensive: SAIN's Normal personality, SAIN's own search delay, no tactics.
//   - Aggressive: Chad (in the user's preset: searches after ~16 s, pushes enemies that are reloading or
//     hurt), search delay cut to TACTICAL_SEARCH_DELAY, and the tactics the old Engage Enemy ran:
//       - roles among the squad's Aggressive bots, ordered by profile id: a lone bot flanks (the player
//         is the base of fire); with two or more, the first is the base of fire and the rest flank,
//         alternating sides;
//       - base of fire: while any flanker is moving it does not advance; it lays SAIN's suppressing fire
//         when SAIN has a suppression point, otherwise holds in cover;
//       - flanker: walks a flank leg (run by McsGuardLayer) to a point picked by C2Nav.TryPickFlankSpot,
//         then SAIN searches the last known position from there. SAIN has no flanking of its own
//         (checked in SAIN 4.5.1). Since fork v17 the leash bounds the legs (Core/LeashRules.cs): none is planned
//         during a recall's no-chase grace, while the chase leash is returning the bot, when the bot is past 0.8 of the
//         leash range from the player or the enemy past the whole range; a leg's end lies within 0.8 of the range;
//         a running leg ends as Leash past the range and is dropped when a recall begins or its grace runs.
//   - Hyper-aggressive: GigaChad or Wreckless (the reckless-personality setting), search delay cut to
//     RECKLESS_SEARCH_DELAY; SAIN does the rest, as the old Engage Enemy Wrecklessly did.
// The leash per stance is applied by DecisionPolicy, not here.

using System.Collections.Generic;
using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Models;
using MiyakoCarryService.Client.Utils;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.Preset.Shared.Models.Preset.Personalities;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Stance personalities and the aggressive stances' tactics. Main thread only.</summary>
    internal static class EngageTactics
    {
        /// <summary>Search delay (s) in the Aggressive stance: a short pause, then the approach.</summary>
        private const float TACTICAL_SEARCH_DELAY = 3f;

        /// <summary>Search delay (s) in the Hyper-aggressive stance: straight in, as Wreckless's preset value 0.1 s.</summary>
        private const float RECKLESS_SEARCH_DELAY = 0.1f;

        /// <summary>Seconds after a hit or a near miss during which a flank leg counts as under fire.</summary>
        private const float UNDER_FIRE_WINDOW = 1.5f;

        /// <summary>Metres to the flank point beyond which the bot sprints the leg.</summary>
        public const float FLANK_SPRINT_DISTANCE = 15f;

        /// <summary>Lowest ammo ratio at which the base of fire lays suppressing fire.</summary>
        private const float SUPPRESS_MIN_AMMO = 0.3f;

        private static readonly EngageSettings _settings = new();
        private static readonly List<BotOwner> _orderedBots = new();

        /// <summary>Fork v17: the leash-near-flank-legs tunables (Core/LeashRules.cs), the defaults; shared with C2Gate's leash pull.</summary>
        private static readonly LeashRuleSettings _leashRules = new();

        /// <summary>Fork v17: the stance tunables RangeLimit reads, refreshed from the "Aggressive: leash" setting before each use.</summary>
        private static readonly StanceSettings _rangeStance = new();

        private static McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        /// <summary>Fork v17: the leash-near-flank-legs tunables (Core/LeashRules.cs), read by C2Gate for the leash pull.</summary>
        internal static LeashRuleSettings LeashTunables => _leashRules;

        /// <summary>Fork v17: the share of the leash range a Leash recall brings the bot back to (StanceSettings.ReturnShare, 0.5).</summary>
        internal static float ReturnShare => _rangeStance.ReturnShare;

        /// <summary>
        /// The bot's stance now: its own override when it has one (this raid only), otherwise the squad
        /// stance in the "Stance" setting (Defensive before the settings are bound).
        /// </summary>
        public static CarryStance CurrentStance(BotForkState fork)
        {
            return StanceRules.Effective(ForkConfig.Stance?.Value ?? CarryStance.Defensive, fork?.StanceOverride);
        }

        /// <summary>
        /// Fork v17: the stance's leash range from the player (m): the "Aggressive: leash" setting for Aggressive, float.PositiveInfinity
        /// for Hyper-aggressive, 0 for Passive, Reactive and Defensive (the timed leash; Passive's is the no-chase kind). The flank rules, the flank leg's leash test and the
        /// leash pull (C2Gate) read it.
        /// </summary>
        public static float RangeLimit(CarryStance stance)
        {
            _rangeStance.AggressiveLeash = ForkConfig.AggressiveLeash?.Value ?? _rangeStance.AggressiveLeash;
            return StanceRules.RangeLimit(stance, _rangeStance);
        }

        /// <summary>
        /// Keeps the bot's SAIN side in line with its stance, on every decision: when the stance changed
        /// since the last call, picks its personality (GigaChad or Wreckless drawn once for
        /// Hyper-aggressive), sets or restores the search delay, starts or ends the aggressive tactics,
        /// and logs the change with the diagnostic log on. On every call it puts the personality back if
        /// SAIN changed it (SAIN re-rolls personalities when its preset is reloaded). Returns the stance.
        /// </summary>
        public static CarryStance EnsureStance(BotComponent bot, BotOwner botOwner, BotForkState fork, float now)
        {
            var stance = CurrentStance(fork);
            var current = SainBridge.GetPersonality(bot);
            if (current != null && !fork.HasSainRoll)
            {
                fork.HasSainRoll = true;
                fork.SainRolledPersonality = current.Value;
            }

            if (!fork.HasStance || fork.Stance != stance)
            {
                var first = !fork.HasStance;
                var previous = fork.Stance;
                fork.HasStance = true;
                fork.Stance = stance;
                fork.StancePersonality = StanceRules.Personality(stance) switch
                {
                    StancePersonality.Chad => EPersonality.Chad,
                    StancePersonality.Reckless => PickRecklessPersonality(),
                    _ => EPersonality.Normal,
                };
                var kind = StanceRules.Tactics(stance);
                if (kind == EngageKind.None)
                {
                    fork.Engage.Cancel();
                    fork.EngageRole = "";
                    SainBridge.RestoreTimeBeforeSearch(bot);
                }
                else
                {
                    if (fork.Engage.Kind != kind)
                    {
                        fork.Engage.Begin(kind, now);
                        fork.EngageRole = "";
                    }
                    SainBridge.SetTimeBeforeSearch(bot, kind == EngageKind.Tactical ? TACTICAL_SEARCH_DELAY : RECKLESS_SEARCH_DELAY);
                }
                LogDebug(botOwner, first
                    ? $"stance {stance} ({fork.StancePersonality}; SAIN rolled {(fork.HasSainRoll ? fork.SainRolledPersonality.ToString() : "?")})"
                    : $"stance {previous} -> {stance} ({fork.StancePersonality})");
            }

            if (current != null && current.Value != fork.StancePersonality)
            {
                SainBridge.TryApplyPersonality(bot, fork.StancePersonality);
            }
            return stance;
        }

        /// <summary>
        /// Applies the aggressive stances' tactics to one set of SAIN decisions, editing them in place.
        /// Returns true while the bot's stance runs tactics (Aggressive or Hyper-aggressive), so the
        /// caller skips the rules for the cautious stances. Side effects: plans and advances flank legs,
        /// records the bot's role, and re-applies the shortened search delay.
        /// </summary>
        /// <param name="bot">SAIN's component for the bot.</param>
        /// <param name="botOwner">The carry bot.</param>
        /// <param name="data">Its MCS data.</param>
        /// <param name="solo">SAIN's combat decision.</param>
        /// <param name="squad">SAIN's squad decision.</param>
        /// <param name="enemy">The enemy SAIN is deciding about; may be null.</param>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="reason">Overlay text describing what the tactics did, or null.</param>
        public static bool Apply(BotComponent bot, BotOwner botOwner, McsBotPlayerData data,
            ref ECombatDecision solo, ref ESquadDecision squad, Enemy enemy, float now, out string reason)
        {
            reason = null;
            var fork = data.Fork;
            var order = fork.Engage;
            if (!order.Active)
            {
                fork.EngageRole = "";
                return false;
            }

            SainBridge.SetTimeBeforeSearch(bot, order.Kind == EngageKind.Tactical ? TACTICAL_SEARCH_DELAY : RECKLESS_SEARCH_DELAY);
            if (order.Kind == EngageKind.Reckless)
            {
                fork.EngageRole = "rush";
                reason = "hyper-aggressive: rush";
                return true;
            }

            var visible = enemy != null && enemy.IsVisible;
            var underFire = SainBridge.IsUnderFire(data, now, UNDER_FIRE_WINDOW);

            // Fork v17: the leash. The tactical kind is the Aggressive stance, whose range is the "Aggressive: leash" setting.
            var lead = data.LeadPlayer;
            var botToPlayer = lead != null ? Vector3.Distance(botOwner.Position, lead.Position) : 0f;
            var rangeLimit = RangeLimit(CurrentStance(fork));
            var graceRunning = fork.Leash.GraceLeft(now) > 0f;

            if (order.FlankActive)
            {
                var flankPoint = ToVector3(order.FlankPoint);
                FlankEnd flankEnd;
                if (graceRunning)
                {
                    // A recall just ended: no flank leg is continued during its no-chase time (before v17 the grace saw only chases).
                    order.EndFlank(FlankEnd.Recall);
                    flankEnd = FlankEnd.Recall;
                }
                else
                {
                    flankEnd = order.UpdateFlank(now, Vector3.Distance(botOwner.Position, flankPoint), visible, underFire, botToPlayer, rangeLimit, _settings);
                }
                if (flankEnd == FlankEnd.None)
                {
                    solo = ECombatDecision.None;
                    squad = ESquadDecision.None;
                    fork.EngageRole = order.FlankSide < 0 ? "flank L" : "flank R";
                    reason = "aggressive: flanking";
                    return true;
                }
                LogDebug(botOwner, $"flank leg ended: {flankEnd}");
            }

            GetRole(botOwner, data, out var index, out var count, out var anyFlanking);
            if (count >= 2 && index == 0)
            {
                var wasCovering = fork.EngageRole == "cover";
                fork.EngageRole = "cover";
                reason = "aggressive: base of fire";
                if (!visible && anyFlanking)
                {
                    if (enemy?.SuppressionTarget != null && bot.Decision.SelfActionDecisions.AmmoRatio >= SUPPRESS_MIN_AMMO)
                    {
                        squad = ESquadDecision.Suppress;
                        reason = "aggressive: suppressing";
                    }
                    else if (solo is ECombatDecision.Search or ECombatDecision.RushEnemy or ECombatDecision.MoveToEngage or ECombatDecision.None)
                    {
                        solo = ECombatDecision.SeekCover;
                        reason = "aggressive: holding for the flank";
                    }
                    if (squad is ESquadDecision.GroupSearch or ESquadDecision.Help or ESquadDecision.PushSuppressedEnemy)
                    {
                        squad = ESquadDecision.None;
                    }
                }
                if (!wasCovering)
                {
                    Say(botOwner, EPhraseTrigger.Covering, "McsFork/CoveringFire");
                }
                return true;
            }

            fork.EngageRole = "push";
            reason = "aggressive: push";
            var lastKnown = enemy?.KnownPlaces?.LastKnownPosition;
            if (ForkConfig.EngageFlanking.Value && lastKnown.HasValue && !visible && !underFire)
            {
                var anchor = ToV3(lastKnown.Value);
                var enemyToPlayer = lead != null ? Vector3.Distance(lastKnown.Value, lead.Position) : 0f;
                // Fork v17: no leg while the grace runs, while the leash is bringing the bot back, or near the edge of the range.
                if (order.ShouldPlanFlank(anchor, _settings)
                    && LeashRules.CanPlanFlank(graceRunning, fork.Leash.Returning, botToPlayer, enemyToPlayer, rangeLimit, _leashRules))
                {
                    var preferredSide = count >= 2 ? (index % 2 == 1 ? -1 : 1) : 0;
                    var playerPos = lead != null ? lead.Position : botOwner.Position;
                    var spotLimit = lead != null ? LeashRules.FlankSpotLimit(rangeLimit, _leashRules) : float.PositiveInfinity;
                    if (C2Nav.TryPickFlankSpot(botOwner.Position, lastKnown.Value, preferredSide, playerPos, spotLimit, out var spot, out var side))
                    {
                        order.BeginFlank(ToV3(spot), anchor, side, now);
                        solo = ECombatDecision.None;
                        squad = ESquadDecision.None;
                        fork.EngageRole = side < 0 ? "flank L" : "flank R";
                        reason = "aggressive: flank start";
                        Say(botOwner, EPhraseTrigger.Gogogo, side < 0 ? "McsFork/FlankLeft" : "McsFork/FlankRight");
                        LogDebug(botOwner, $"flanking {(side < 0 ? "left" : "right")} to {spot} against {lastKnown.Value}");
                    }
                    else
                    {
                        order.MarkFlankUnavailable(anchor);
                        LogDebug(botOwner, "no flank route; pushing straight");
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Whether the bot is walking a flank leg, which keeps the C2 layer off whatever the squad's mode
        /// (called by C2Gate). Also ends a leg that no SAIN decision is ticking: on arrival, after the
        /// leg's time limit, when the bot's stance no longer runs tactics, and, since fork v17, when the
        /// bot is beyond the stance's leash range from the player or a recall's no-flank grace is running.
        /// Logs a leg that ends here.
        /// </summary>
        /// <param name="botOwner">The carry bot.</param>
        /// <param name="fork">Its fork state.</param>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="distanceToPlayer">Metres from the bot to the player.</param>
        public static bool OnFlankLeg(BotOwner botOwner, BotForkState fork, float now, float distanceToPlayer)
        {
            var order = fork.Engage;
            if (!order.FlankActive)
            {
                return false;
            }
            var stance = CurrentStance(fork);
            if (StanceRules.Tactics(stance) == EngageKind.None)
            {
                order.Cancel();
                fork.EngageRole = "";
                LogDebug(botOwner, "flank leg dropped: stance changed");
                return false;
            }
            if (fork.Leash.GraceLeft(now) > 0f)
            {
                order.EndFlank(FlankEnd.Recall);
                LogDebug(botOwner, $"flank leg ended: {FlankEnd.Recall}");
                return false;
            }
            var end = order.UpdateFlank(now, Vector3.Distance(botOwner.Position, ToVector3(order.FlankPoint)), false, false, distanceToPlayer, RangeLimit(stance), _settings);
            if (end != FlankEnd.None)
            {
                LogDebug(botOwner, $"flank leg ended: {end}");
            }
            return order.FlankActive;
        }

        /// <summary>GigaChad or Wreckless, per the reckless-personality setting (Random flips a coin).</summary>
        private static EPersonality PickRecklessPersonality()
        {
            return (ForkConfig.RecklessStyle?.Value ?? ERecklessStyle.Random) switch
            {
                ERecklessStyle.GigaChad => EPersonality.GigaChad,
                ERecklessStyle.Wreckless => EPersonality.Wreckless,
                _ => Random.value < 0.5f ? EPersonality.GigaChad : EPersonality.Wreckless,
            };
        }

        /// <summary>
        /// This bot's place among the squad's bots running the tactical kind (Aggressive stance), ordered
        /// by profile id, how many there are, and whether another of them is walking a flank leg.
        /// </summary>
        private static void GetRole(BotOwner self, McsBotPlayerData data, out int index, out int count, out bool anyOtherFlanking)
        {
            index = 0;
            anyOtherFlanking = false;
            _orderedBots.Clear();
            foreach (var member in McsMgr.GetAllAliveMcsSquadMembersByMcsLeadId(data.LeadPlayer.ProfileId))
            {
                var owner = member?.AIData?.BotOwner;
                var engage = owner?.GetMcsBotPlayerData()?.Fork.Engage;
                if (engage == null || engage.Kind != EngageKind.Tactical)
                {
                    continue;
                }
                _orderedBots.Add(owner);
                if (owner != self && engage.FlankActive)
                {
                    anyOtherFlanking = true;
                }
            }
            _orderedBots.Sort((a, b) => string.CompareOrdinal(a.ProfileId, b.ProfileId));
            count = _orderedBots.Count;
            for (var i = 0; i < count; i++)
            {
                if (_orderedBots[i] == self)
                {
                    index = i;
                    break;
                }
            }
            _orderedBots.Clear();
        }

        /// <summary>Has the bot say a voice line with a fork subtitle.</summary>
        private static void Say(BotOwner botOwner, EPhraseTrigger trigger, string key)
        {
            botOwner.TalkMsg(new McsMsg { PhraseTrigger = trigger, Keys = [key] });
        }

        /// <summary>Writes a stance or tactics event to the BepInEx log when the fork's debug log is on.</summary>
        private static void LogDebug(BotOwner botOwner, string text)
        {
            if (ForkConfig.DebugLog?.Value == true)
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] {botOwner.Profile?.Nickname}: {text}");
            }
        }

        /// <summary>Unity vector to the core's vector.</summary>
        private static V3 ToV3(Vector3 v) => new(v.x, v.y, v.z);

        /// <summary>The core's vector to a Unity vector.</summary>
        private static Vector3 ToVector3(V3 v) => new(v.X, v.Y, v.Z);
    }
}
