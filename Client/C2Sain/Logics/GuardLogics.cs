// GuardLogics.cs -- the two BigBrain logics McsGuardLayer runs.
//
//   GuardMoveLogic  -- go to the move target the layer set (guard spot, the way
//                      back after a chase, or a flank leg's point): sprint when
//                      the layer flags MoveFast, otherwise walk upright, watching
//                      the enemy's last known position on a flank leg and the
//                      way ahead otherwise. Uses the game's GoToSomePoint node,
//                      so the game's door handling applies.
//   GuardWatchLogic -- stand still and watch, crouched when the spot has cover or
//                      a bush toward the watch direction: an enemy SAIN sees
//                      first, then the squad's threat or contact report
//                      (TeamModes), otherwise scan a +/-70 degree arc around the
//                      watch direction, turning every 2-4 s. This is what
//                      replaces upstream MCS's frozen heading for standing bots.
// Created by BigBrain when McsGuardLayer returns them. Read the bot's fork state;
// read and write no files.

using DrakiaXYZ.BigBrain.Brains;
using EFT;
using MiyakoCarryService.Client.Bots.Brain.Logics;
using MiyakoCarryService.Client.Extensions;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain.Logics
{
    /// <summary>Walks (or runs) to the Guard layer's move target.</summary>
    public class GuardMoveLogic : McsBotBaseLogic
    {
        private readonly GoToSomePoint _baseLogic;

        /// <summary>Created by BigBrain for one bot.</summary>
        public GuardMoveLogic(BotOwner botOwner) : base(botOwner)
        {
            _baseLogic = new GoToSomePoint(botOwner);
        }

        /// <summary>Stands the bot up; a watch may have left it crouched.</summary>
        public override void Start()
        {
            BotOwner.SetPose(1f);
            base.Start();
        }

        /// <summary>Moves one frame toward the target set with GoToSomePointData.SetPoint.</summary>
        public override void Update(CustomLayer.ActionData data)
        {
            var fork = BotOwner.GetMcsBotPlayerData()?.Fork;
            if (fork != null && fork.MoveFast)
            {
                BotOwner.Sprint(true, false);
                _baseLogic.UpdateNodeByMain(data);
                return;
            }

            BotOwner.Sprint(false, false);
            _baseLogic.DoorOpen();
            BotOwner.GoToSomePointData.UpdateToGo(false, 1f, 1f);
            if (fork != null && fork.Engage.FlankActive)
            {
                var anchor = fork.Engage.FlankAnchor;
                BotOwner.Steering.LookToPoint(new Vector3(anchor.X, anchor.Y + 1.2f, anchor.Z));
            }
            else
            {
                BotOwner.Steering.LookToMovingDirection();
            }
        }
    }

    /// <summary>Stands still and watches; see the file header for the look priority.</summary>
    public class GuardWatchLogic : McsBotBaseLogic
    {
        /// <summary>Pose level used at a covered spot (0 = full crouch, 1 = standing).</summary>
        private const float COVERED_POSE = 0.3f;

        private float _nextLookTime;
        private Vector3 _lookDirection;

        /// <summary>Created by BigBrain for one bot.</summary>
        public GuardWatchLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        /// <summary>Stops movement, resets the stuck timer's reference position, and crouches at a covered spot.</summary>
        public override void Start()
        {
            BotOwner.Mover._lastPos = BotOwner.Position;
            BotOwner.StopMove();
            _nextLookTime = 0f;
            var covered = BotOwner.GetMcsBotPlayerData()?.Fork.GuardSpotCovered == true;
            BotOwner.SetPose(covered ? COVERED_POSE : 1f);
            base.Start();
        }

        /// <summary>Holds still and turns the head toward the current point of interest.</summary>
        public override void Update(CustomLayer.ActionData data)
        {
            BotOwner.Sprint(false, false);
            BotOwner.StopMove();
            BotOwner.Mover._lastTimePosChanged = Time.time;

            // SAIN's own vision, not the game's EnemyInfo.IsVisible, which upstream MCS used to force
            // true when sharing the player's enemies (McsAILeadPlayer.CalcGoalEnemy).
            var sainEnemy = SainBridge.GetSain(BotOwner)?.GoalEnemy;
            if (sainEnemy != null && sainEnemy.IsVisible && sainEnemy.EnemyPlayer != null)
            {
                BotOwner.Steering.LookToPoint(sainEnemy.EnemyPlayer.Position + Vector3.up * 1.2f);
                return;
            }

            var mcsData = BotOwner.GetMcsBotPlayerData();
            if (mcsData?.McsAILeadPlayer != null && TeamModes.TryGetThreat(mcsData.McsAILeadPlayer, out var threat))
            {
                BotOwner.Steering.LookToPoint(threat + Vector3.up * 1.2f);
                return;
            }

            var now = Time.time;
            if (now >= _nextLookTime)
            {
                var outward = mcsData?.Fork.GuardOutward ?? Vector3.zero;
                outward.y = 0f;
                if (outward.sqrMagnitude < 0.01f)
                {
                    outward = BotOwner.Transform.forward;
                    outward.y = 0f;
                }
                _lookDirection = Quaternion.Euler(0f, Random.Range(-70f, 70f), 0f) * outward.normalized;
                _nextLookTime = now + Random.Range(2f, 4f);
            }
            BotOwner.Steering.LookToDirection(_lookDirection, 120f);
        }
    }
}
