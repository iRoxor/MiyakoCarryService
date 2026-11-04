
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using MiyakoCarryService.Client.Extensions;

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