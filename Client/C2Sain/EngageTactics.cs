// EngageTactics.cs -- carries out the Engage Enemy orders and keeps carry bots' SAIN personality.
//
// Begin/Cancel are called by ForkCommands (the Engage commands, and every other
// order through CommandUtils.Execute). Apply and EnsurePersonality are called by
// DecisionPolicy from SAIN's decision prefix, about ten times a second per bot.
// Writes the bot's fork state (order, flank leg, role, lent personality) and,
// through SainBridge, SAIN's personality and search delay for the bot. Reads and
// writes no files.
//
// Engage Enemy (tactical):
//   - SAIN's Chad personality is lent (in the user's preset: searches after ~16 s,
//     pushes enemies that are reloading or hurt) and the search delay is cut to
//     TACTICAL_SEARCH_DELAY; the chase leash does not apply.
//   - Roles among the squad's bots with a tactical order, ordered by profile id:
//     a lone bot flanks (the player is the base of fire); with two or more, the
//     first is the base of fire and the rest flank, alternating sides.
//   - Base of fire: while any flanker is moving it does not advance; it lays
//     SAIN's suppressing fire when SAIN has a suppression point, otherwise holds
//     in cover.
//   - Flanker: walks a flank leg (run by McsGuardLayer) to a point picked by
//     C2Nav.TryPickFlankSpot, then SAIN searches the last known position from
//     there. SAIN has no flanking of its own (checked in SAIN 4.5.1).
// Engage Enemy Wrecklessly: SAIN's GigaChad or Wreckless personality is lent and
// the search delay cut to RECKLESS_SEARCH_DELAY; SAIN does the rest.

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
    /// <summary>Engage Enemy orders and carry-bot personalities. Main thread only.</summary>
    internal static class EngageTactics
    {
        /// <summary>
        /// Seconds since the enemy's position was last updated (seen, heard or reported) for an Engage
        /// command to be accepted. Generous on purpose: the player may give the order well after the
        /// first shots.
        /// </summary>
        private const float ACCEPT_MAX_AGE = 60f;

        /// <summary>Seconds a known position keeps an order alive; past it the order's lost-contact timer runs.</summary>
        private const float CONTACT_MAX_AGE = 30f;

        /// <summary>Search delay (s) during a tactical order: a short pause, then the approach.</summary>
        private const float TACTICAL_SEARCH_DELAY = 3f;

        /// <summary>Search delay (s) during a reckless order: straight in, as Wreckless's preset value 0.1 s.</summary>
        private const float RECKLESS_SEARCH_DELAY = 0.1f;

        /// <summary>Seconds after a hit or a near miss during which a flank leg counts as under fire.</summary>
        private const float UNDER_FIRE_WINDOW = 1.5f;

        /// <summary>Metres to the flank point beyond which the bot sprints the leg.</summary>
        public const float FLANK_SPRINT_DISTANCE = 15f;

        /// <summary>Lowest ammo ratio at which the base of fire lays suppressing fire.</summary>
        private const float SUPPRESS_MIN_AMMO = 0.3f;

        private static readonly EngageSettings _settings = new();
        private static readonly List<BotOwner> _orderedBots = new();
        private static McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        /// <summary>
        /// True when the bot has an enemy it can be ordered to engage: SAIN's goal enemy visible, or
        /// updated within ACCEPT_MAX_AGE seconds. Used by the Engage commands to accept or refuse.
        /// </summary>
        public static bool HasEngageableContact(BotOwner botOwner)
        {
            var bot = SainBridge.GetSain(botOwner);
            return bot != null && SainBridge.IsFreshContact(bot.GoalEnemy, ACCEPT_MAX_AGE);
        }

        /// <summary>
        /// Starts an Engage order for one bot. Side effects: clears the bot's other orders (as Break
        /// off does), cancels Break off, lends the order's SAIN personality, shortens SAIN's search
        /// delay, and says the acknowledgement.
        /// </summary>
        /// <param name="botOwner">The carry bot.</param>
        /// <param name="data">Its MCS data.</param>
        /// <param name="kind">Tactical or Reckless.</param>
        public static void Begin(BotOwner botOwner, McsBotPlayerData data, EngageKind kind)
        {
            var bot = SainBridge.GetSain(botOwner);
            var fork = data.Fork;
            RecordBasePersonality(bot, fork);

            data.SetIntent([Intents.ShouldKeepFormation]);
            data.MoveResetRequested = true;
            data.IsLooting = false;
            data.TargetPos = null;
            data.ProxyTargetId = null;
            fork.BreakOff.Cancel();

            fork.Engage.Begin(kind, Time.time);
            fork.EngageRole = "";
            var lend = kind == EngageKind.Tactical ? EPersonality.Chad : PickRecklessPersonality();
            if (SainBridge.TryApplyPersonality(bot, lend))
            {
                fork.PersonalityLent = true;
                fork.LentPersonality = lend;
            }
            SainBridge.SetTimeBeforeSearch(bot, kind == EngageKind.Tactical ? TACTICAL_SEARCH_DELAY : RECKLESS_SEARCH_DELAY);
            Say(botOwner, EPhraseTrigger.Gogogo, kind == EngageKind.Tactical ? "McsFork/Engaging" : "McsFork/EngagingReckless");
        }

        /// <summary>
        /// Ends the bot's Engage order because another order arrived, restoring its personality and
        /// search delay. Does nothing when no order is running.
        /// </summary>
        public static void Cancel(BotOwner botOwner, McsBotPlayerData data)
        {
            if (data?.Fork.Engage.Cancel() == true)
            {
                Restore(SainBridge.GetSain(botOwner), data.Fork);
                data.Fork.EngageRole = "";
                data.Fork.EngageTarget = null;
            }
        }

        /// <summary>
        /// Applies a running Engage order to one set of SAIN decisions, editing them in place.
        /// Returns true while an order is in force, so the caller skips the chase leash. Side effects:
        /// ticks the order (ending it restores the personality and says why), plans and advances flank
        /// legs, records the bot's role, and re-applies the shortened search delay.
        /// </summary>
        /// <param name="bot">SAIN's component for the bot.</param>
        /// <param name="botOwner">The carry bot.</param>
        /// <param name="data">Its MCS data.</param>
        /// <param name="solo">SAIN's combat decision.</param>
        /// <param name="squad">SAIN's squad decision.</param>
        /// <param name="enemy">The enemy SAIN is deciding about; may be null.</param>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="reason">Overlay text describing what the order did, or null.</param>
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

            RefreshSettings();
            if (enemy?.EnemyPlayer != null)
            {
                fork.EngageTarget = enemy.EnemyPlayer;
            }
            var end = order.Tick(now, SainBridge.IsFreshContact(enemy, CONTACT_MAX_AGE), _settings);
            if (end != EngageEnd.None)
            {
                Finish(botOwner, data, bot, end, now);
                return false;
            }

            SainBridge.SetTimeBeforeSearch(bot, order.Kind == EngageKind.Tactical ? TACTICAL_SEARCH_DELAY : RECKLESS_SEARCH_DELAY);
            if (order.Kind == EngageKind.Reckless)
            {
                fork.EngageRole = "rush";
                reason = "engage: rush";
                return true;
            }

            var visible = enemy != null && enemy.IsVisible;
            var underFire = SainBridge.IsUnderFire(data, now, UNDER_FIRE_WINDOW);

            if (order.FlankActive)
            {
                var flankPoint = ToVector3(order.FlankPoint);
                var flankEnd = order.UpdateFlank(now, Vector3.Distance(botOwner.Position, flankPoint), visible, underFire, _settings);
                if (flankEnd == FlankEnd.None)
                {
                    solo = ECombatDecision.None;
                    squad = ESquadDecision.None;
                    fork.EngageRole = order.FlankSide < 0 ? "flank L" : "flank R";
                    reason = "engage: flanking";
                    return true;
                }
                LogDebug(botOwner, $"flank leg ended: {flankEnd}");
            }

            GetRole(botOwner, data, out var index, out var count, out var anyFlanking);
            if (count >= 2 && index == 0)
            {
                var wasCovering = fork.EngageRole == "cover";
                fork.EngageRole = "cover";
                reason = "engage: base of fire";
                if (!visible && anyFlanking)
                {
                    if (enemy?.SuppressionTarget != null && bot.Decision.SelfActionDecisions.AmmoRatio >= SUPPRESS_MIN_AMMO)
                    {
                        squad = ESquadDecision.Suppress;
                        reason = "engage: suppressing";
                    }
                    else if (solo is ECombatDecision.Search or ECombatDecision.RushEnemy or ECombatDecision.MoveToEngage or ECombatDecision.None)
                    {
                        solo = ECombatDecision.SeekCover;
                        reason = "engage: holding for the flank";
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
            reason = "engage: push";
            var lastKnown = enemy?.KnownPlaces?.LastKnownPosition;
            if (ForkConfig.EngageFlanking.Value && lastKnown.HasValue && !visible && !underFire)
            {
                var anchor = ToV3(lastKnown.Value);
                if (order.ShouldPlanFlank(anchor, _settings))
                {
                    var preferredSide = count >= 2 ? (index % 2 == 1 ? -1 : 1) : 0;
                    if (C2Nav.TryPickFlankSpot(botOwner.Position, lastKnown.Value, preferredSide, out var spot, out var side))
                    {
                        order.BeginFlank(ToV3(spot), anchor, side, now);
                        solo = ECombatDecision.None;
                        squad = ESquadDecision.None;
                        fork.EngageRole = side < 0 ? "flank L" : "flank R";
                        reason = "engage: flank start";
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

        /// <summary>Re-rolls tried before falling back to the first allowed personality.</summary>
        private const int MAX_REROLLS = 20;

        /// <summary>Allowed set used before the setting is bound: everything but the four cautious ones.</summary>
        private const ECarryPersonalities DEFAULT_ALLOWED =
            ECarryPersonalities.Normal | ECarryPersonalities.Chad | ECarryPersonalities.GigaChad | ECarryPersonalities.Wreckless;

        /// <summary>
        /// Keeps the bot's SAIN personality at what it should be: the one an Engage order lent, else its
        /// base personality (SAIN's roll when the allowed-personalities setting permits it, otherwise a
        /// re-roll). SAIN re-rolls personalities when its preset is reloaded, so this runs on every
        /// decision. Side effects: records SAIN's roll the first time it sees the bot, and re-chooses the
        /// base when the setting changes.
        /// </summary>
        public static void EnsurePersonality(BotComponent bot, BotForkState fork)
        {
            var current = SainBridge.GetPersonality(bot);
            if (current == null)
            {
                return;
            }
            RecordBasePersonality(bot, fork);
            var desired = fork.PersonalityLent ? fork.LentPersonality : fork.BasePersonality;
            if (current.Value != desired)
            {
                SainBridge.TryApplyPersonality(bot, desired);
            }
        }

        /// <summary>
        /// Records SAIN's own roll for the bot (once) and chooses its base personality under the current
        /// allowed-personalities setting (again whenever the setting changes). A banned roll is replaced
        /// by re-rolling SAIN's own picker, so the allowed personalities keep SAIN's relative odds; if no
        /// re-roll is allowed (a nickname or boss rule forces one), the first allowed personality in the
        /// setting's order is used. An empty setting keeps SAIN's roll.
        /// </summary>
        private static void RecordBasePersonality(BotComponent bot, BotForkState fork)
        {
            var current = SainBridge.GetPersonality(bot);
            if (current == null)
            {
                return;
            }
            if (!fork.HasBasePersonality)
            {
                fork.HasBasePersonality = true;
                fork.SainRolledPersonality = current.Value;
                fork.BaseForMask = -1;
            }

            var allowed = ForkConfig.AllowedPersonalities?.Value ?? DEFAULT_ALLOWED;
            if (fork.BaseForMask == (int)allowed)
            {
                return;
            }
            fork.BaseForMask = (int)allowed;
            fork.BasePersonality = ChooseBase(bot, fork.SainRolledPersonality, allowed);
            if (fork.BasePersonality != fork.SainRolledPersonality && ForkConfig.DebugLog?.Value == true)
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] {bot.BotOwner?.Profile?.Nickname}: SAIN rolled {fork.SainRolledPersonality}, not allowed; using {fork.BasePersonality}");
            }
        }

        /// <summary>See <see cref="RecordBasePersonality"/> for the rule.</summary>
        private static EPersonality ChooseBase(BotComponent bot, EPersonality rolled, ECarryPersonalities allowed)
        {
            if (allowed == ECarryPersonalities.None || IsAllowed(rolled, allowed))
            {
                return rolled;
            }
            for (var i = 0; i < MAX_REROLLS; i++)
            {
                var roll = SainBridge.RollPersonality(bot);
                if (roll.HasValue && IsAllowed(roll.Value, allowed))
                {
                    return roll.Value;
                }
            }
            foreach (ECarryPersonalities flag in System.Enum.GetValues(typeof(ECarryPersonalities)))
            {
                if (flag != ECarryPersonalities.None && (allowed & flag) != 0
                    && System.Enum.TryParse(flag.ToString(), out EPersonality first))
                {
                    return first;
                }
            }
            return rolled;
        }

        /// <summary>True when the setting ticks this personality. Ones it does not list (SAIN's Custom personalities) are not allowed.</summary>
        private static bool IsAllowed(EPersonality personality, ECarryPersonalities allowed)
        {
            return System.Enum.TryParse(personality.ToString(), out ECarryPersonalities flag)
                && flag != ECarryPersonalities.None
                && (allowed & flag) != 0;
        }

        /// <summary>
        /// Wraps up an order that ended on its own: restores the personality, says how it ended ("Enemy
        /// down" when the last target is dead), and when contact was lost more than FALL_BACK_DISTANCE m
        /// from the player sends the bot back the way Break off from combat does. A timed-out order
        /// leaves the bot to the chase leash, which brings it back after its next chase.
        /// </summary>
        private static void Finish(BotOwner botOwner, McsBotPlayerData data, BotComponent bot, EngageEnd end, float now)
        {
            var fork = data.Fork;
            Restore(bot, fork);
            fork.EngageRole = "";
            var targetDead = fork.EngageTarget?.HealthController != null && !fork.EngageTarget.HealthController.IsAlive;
            fork.EngageTarget = null;

            if (targetDead)
            {
                botOwner.TalkMsg(new McsMsg { PhraseTrigger = EPhraseTrigger.EnemyDown });
            }
            else
            {
                Say(botOwner, EPhraseTrigger.LostVisual, end == EngageEnd.TimedOut ? "McsFork/EngageTimeout" : "McsFork/LostContact");
            }

            var lead = data.LeadPlayer;
            if (end != EngageEnd.TimedOut && lead != null && Vector3.Distance(botOwner.Position, lead.Position) > FALL_BACK_DISTANCE)
            {
                fork.BreakOff.Begin(now);
            }
            LogDebug(botOwner, $"engage order ended: {end}{(targetDead ? ", target dead" : "")}");
        }

        /// <summary>Metres from the player beyond which a bot runs back after an order ends without contact.</summary>
        private const float FALL_BACK_DISTANCE = 15f;

        /// <summary>Undoes what an order lent: the personality and the shortened search delay.</summary>
        private static void Restore(BotComponent bot, BotForkState fork)
        {
            fork.PersonalityLent = false;
            if (fork.HasBasePersonality)
            {
                SainBridge.TryApplyPersonality(bot, fork.BasePersonality);
            }
            SainBridge.RestoreTimeBeforeSearch(bot);
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
        /// This bot's place among the squad's bots with a tactical order (ordered by profile id), how
        /// many there are, and whether another of them is walking a flank leg.
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

        /// <summary>Copies the order length from the BepInEx settings.</summary>
        private static void RefreshSettings()
        {
            if (ForkConfig.EngageMaxSeconds != null)
            {
                _settings.MaxSeconds = ForkConfig.EngageMaxSeconds.Value;
            }
        }

        /// <summary>Has the bot say a voice line with a fork subtitle.</summary>
        private static void Say(BotOwner botOwner, EPhraseTrigger trigger, string key)
        {
            botOwner.TalkMsg(new McsMsg { PhraseTrigger = trigger, Keys = [key] });
        }

        /// <summary>Writes an engage event to the BepInEx log when the fork's debug log is on.</summary>
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
