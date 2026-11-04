// SainBridge.cs -- every direct read or write of SAIN's per-bot state, in one place.
//
// Called by C2Gate, TeamModes, DecisionPolicy and the Guard layer. Requires
// SAIN 4.5.1 (SAIN.dll is a compile-time reference and a hard BepInEx
// dependency of this fork). Reads and writes no files.
//
// Why ReleaseSain exists: SAIN records which of its layers is active only inside
// its layers' IsActive. While the priority-200 C2 layer runs, those IsActive
// calls never happen, so the flag goes stale and SAIN keeps steering, aiming and
// blocking the game's mover for a layer that is not running. Clearing it makes
// SAINActivationClass.ManualUpdate stop SAIN's mover and aiming once, on the
// transition (checked in SAIN 4.5.1 source, SAINActivationClass.cs).

using EFT;
using MiyakoCarryService.Client.Datas;
using SAIN.Components;
using SAIN.Components.BotController;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;

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
    }
}
