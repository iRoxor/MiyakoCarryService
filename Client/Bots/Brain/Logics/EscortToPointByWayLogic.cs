using DrakiaXYZ.BigBrain.Brains;
using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Extensions;
using UnityEngine;

namespace MiyakoCarryService.Client.Bots.Brain.Logics
{
    public class EscortToPointByWayLogic : McsBotBaseLogic
    {
        private GoToSomePoint _baseLogic;

        // Fork: escort pace (C2Sain/Core/EscortPacer.cs) and the look-around while walking or waiting.
        private static readonly EscortPacerSettings _paceSettings = new();
        private float _nextLookTime;
        private Vector3 _lookDirection;

        public EscortToPointByWayLogic(BotOwner botOwner) : base(botOwner)
        {
            _baseLogic = new(botOwner);
        }

        /// <summary>
        /// Fork: a new escort starts with a sprint burst. MCS restarts this logic at every intermediate
        /// point, so the cycle is kept across restarts and only reset after 5 s without escorting.
        /// </summary>
        public override void Start()
        {
            var pacer = BotOwner.GetMcsBotPlayerData()?.Fork.EscortPace;
            if (pacer != null && pacer.IsStale(Time.time, 5f))
            {
                pacer.Reset(Time.time, RefreshPaceSettings());
            }
            _nextLookTime = 0f;
            base.Start();
        }

        /// <summary>Fork: copies the escort pace settings from the fork's BepInEx entries.</summary>
        private static EscortPacerSettings RefreshPaceSettings()
        {
            var config = C2Sain.ForkConfig.EscortSprintSeconds;
            if (config != null)
            {
                _paceSettings.SprintSeconds = config.Value;
                _paceSettings.WalkMinSeconds = C2Sain.ForkConfig.EscortWalkMinSeconds.Value;
                _paceSettings.WalkMaxSeconds = Mathf.Max(C2Sain.ForkConfig.EscortWalkMinSeconds.Value, C2Sain.ForkConfig.EscortWalkMaxSeconds.Value);
                _paceSettings.WaitDistance = C2Sain.ForkConfig.EscortWaitDistance.Value;
            }
            return _paceSettings;
        }

        /// <summary>
        /// Fork: turns the head every 1.5-3 s to a direction within 75 degrees of <paramref name="ahead"/>,
        /// or back toward the player one time in three.
        /// </summary>
        private void LookAround(Vector3 ahead, Vector3 towardPlayer)
        {
            var now = Time.time;
            if (now >= _nextLookTime)
            {
                ahead.y = 0f;
                towardPlayer.y = 0f;
                if (towardPlayer.sqrMagnitude > 1f && Random.value < 0.33f)
                {
                    _lookDirection = towardPlayer.normalized;
                }
                else
                {
                    var forward = ahead.sqrMagnitude > 0.01f ? ahead.normalized : BotOwner.Transform.forward;
                    _lookDirection = Quaternion.Euler(0f, Random.Range(-75f, 75f), 0f) * forward;
                }
                _nextLookTime = now + Random.Range(1.5f, 3f);
            }
            BotOwner.Steering.LookToDirection(_lookDirection, 120f);
        }

        public override void Update(CustomLayer.ActionData data)
        {
            var mcsBotPlayerData = BotOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData == null)
            {
                BotOwner.Sprint(true, false);
                _baseLogic.UpdateNodeByMain(data);
                return;
            }

            var leadPlayer = mcsBotPlayerData.LeadPlayer;
            var botToLeaderSqrDistance = BotOwner.Position.McsSqrDistance(leadPlayer.Position);
            var botToTargetSqrDistance = BotOwner.Position.McsSqrDistance(mcsBotPlayerData.TargetPos.Value);
            var leaderToTargetSqrDistance = leadPlayer.Position.McsSqrDistance(mcsBotPlayerData.TargetPos.Value);
            BotOwner.Steering.LookToMovingDirection();

            var leaderMovementContext = leadPlayer.MovementContext;
            var botWithin50 = botToLeaderSqrDistance < 50f * 50f;
            var leaderRelativeSpeed = leaderMovementContext.MaxSpeed > 0f ? leaderMovementContext.CharacterMovementSpeed / leaderMovementContext.MaxSpeed : 0f;

            if (!BotOwner.Memory.HaveEnemy && botWithin50 && leaderMovementContext.IsInPronePose)
            {
                _baseLogic.DoorOpen();
                BotOwner.BotLight?.TurnOff(false, true);
                BotOwner.GoToSomePointData.UpdateToGo(false, 0f, leaderMovementContext.PoseLevel);
            }
            else if (!BotOwner.Memory.HaveEnemy && botWithin50 && (leaderMovementContext.PoseLevel < 1f || leaderMovementContext.PoseLevel == 1f && leaderRelativeSpeed < 1f))
            {
                _baseLogic.DoorOpen();
                BotOwner.BotLight?.TurnOff(false, true);
                BotOwner.GoToSomePointData.UpdateToGo(false, leaderMovementContext.CharacterMovementSpeed, leaderMovementContext.PoseLevel);
            }
            else
            {
                // Fork: upstream sprinted unless the player was 11-18 m away, so the bot outran the
                // player and only watched the way ahead. Now: 3 s sprint bursts, 3-5 s walks looking
                // around, and a stop to watch while the player is more than 20 m behind (EscortPacer).
                var pace = mcsBotPlayerData.Fork.EscortPace.Tick(Time.time, Mathf.Sqrt(botToLeaderSqrDistance),
                    leaderToTargetSqrDistance > botToTargetSqrDistance, RefreshPaceSettings());
                var ahead = mcsBotPlayerData.TargetPos.Value - BotOwner.Position;
                var towardPlayer = leadPlayer.Position - BotOwner.Position;
                switch (pace)
                {
                    case EscortPace.Sprint:
                        BotOwner.Sprint(true, false);
                        _baseLogic.UpdateNodeByMain(data);
                        break;
                    case EscortPace.Walk:
                        BotOwner.Sprint(false, false);
                        _baseLogic.UpdateNodeByMain(data);
                        LookAround(ahead, towardPlayer);
                        break;
                    default:
                        BotOwner.Sprint(false, false);
                        BotOwner.StopMove();
                        BotOwner.Mover._lastTimePosChanged = Time.time;
                        LookAround(ahead, towardPlayer);
                        break;
                }
            }
        }
    }
}