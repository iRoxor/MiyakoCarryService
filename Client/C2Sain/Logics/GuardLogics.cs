// GuardLogics.cs -- the two BigBrain logics McsGuardLayer runs, and the look scan they share.
//
//   GuardMoveLogic  -- go to the move target the layer set (guard or Defend spot,
//                      the way back after a chase, or a flank leg's point): sprint
//                      when the layer flags MoveFast, otherwise walk upright,
//                      watching the enemy's last known position on a flank leg,
//                      the bot's outward direction on a short Defend Me shift, and
//                      the way ahead otherwise. When the walk stalls (no progress
//                      for 2 s, Core/ArrivalWatch.cs) the bot keeps trying but looks
//                      around with the look scan instead. Uses the game's
//                      GoToSomePoint node, so the game's door handling applies.
//   GuardWatchLogic -- hold still, crouched, and watch: an enemy SAIN sees
//                      first, then the squad's threat or contact report
//                      (TeamModes), otherwise the look scan (GuardLook,
//                      Core/LookScan.cs): 2-4 s looks within 70 degrees of the watch
//                      direction, each toward the most open of five tries, and
//                      every fourth look a check behind. This is what replaces
//                      upstream MCS's frozen heading for standing bots.
// Fork v7: before it, a stalled walk looked where the bot last walked, and the scan was one random
// offset within 70 degrees that never checked behind. In the user's build-6 raid (2026-11-05) a
// carry bot stood stalled 1.3 m short of its spot, staring at 198 deg for 12 s while a Reshala guard
// walked behind it.
// Created by BigBrain when McsGuardLayer returns them. Read and write the bot's fork state (walk
// progress, look scan, and what turned the head this frame, which FrozenViewWatch logs); read and
// write no files.

using System;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using MiyakoCarryService.Client.Bots.Brain.Logics;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Extensions;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain.Logics
{
    /// <summary>Walks (or runs) to the Guard layer's move target.</summary>
    public class GuardMoveLogic : McsBotBaseLogic
    {
        /// <summary>Stall rule for a walk: no progress (0.3 m) for 2 s (Core/ArrivalWatch.cs).</summary>
        private static readonly ArrivalSettings _progress = new();

        private readonly GoToSomePoint _baseLogic;
        private bool _wasStalled;

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

        /// <summary>
        /// Moves one frame toward the target set with GoToSomePointData.SetPoint. Records the walk's
        /// progress; while it has stalled, walks without sprinting and looks around (GuardLook).
        /// </summary>
        public override void Update(CustomLayer.ActionData data)
        {
            var fork = BotOwner.GetMcsBotPlayerData()?.Fork;
            var stalled = false;
            if (fork != null)
            {
                var p = BotOwner.Position;
                var target = BotOwner.GoToSomePointData.Point;
                fork.MoveProgress.Update(Time.time, new V3(p.x, p.y, p.z), new V3(target.x, target.y, target.z), _progress);
                stalled = fork.MoveProgress.Stalled && !fork.Engage.FlankActive;
            }

            if (stalled)
            {
                if (!_wasStalled)
                {
                    fork.Look.Restart();
                }
                _wasStalled = true;
                BotOwner.Sprint(false, false);
                _baseLogic.DoorOpen();
                BotOwner.GoToSomePointData.UpdateToGo(false, 1f, 1f);
                GuardLook.Scan(BotOwner, fork);
                fork.SetLook("scanning on a stalled walk", Time.time);
                return;
            }
            _wasStalled = false;

            if (fork != null && fork.MoveFast)
            {
                BotOwner.Sprint(true, false);
                _baseLogic.UpdateNodeByMain(data);
                fork.SetLook("where it runs", Time.time);
                return;
            }

            BotOwner.Sprint(false, false);
            _baseLogic.DoorOpen();
            BotOwner.GoToSomePointData.UpdateToGo(false, 1f, 1f);
            if (fork != null && fork.Engage.FlankActive)
            {
                var anchor = fork.Engage.FlankAnchor;
                BotOwner.Steering.LookToPoint(new Vector3(anchor.X, anchor.Y + 1.2f, anchor.Z));
                fork.SetLook("at the flank's anchor", Time.time);
            }
            else if (fork != null && fork.LookOutwardWhileMoving && fork.GuardOutward.sqrMagnitude > 0.01f)
            {
                BotOwner.Steering.LookToDirection(fork.GuardOutward, 120f);
                fork.SetLook("outward on a Defend shift", Time.time);
            }
            else
            {
                BotOwner.Steering.LookToMovingDirection();
                fork?.SetLook("where it walks", Time.time);
            }
        }
    }

    /// <summary>Stands still and watches; see the file header for the look priority.</summary>
    public class GuardWatchLogic : McsBotBaseLogic
    {
        /// <summary>
        /// Pose level at every guard post (0 = full crouch, 1 = standing). Fork v3-v4 crouched only at
        /// posts planned with cover and stood upright at the others. In the build-4 raids (2026-11-04),
        /// bots standing upright at bare posts made up 212 of the 305 s that bots stood still in the
        /// open for 5 s or more.
        /// </summary>
        private const float WATCH_POSE = 0.3f;

        /// <summary>Created by BigBrain for one bot.</summary>
        public GuardWatchLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        /// <summary>Stops movement, resets the stuck timer's reference position, crouches, and starts a fresh look.</summary>
        public override void Start()
        {
            BotOwner.Mover._lastPos = BotOwner.Position;
            BotOwner.StopMove();
            BotOwner.SetPose(WATCH_POSE);
            BotOwner.GetMcsBotPlayerData()?.Fork.Look.Restart();
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
            var mcsData = BotOwner.GetMcsBotPlayerData();
            var sainEnemy = SainBridge.GetSain(BotOwner)?.GoalEnemy;
            if (sainEnemy != null && sainEnemy.IsVisible && sainEnemy.EnemyPlayer != null)
            {
                BotOwner.Steering.LookToPoint(sainEnemy.EnemyPlayer.Position + Vector3.up * 1.2f);
                mcsData?.Fork.SetLook("at an enemy it sees", Time.time);
                return;
            }

            if (mcsData?.McsAILeadPlayer != null && TeamModes.TryGetThreat(mcsData.McsAILeadPlayer, out var threat))
            {
                BotOwner.Steering.LookToPoint(threat + Vector3.up * 1.2f);
                mcsData.Fork.SetLook("at the squad's threat or contact report", Time.time);
                return;
            }

            if (mcsData != null)
            {
                GuardLook.Scan(BotOwner, mcsData.Fork);
                mcsData.Fork.SetLook("scanning", Time.time);
            }
        }
    }

    /// <summary>
    /// The look scan both guard logics use: Core/LookScan.cs picks each look; this measures each
    /// candidate's clear sight line with a raycast at eye height and turns the bot's head. Main thread
    /// only (it keeps the current eye point and watch direction in static fields for the raycasts).
    /// </summary>
    internal static class GuardLook
    {
        /// <summary>Eye height above the bot's feet for the sight-line raycasts (m); a crouched bot's view is near 1 m, a standing one's near 1.6 m.</summary>
        private const float EYE_HEIGHT = 1.3f;

        private static readonly LookScanSettings _scan = new();
        private static readonly Func<float, float> _clearDistance = ClearDistance;
        private static Vector3 _eye;
        private static Vector3 _outward;

        /// <summary>
        /// Turns the bot's head to its current look, picking a new look when the old one has run out.
        /// Looks are offsets from fork.GuardOutward (the bot's facing when it has none).
        /// </summary>
        internal static void Scan(BotOwner bot, BotForkState fork)
        {
            var outward = fork.GuardOutward;
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.01f)
            {
                outward = bot.Transform.forward;
                outward.y = 0f;
            }
            _outward = outward.normalized;
            _eye = bot.Position + Vector3.up * EYE_HEIGHT;
            fork.Look.Tick(Time.time, _clearDistance, _scan);
            bot.Steering.LookToDirection(Quaternion.Euler(0f, fork.Look.Offset, 0f) * _outward, 120f);
        }

        /// <summary>Metres of clear view from the current eye point at an offset from the current watch direction, up to the scan's cap.</summary>
        private static float ClearDistance(float offset)
        {
            var direction = Quaternion.Euler(0f, offset, 0f) * _outward;
            return Physics.Raycast(_eye, direction, out var hit, _scan.ViewCapMetres, LayersMaskController.HighPolyWithTerrainMask)
                ? hit.distance
                : _scan.ViewCapMetres;
        }
    }
}
