// TeamModes.cs -- one TeamModeMachine per player squad, ticked from the bots' layers.
//
// Owner of the per-squad mode state for the whole raid. C2Gate (C2 layer gate),
// McsGuardLayer and ForkDebug call Get(); the first caller after 0.2 s ticks the
// machine with the player's position and whether anyone in the squad is
// engaged. BrainMgr.OnRaidEnded calls Clear(). No MonoBehaviour is involved.
// Reads and writes no files; with "Debug: log mode changes" on it writes mode
// changes to the BepInEx log.

using System.Collections.Generic;
using Comfort.Common;
using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Misc;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Registry and ticker of per-squad modes. Main-thread only.</summary>
    internal static class TeamModes
    {
        /// <summary>Seconds between ticks of one squad's machine.</summary>
        private const float TICK_INTERVAL = 0.2f;

        /// <summary>Seconds a recorded threat position stays useful for guards to look at.</summary>
        private const float THREAT_MEMORY = 10f;

        /// <summary>Seconds after the player hits an identified enemy during which the player counts as engaged.</summary>
        private const float PLAYER_SHOT_WINDOW = 3f;

        private sealed class Entry
        {
            public readonly TeamModeMachine Machine = new();
            public float NextTick;
            public bool HasThreat;
            public Vector3 ThreatPosition;
            public float ThreatTime = -999f;
        }

        private static readonly Dictionary<string, Entry> _entries = new();
        private static readonly TeamModeSettings _settings = new();
        private static McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        /// <summary>
        /// Returns the squad's machine, ticking it first when a tick is due.
        /// </summary>
        /// <param name="lead">The player's MCS lead object; null returns null.</param>
        public static TeamModeMachine Get(McsAILeadPlayer lead)
        {
            var entry = GetEntry(lead);
            return entry?.Machine;
        }

        /// <summary>
        /// Where the squad's current threat was last known (the engaged bot's goal enemy, or the
        /// player's attacker), for guards to look toward. False when nothing recent is known.
        /// </summary>
        public static bool TryGetThreat(McsAILeadPlayer lead, out Vector3 position)
        {
            var entry = GetEntry(lead);
            if (entry != null && entry.HasThreat && Time.time - entry.ThreatTime <= THREAT_MEMORY)
            {
                position = entry.ThreatPosition;
                return true;
            }
            position = default;
            return false;
        }

        /// <summary>Forgets every squad. Called at raid end.</summary>
        public static void Clear()
        {
            _entries.Clear();
        }

        /// <summary>Finds or creates the squad entry and ticks it when due.</summary>
        private static Entry GetEntry(McsAILeadPlayer lead)
        {
            var player = lead?.McsLeadPlayer;
            if (player == null)
            {
                return null;
            }

            var id = player.ProfileId;
            if (!_entries.TryGetValue(id, out var entry))
            {
                entry = new Entry();
                _entries[id] = entry;
            }

            var now = Time.time;
            if (now >= entry.NextTick)
            {
                entry.NextTick = now + TICK_INTERVAL;
                Tick(entry, lead, player, now);
            }
            return entry;
        }

        /// <summary>
        /// One tick: refresh settings, work out whether the squad is engaged (and where the threat is),
        /// then advance the machine. Logs a mode change when debug logging is on.
        /// </summary>
        private static void Tick(Entry entry, McsAILeadPlayer lead, Player player, float now)
        {
            RefreshSettings();

            var engaged = false;
            var attacker = lead.GetLeadThreatEnemy();
            if (attacker != null)
            {
                engaged = true;
                RecordThreat(entry, attacker.Position, now);
            }
            else if (now - lead.LastLeadEngageShotTime <= PLAYER_SHOT_WINDOW)
            {
                engaged = true;
            }

            foreach (var member in McsMgr.GetAllAliveMcsSquadMembersByMcsLeadId(player.ProfileId))
            {
                var botOwner = member?.AIData?.BotOwner;
                if (botOwner == null)
                {
                    continue;
                }
                if (SainBridge.IsBotEngaged(botOwner, botOwner.GetMcsBotPlayerData(), now))
                {
                    engaged = true;
                    if (SainBridge.TryGetGoalEnemyPosition(botOwner, out var enemyPos))
                    {
                        RecordThreat(entry, enemyPos, now);
                    }
                    break;
                }
            }

            var before = entry.Machine.Mode;
            var pos = player.Position;
            var after = entry.Machine.Tick(now, new V3(pos.x, pos.y, pos.z), engaged, _settings);
            if (before != after && ForkConfig.DebugLog != null && ForkConfig.DebugLog.Value)
            {
                McsLogger.LogInfo($"[{ForkInfo.Label}] squad of {player.Profile?.Nickname}: {before} -> {after}");
            }
        }

        /// <summary>Remembers where the squad's threat is.</summary>
        private static void RecordThreat(Entry entry, Vector3 position, float now)
        {
            entry.HasThreat = true;
            entry.ThreatPosition = position;
            entry.ThreatTime = now;
        }

        /// <summary>Copies the BepInEx values into the machine's settings object.</summary>
        private static void RefreshSettings()
        {
            if (ForkConfig.IdleRadius == null)
            {
                return;
            }
            _settings.IdleRadius = ForkConfig.IdleRadius.Value;
            _settings.IdleSeconds = ForkConfig.IdleSeconds.Value;
            _settings.GuardRadius = ForkConfig.GuardRadius.Value;
            _settings.CombatEndDelay = ForkConfig.CombatEndDelay.Value;
        }
    }
}
