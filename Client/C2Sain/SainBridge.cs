// SainBridge.cs -- every direct read or write of SAIN's per-bot state, in one place.
//
// Called by C2Gate, TeamModes, DecisionPolicy, EngageTactics, ContactReports and
// the Guard layer. Requires
// SAIN 4.5.1 (SAIN.dll is a compile-time reference and a hard BepInEx
// dependency of this fork). Reads and writes no files.
//
// Why ReleaseSain exists: SAIN records which of its layers is active only inside
// its layers' IsActive. While the priority-200 C2 layer runs, those IsActive
// calls never happen, so the flag goes stale and SAIN keeps steering, aiming and
// blocking the game's mover for a layer that is not running. Clearing it makes
// SAINActivationClass.ManualUpdate stop SAIN's mover and aiming once, on the
// transition (checked in SAIN 4.5.1 source, SAINActivationClass.cs).

using System;
using EFT;
using HarmonyLib;
using MiyakoCarryService.Client.Datas;
using SAIN.Components;
using SAIN.Components.BotController;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Models.Preset.Personalities;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Info;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Thin, null-safe access to SAIN for carry bots.</summary>
    internal static class SainBridge
    {
        /// <summary>Seconds after being hit or shot at during which a bot counts as engaged.</summary>
        private const float UNDER_FIRE_WINDOW = 5f;

        /// <summary>Seconds after last seeing its goal enemy during which a bot counts as engaged.</summary>
        private const float SEEN_WINDOW = 8f;

        /// <summary>SAIN's component for a bot, or null when SAIN does not manage it (or is not running).</summary>
        public static BotComponent GetSain(BotOwner botOwner)
        {
            if (botOwner == null)
            {
                return null;
            }
            var controller = BotSpawnController.Instance;
            return controller != null ? controller.GetSAIN(botOwner) : null;
        }

        /// <summary>
        /// Tells SAIN none of its layers is active. Side effect: on the first call after a SAIN
        /// layer was active, SAIN stops its mover and aiming for this bot.
        /// </summary>
        public static void ReleaseSain(BotOwner botOwner)
        {
            var bot = GetSain(botOwner);
            if (bot != null && bot.ActiveLayer != ESAINLayer.None)
            {
                bot.ActiveLayer = ESAINLayer.None;
            }
        }

        /// <summary>
        /// True when this bot is in a fight: hit or shot at recently, or its goal enemy is
        /// visible or was seen recently. An enemy that was only heard does not count.
        /// </summary>
        /// <param name="botOwner">The carry bot.</param>
        /// <param name="data">Its MCS data (hit and shot-at times); may be null.</param>
        /// <param name="now">Game time in seconds.</param>
        public static bool IsBotEngaged(BotOwner botOwner, McsBotPlayerData data, float now)
        {
            if (data != null && (now - data.LastHitTime <= UNDER_FIRE_WINDOW || now - data.LastShotAtTime <= UNDER_FIRE_WINDOW))
            {
                return true;
            }

            var enemy = GetSain(botOwner)?.GoalEnemy;
            if (enemy == null)
            {
                return false;
            }
            return enemy.IsVisible || (enemy.Seen && enemy.TimeSinceSeen <= SEEN_WINDOW);
        }

        /// <summary>The position SAIN last knew for the bot's goal enemy, if any.</summary>
        public static bool TryGetGoalEnemyPosition(BotOwner botOwner, out UnityEngine.Vector3 position)
        {
            var known = GetSain(botOwner)?.GoalEnemy?.KnownPlaces?.LastKnownPosition;
            position = known ?? default;
            return known.HasValue;
        }

        /// <summary>
        /// Where this bot itself most recently heard an enemy it cannot see, within the last
        /// <paramref name="maxAgeSeconds"/> (SAIN's EnemyHearing.LastHeardPosition). False when none.
        /// Fork v8: the guard logics turn toward it before anything the squad shares.
        /// </summary>
        public static bool TryGetOwnHeard(BotComponent bot, float maxAgeSeconds, out UnityEngine.Vector3 position)
        {
            position = default;
            var found = false;
            var newest = maxAgeSeconds;
            var enemies = bot?.EnemyController?.Enemies;
            if (enemies == null)
            {
                return false;
            }
            foreach (var enemy in enemies.Values)
            {
                var hearing = enemy?.Hearing;
                if (hearing == null || enemy.IsVisible || !hearing.Heard || !hearing.LastHeardPosition.HasValue || hearing.TimeSinceHeard > newest)
                {
                    continue;
                }
                newest = hearing.TimeSinceHeard;
                position = hearing.LastHeardPosition.Value;
                found = true;
            }
            return found;
        }

        /// <summary>Number of enemies SAIN currently counts as known for this bot.</summary>
        public static int CountKnownEnemies(BotComponent bot)
        {
            var count = 0;
            var enemies = bot?.EnemyController?.Enemies;
            if (enemies == null)
            {
                return 0;
            }
            foreach (var enemy in enemies.Values)
            {
                if (enemy != null && enemy.EnemyKnown)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Stops SAIN's search toward an enemy and marks its last known place as searched, so the
        /// search does not restart until the enemy's position next updates. Used when a chase is cut.
        /// </summary>
        public static void StopSearch(BotComponent bot, Enemy enemy)
        {
            if (bot == null || enemy == null)
            {
                return;
            }
            bot.Search?.ToggleSearch(false, enemy);
            var place = enemy.KnownPlaces?.LastKnownPlace;
            if (place != null)
            {
                enemy.KnownPlaces.SetPlaceAsSearched(place);
            }
        }

        /// <summary>Setter for SAIN's private TimeBeforeSearch, bound on first use.</summary>
        private static Action<SAINBotInfoClass, float> _setTimeBeforeSearch;

        /// <summary>
        /// Gives the bot a SAIN personality from the loaded preset and recalculates the timers SAIN
        /// derives from it (search delay, hold-ground time). Returns false when the bot has no SAIN
        /// component or the preset lacks that personality.
        /// </summary>
        public static bool TryApplyPersonality(BotComponent bot, EPersonality personality)
        {
            var info = bot?.Info;
            if (info == null)
            {
                return false;
            }
            if (info.Personality == personality)
            {
                return true;
            }
            info.SetPersonality(personality);
            if (info.Personality != personality)
            {
                return false;
            }
            info.CalcTimeBeforeSearch();
            info.CalcHoldGroundDelay();
            return true;
        }

        /// <summary>
        /// A fresh personality roll from SAIN's own picker for this bot (forced, nickname and boss rules,
        /// then each personality's random chance), without applying it. Null without a SAIN component.
        /// </summary>
        public static EPersonality? RollPersonality(BotComponent bot)
        {
            return bot?.Info?.GetPersonality(out _);
        }

        /// <summary>The bot's current SAIN personality, or null without a SAIN component.</summary>
        public static EPersonality? GetPersonality(BotComponent bot)
        {
            return bot?.Info?.Personality;
        }

        /// <summary>
        /// Overrides how long SAIN waits after losing sight of an enemy before it searches. SAIN
        /// recalculates the value from the personality every two minutes and on preset changes,
        /// so callers re-apply it while their order lasts and call <see cref="RestoreTimeBeforeSearch"/> after.
        /// </summary>
        public static void SetTimeBeforeSearch(BotComponent bot, float seconds)
        {
            var info = bot?.Info;
            if (info == null || Math.Abs(info.TimeBeforeSearch - seconds) < 0.01f)
            {
                return;
            }
            _setTimeBeforeSearch ??= AccessTools.MethodDelegate<Action<SAINBotInfoClass, float>>(
                AccessTools.PropertySetter(typeof(SAINBotInfoClass), nameof(SAINBotInfoClass.TimeBeforeSearch)));
            _setTimeBeforeSearch(info, seconds);
        }

        /// <summary>Recomputes SAIN's search delay from the bot's personality, undoing <see cref="SetTimeBeforeSearch"/>.</summary>
        public static void RestoreTimeBeforeSearch(BotComponent bot)
        {
            bot?.Info?.CalcTimeBeforeSearch();
        }

        /// <summary>
        /// Tells SAIN that a squadmate saw <paramref name="enemyPlayer"/> at <paramref name="position"/>,
        /// the way SAIN shares sightings inside a squad: the enemy becomes known with that last known
        /// place, but the bot has not seen it itself (Enemy.Seen stays false). Returns SAIN's enemy
        /// record, or null when SAIN does not treat the player as this bot's enemy.
        /// </summary>
        public static Enemy ShareSighting(BotComponent bot, Player enemyPlayer, UnityEngine.Vector3 position)
        {
            if (bot?.EnemyController == null || enemyPlayer == null)
            {
                return null;
            }
            var enemy = bot.EnemyController.CheckAddEnemy(enemyPlayer);
            if (enemy?.KnownPlaces == null || enemy.IsVisible)
            {
                return enemy;
            }
            var placeData = new PlaceData
            {
                OwnerEnemy = enemy,
                Owner = enemy.Bot,
                IsAI = enemy.IsAI,
                OwnerID = enemy.Bot.ProfileId,
            };
            var reported = new EnemyPlace(placeData, position, true, EEnemyPlaceType.Vision, null);
            enemy.KnownPlaces.UpdateSquadSeenPlace(reported, UnityEngine.Time.time);
            return enemy;
        }

        /// <summary>
        /// True when the enemy is worth engaging: visible now, or its position was updated (seen,
        /// heard or reported) within <paramref name="maxAgeSeconds"/>.
        /// </summary>
        public static bool IsFreshContact(Enemy enemy, float maxAgeSeconds)
        {
            return enemy != null && (enemy.IsVisible || enemy.TimeSinceLastKnownUpdated <= maxAgeSeconds);
        }

        /// <summary>True when the bot was hit or shot at within the last <paramref name="windowSeconds"/>.</summary>
        public static bool IsUnderFire(McsBotPlayerData data, float now, float windowSeconds)
        {
            return data != null && (now - data.LastHitTime <= windowSeconds || now - data.LastShotAtTime <= windowSeconds);
        }
    }
}
