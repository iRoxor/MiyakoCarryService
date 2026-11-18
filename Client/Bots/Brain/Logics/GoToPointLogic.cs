
using System;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using MiyakoCarryService.Client.Extensions;
using UnityEngine;

namespace MiyakoCarryService.Client.Bots.Brain.Logics
{
    public class GoToPointLogic : McsBotBaseLogic
    {
        private GoToSomePoint _baseLogic;

        public GoToPointLogic(BotOwner botOwner) : base(botOwner)
        {
            _baseLogic = new(botOwner);
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
            BotOwner.Steering.LookToMovingDirection();
            var leaderMovementContext = leadPlayer.MovementContext;
            var botWithin50 = botToLeaderSqrDistance < 50f * 50f;
            var leaderRelativeSpeed = leaderMovementContext.MaxSpeed > 0f ? leaderMovementContext.CharacterMovementSpeed / leaderMovementContext.MaxSpeed : 0f;

            // Fork v15: a bot beyond the follow distance sprints whatever the player's pose or speed setting, and keeps
            // doing so until it is close; only then does it copy their pose and speed (Core/FollowTrail.ChoosePace).
            // v14 copied them within 50 m, so a crouching, slow-walking or prone player could not be caught up with.
            if (C2Sain.FollowWatch.TrailOn && C2Sain.MovePolicy.CopiesLeaderPose(BotOwner, mcsBotPlayerData, true))
            {
                try
                {
                    var pace = mcsBotPlayerData.Fork.Follow.ChoosePace(Time.time, Mathf.Sqrt(botToLeaderSqrDistance), C2Sain.FollowWatch.FollowDistance,
                        leaderMovementContext.IsInPronePose, leaderMovementContext.PoseLevel, leaderRelativeSpeed, C2Sain.FollowWatch.Settings);
                    if (pace == C2Sain.Core.FollowPace.CopyProne)
                    {
                        _baseLogic.DoorOpen();
                        BotOwner.BotLight?.TurnOff(false, true);
                        BotOwner.GoToSomePointData.UpdateToGo(false, 0f, leaderMovementContext.PoseLevel);
                    }
                    else if (pace == C2Sain.Core.FollowPace.CopySlow)
                    {
                        _baseLogic.DoorOpen();
                        BotOwner.BotLight?.TurnOff(false, true);
                        BotOwner.GoToSomePointData.UpdateToGo(false, leaderMovementContext.CharacterMovementSpeed, leaderMovementContext.PoseLevel);
                    }
                    else
                    {
                        BotOwner.Sprint(true, false);
                        _baseLogic.UpdateNodeByMain(data);
                    }
                    return;
                }
                catch (Exception e)
                {
                    // A fault in the new speed rule must not stop the bot walking: log it (at most every 30 s) and
                    // let MCS's own rule below move it this frame.
                    C2Sain.FollowWatch.Fault("the follow speed rule (this frame falls back to MCS's own rule)", e);
                }
            }

            // Fork: copy the player's pose only while following or escorting, never on a job of the bot's
            // own or in combat (upstream tested the game's enemy memory instead). C2Sain/MovePolicy.cs.
            var copyPose = C2Sain.MovePolicy.CopiesLeaderPose(BotOwner, mcsBotPlayerData, botWithin50);
            if (copyPose && leaderMovementContext.IsInPronePose)
            {
                _baseLogic.DoorOpen();
                BotOwner.BotLight?.TurnOff(false, true);
                BotOwner.GoToSomePointData.UpdateToGo(false, 0f, leaderMovementContext.PoseLevel);
            }
            else if (copyPose && (leaderMovementContext.PoseLevel < 1f || leaderMovementContext.PoseLevel == 1f && leaderRelativeSpeed < 1f))
            {
                _baseLogic.DoorOpen();
                BotOwner.BotLight?.TurnOff(false, true);
                BotOwner.GoToSomePointData.UpdateToGo(false, leaderMovementContext.CharacterMovementSpeed, leaderMovementContext.PoseLevel);
            }
            else
            {
                BotOwner.Sprint(true, false);
                _baseLogic.UpdateNodeByMain(data);
            }
        }
    }
}