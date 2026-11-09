// FootstepFeed.cs -- hands AI enemies' footsteps to carry bots, so they can hear them (fork v8).
//
// PlayerDataMgr.FeedFootsteps calls Tick four times a second during a raid, while "Hearing: AI
// footsteps" is on. SAIN makes footstep sounds only for human players
// (SAIN/Classes/Player/PlayerSoundController.cs returns early for AI), so without this no carry bot
// hears an AI enemy walk. For each living AI player near a carry bot, Core/Hearing.cs
// (FootstepCadence) decides when it steps; each step becomes a SAIN SoundEvent built the way SAIN
// builds a human's (base range from the loaded SAIN preset, volume from the game's own step-volume
// formulas, as SAIN's CalculateStepVolume does), and is handed to each carry bot within its range
// through HearingInput.CheckAddSoundToCache, the call SAIN itself uses for every bot. SAIN then
// decides per bot whether the step is heard (walls, distance, headphones, chance) and ignores
// players that are not enemies. Other bots are not given these sounds, so AI-versus-AI hearing in
// the raid is unchanged. While the squad follows the player (C2), only steps within "Hearing:
// footsteps while following" are handed over (HearingFilter).
//
// Owns one step timer per AI player for the raid (_cadence, keyed by profile id); only Tick reads or
// writes it, and BrainMgr.OnRaidEnded calls Clear(). Reads and writes no files.

using System;
using System.Collections.Generic;
using CommonAssets.Scripts.Audio;
using Comfort.Common;
using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Utils;
using SAIN;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>AI footsteps for carry bots; see the file header. Main thread only.</summary>
    internal static class FootstepFeed
    {
        /// <summary>Seconds between two error lines, so a fault cannot flood the log.</summary>
        private const float ERROR_LOG_INTERVAL = 30f;

        /// <summary>Speed of sound (m/s) SAIN gives footstep events.</summary>
        private const float SOUND_SPEED = 343f;

        private static readonly Dictionary<string, FootstepCadence> _cadence = new();
        private static readonly List<McsBotPlayerData> _bots = new();
        private static readonly FootstepSettings _steps = new();
        private static readonly HearingFilterSettings _filter = new();
        private static float _nextErrorLog;
        private static McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        /// <summary>
        /// One pass: every living AI player near a carry bot is sampled, and each step it makes is handed
        /// to the carry bots in range. Never throws.
        /// </summary>
        /// <param name="carryBots">The raid's carry bots (MCS data), dead ones included; they are skipped.</param>
        public static void Tick(IEnumerable<McsBotPlayerData> carryBots)
        {
            try
            {
                var world = Singleton<GameWorld>.Instance;
                var hearing = SAINPlugin.LoadedPreset?.GlobalSettings?.Hearing;
                if (world == null || hearing == null)
                {
                    return;
                }
                _bots.Clear();
                foreach (var data in carryBots)
                {
                    if (data?.BotOwner != null && data.Player?.HealthController?.IsAlive == true && SainBridge.GetSain(data.BotOwner) != null)
                    {
                        _bots.Add(data);
                    }
                }
                if (_bots.Count == 0)
                {
                    return;
                }
                _filter.FollowFootstepRange = ForkConfig.FollowFootstepRange?.Value ?? 30f;
                var maxRange = Mathf.Max(hearing.BaseSoundRange_Footstep, hearing.BaseSoundRange_Sprint);
                var now = Time.time;

                foreach (var enemy in world.AllAlivePlayersList)
                {
                    if (enemy == null || !enemy.IsAI || enemy.HealthController?.IsAlive != true || McsMgr.IsMcsBotPlayer(enemy.ProfileId)
                        || !NearAnyBot(enemy.Position, maxRange))
                    {
                        continue;
                    }
                    if (!_cadence.TryGetValue(enemy.ProfileId, out var cadence))
                    {
                        cadence = new FootstepCadence();
                        _cadence[enemy.ProfileId] = cadence;
                    }
                    var p = enemy.Position;
                    var sprinting = enemy.IsSprintEnabled;
                    var step = cadence.Sample(now, new V3(p.x, p.y, p.z), sprinting, _steps);
                    if (step == StepKind.None || !GameWorldComponent.TryGetPlayerComponent(enemy, out var source))
                    {
                        continue;
                    }
                    Deliver(enemy, source, step, hearing.BaseSoundRange_Footstep, hearing.BaseSoundRange_Sprint, hearing.FootstepAudioMultiplier);
                }
            }
            catch (Exception e)
            {
                if (Time.time >= _nextErrorLog)
                {
                    _nextErrorLog = Time.time + ERROR_LOG_INTERVAL;
                    McsLogger.LogError($"[{ForkLog.Tag}] footstep feed failed: {e}");
                }
            }
        }

        /// <summary>True when some carry bot in _bots is within <paramref name="range"/> m of <paramref name="position"/>.</summary>
        private static bool NearAnyBot(Vector3 position, float range)
        {
            var rangeSqr = range * range;
            foreach (var data in _bots)
            {
                if ((data.BotOwner.Position - position).sqrMagnitude <= rangeSqr)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Builds one step's SAIN sound and hands it to every carry bot whose distance passes
        /// HearingFilter.HandsOverFootstep.
        /// </summary>
        private static void Deliver(Player enemy, PlayerComponent source, StepKind step, float walkRange, float sprintRange, float audioMultiplier)
        {
            var sprint = step == StepKind.Sprint;
            var range = sprint ? sprintRange : walkRange;
            var volume = StepVolume(enemy, sprint) * audioMultiplier;
            var sound = new SoundEvent(sprint ? SAINSoundType.Sprint : SAINSoundType.FootStep, enemy.Position, source, range, volume, SOUND_SPEED);
            foreach (var data in _bots)
            {
                var distance = Vector3.Distance(data.BotOwner.Position, enemy.Position);
                var following = data.McsAILeadPlayer != null && TeamModes.Get(data.McsAILeadPlayer)?.Mode == TeamMode.C2;
                if (!HearingFilter.HandsOverFootstep(distance, range * volume, following, _filter))
                {
                    continue;
                }
                SainBridge.GetSain(data.BotOwner)?.Hearing?.HearingInput?.CheckAddSoundToCache(sound, distance);
            }
        }

        /// <summary>
        /// A step's volume from the game's own formulas, as SAIN's PlayerSoundController.CalculateStepVolume
        /// works it out for human players (walking or crouch-walking), or its sprint multiplier.
        /// </summary>
        private static float StepVolume(Player player, bool sprint)
        {
            if (sprint)
            {
                return player.CalculateMovementVolumeDefaultMultiplier(EAudioMovementState.Sprint);
            }
            var state = player.Pose != EPlayerPose.Duck ? EAudioMovementState.Run : EAudioMovementState.Duck;
            return player.MovementContext.CovertMovementVolumeBySpeed * player.CalculateStepSoundVolumeSpeedFactor() * player.CalculateMovementVolumeDefaultMultiplier(state);
        }

        /// <summary>Forgets every player's step timer. Called at raid end.</summary>
        public static void Clear()
        {
            _cadence.Clear();
            _bots.Clear();
        }
    }
}
