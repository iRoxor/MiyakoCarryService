// HeardWatch.cs -- what the squad does when a carry bot hears an enemy it cannot see (fork v8).
//
// Patches/SainHearingPatches.cs (SainHeardPatch) calls OnHeard each time SAIN records that a carry
// bot heard an enemy: AI footsteps from FootstepFeed, the player's own footsteps toward enemies, and
// gunshots within "Hearing: gunshot range". For an enemy the bot does not see:
//   - the place becomes a squad look target for a few seconds (TeamModes.RecordHeard); guards whose
//     sector covers it turn toward it (GuardWatchLogic), and the bot that heard it always does;
//   - while the squad follows the player (C2), a sound within "Hearing: footsteps while following"
//     alerts the squad: Guard at the player with posts in cover facing the sound
//     (TeamModes.AlertHeard), at most once every ALERT_INTERVAL s;
//   - the bot says "I hear something." (NoisePhrase), at most once every VOICE_INTERVAL s per squad,
//     never in Combat, when "Hearing: say I hear something" is on.
// The fork's decision policy already keeps SAIN from chasing an enemy the bot has only heard
// (DecisionPolicy rule 4), so a heard enemy makes a bot look and take cover, not leave.
// With "Debug: diagnostic log" on, each heard enemy gets a "heard:" line (at most one per bot and
// enemy every LOG_INTERVAL s).
//
// Owns the per-squad gates and the per-bot log times for the raid; only OnHeard reads or writes them,
// and BrainMgr.OnRaidEnded calls Clear(). Reads and writes no files.

using System.Collections.Generic;
using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Models;
using MiyakoCarryService.Client.Utils;
using SAIN.Components.BotComponentSpace.Classes.EnemyClasses;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Squad reactions to heard enemies; see the file header. Main thread only.</summary>
    internal static class HeardWatch
    {
        /// <summary>Seconds between two "I hear something." lines from one squad.</summary>
        private const float VOICE_INTERVAL = 20f;

        /// <summary>Seconds between two alerts of one squad from heard sounds while following.</summary>
        private const float ALERT_INTERVAL = 10f;

        /// <summary>Seconds between two "heard:" log lines for the same bot and enemy.</summary>
        private const float LOG_INTERVAL = 5f;

        private static readonly Dictionary<string, RateGate> _voice = new();
        private static readonly Dictionary<string, RateGate> _alert = new();
        private static readonly Dictionary<string, float> _lastLog = new();

        /// <summary>
        /// Reacts to one heard enemy; see the file header. Never throws past SainHeardPatch's catch.
        /// </summary>
        /// <param name="hearing">SAIN's hearing record of the enemy, on the carry bot that heard it.</param>
        /// <param name="position">Where SAIN placed the sound.</param>
        /// <param name="soundType">What SAIN heard (FootStep, Sprint, Shot, ...).</param>
        public static void OnHeard(EnemyHearing hearing, Vector3 position, SAINSoundType soundType)
        {
            var bot = hearing.Bot;
            var botOwner = hearing.BotOwner;
            var data = botOwner.GetMcsBotPlayerData();
            var lead = data?.McsAILeadPlayer;
            var player = data?.LeadPlayer;
            if (lead == null || player == null)
            {
                return;
            }

            Player enemyPlayer = null;
            var visible = false;
            var enemies = bot.EnemyController?.Enemies;
            if (enemies != null)
            {
                foreach (var enemy in enemies.Values)
                {
                    if (enemy != null && enemy.Hearing == hearing)
                    {
                        enemyPlayer = enemy.EnemyPlayer;
                        visible = enemy.IsVisible;
                        break;
                    }
                }
            }
            if (visible)
            {
                return;
            }

            var now = Time.time;
            var squadId = player.ProfileId;
            var mode = TeamModes.Get(lead)?.Mode ?? TeamMode.C2;
            var distance = Vector3.Distance(botOwner.Position, position);
            TeamModes.RecordHeard(lead, position);

            var alerted = false;
            var followRange = ForkConfig.FollowFootstepRange?.Value ?? 30f;
            if (mode == TeamMode.C2 && (followRange <= 0f || distance <= followRange) && Gate(_alert, squadId).Try(now, ALERT_INTERVAL))
            {
                alerted = TeamModes.AlertHeard(lead, position, player.Position);
            }

            var said = false;
            if (ForkConfig.SayIHearSomething?.Value != false && mode != TeamMode.Combat && Gate(_voice, squadId).Try(now, VOICE_INTERVAL))
            {
                botOwner.TalkMsg(new McsMsg { PhraseTrigger = EPhraseTrigger.NoisePhrase, Keys = ["McsFork/IHearSomething"] });
                said = true;
            }

            if (ForkConfig.DebugLog?.Value != true)
            {
                return;
            }
            var key = botOwner.ProfileId + "|" + (enemyPlayer?.ProfileId ?? "?");
            if (_lastLog.TryGetValue(key, out var last) && now - last < LOG_INTERVAL)
            {
                return;
            }
            _lastLog[key] = now;
            var away = position - botOwner.Position;
            McsLogger.LogInfo($"[{ForkLog.Tag}] heard: {botOwner.Profile?.Nickname} heard {enemyPlayer?.Profile?.Nickname ?? "an enemy"} ({soundType}) "
                + $"{distance:0} m away at {GuardFormation.YawOf(away.x, away.z):0} deg, not seen; squad {mode}"
                + (alerted ? "; squad alerted" : "") + (said ? "; said \"I hear something.\"" : ""));
        }

        /// <summary>The squad's gate in <paramref name="gates"/>, created on first use.</summary>
        private static RateGate Gate(Dictionary<string, RateGate> gates, string squadId)
        {
            if (!gates.TryGetValue(squadId, out var gate))
            {
                gate = new RateGate();
                gates[squadId] = gate;
            }
            return gate;
        }

        /// <summary>Forgets every squad and bot. Called at raid end.</summary>
        public static void Clear()
        {
            _voice.Clear();
            _alert.Clear();
            _lastLog.Clear();
        }
    }
}
