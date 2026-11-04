// GuardLogics.cs -- the two BigBrain logics McsGuardLayer runs.
//
//   GuardMoveLogic  -- walk to the guard spot the layer set as the move target;
//                      sprint when the layer flags MoveFast (returning from a cut
//                      chase, or far from the player). Uses the game's
//                      GoToSomePoint node, so the game's door handling applies.
//   GuardWatchLogic -- stand still and watch: an enemy SAIN sees first, then the
//                      squad's last known threat (TeamModes), otherwise scan a
//                      +/-70 degree arc around the outward direction, turning
//                      every 2-4 s. This is what replaces upstream MCS's frozen
//                      heading for standing bots.
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
            BotOwner.Steering.LookToMovingDirection();
            _baseLogic.DoorOpen();
            BotOwner.GoToSomePointData.UpdateToGo(false, 1f, 1f);
        }
    }

    /// <summary>Stands still and watches; see the file header for the look priority.</summary>
    public class GuardWatchLogic : McsBotBaseLogic
    {
        private float _nextLookTime;
        private Vector3 _lookDirection;

        /// <summary>Created by BigBrain for one bot.</summary>
        public GuardWatchLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        /// <summary>Stops movement and resets the stuck timer's reference position.</summary>
        public override void Start()
        {
            BotOwner.Mover._lastPos = BotOwner.Position;
            BotOwner.StopMove();
            _nextLookTime = 0f;
            base.Start();
        }

        /// <summary>Holds still and turns the head toward the current point of interest.</summary>
        public override void Update(CustomLayer.ActionData data)
        {
            BotOwner.Sprint(false, false);
            BotOwner.StopMove();
            BotOwner.Mover._lastTimePosChanged = Time.time;

            // SAIN's own vision, not the game's EnemyInfo.IsVisible: MCS forces that flag true when
            // sharing the player's enemies (McsAILeadPlayer.CalcGoalEnemy), even through walls.
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
