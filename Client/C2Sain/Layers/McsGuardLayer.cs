// McsGuardLayer.cs -- the fork's priority-19 floor layer for carry bots.
//
// Registered by BrainMgr.InitCustomLayerMaps and injected per bot like MCS's own
// layer. It is always active for carry bots, so it runs whenever nothing above
// it is: the C2 layer has stepped aside (C2Gate) and SAIN has no decision. That
// happens while guarding (player standing still), in combat when this bot has
// nothing to shoot, after a chase is cut (it walks the bot back), and during an
// Engage order's flank leg (EngageTactics vetoes SAIN's decisions for the leg).
//
// Behaviour, in order:
//   - flank leg: go to the leg's point, sprinting while far from it;
//   - guard: pick a spot 2.5-8 m from the player in this bot's sector,
//     preferring hard cover or a bush (C2Nav), walk there, then watch: outward,
//     or toward a contact report ("I see the enemy"), whose spots fan out on the
//     player's side facing the mark, or at a visible enemy or the squad's threat.
// Writes the bot's fork state (guard spot, watch direction, cover flag, report
// id, hurry flag). Reads and writes no files.

using System;
using System.Collections.Generic;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using MiyakoCarryService.Client.Api;
using MiyakoCarryService.Client.Bots.Brain.Layers;
using MiyakoCarryService.Client.Bots.Brain.Logics;
using MiyakoCarryService.Client.C2Sain.Logics;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain.Layers
{
    /// <summary>Guard and flank-leg behaviour for carry bots when neither C2 nor SAIN is driving.</summary>
    public class McsGuardLayer : McsBaseLayer
    {
        /// <summary>Re-pick the guard spot once the player has moved this far (m) from where it was picked.</summary>
        private const float RESPOT_PLAYER_MOVE = 3f;

        /// <summary>A bot this close (m) to its spot counts as there.</summary>
        private const float AT_SPOT = 1.25f;

        /// <summary>Seconds between re-evaluations while moving or watching.</summary>
        private const float EVAL_INTERVAL = 0.5f;

        /// <summary>
        /// Sector offsets (degrees from the direction of a reported contact) for the squad's guards, by
        /// their order in the squad: they spread across the front facing the mark.
        /// </summary>
        private static readonly float[] ReportSectorOffsets = { -35f, 35f, -70f, 70f, 0f, -105f, 105f };

        private float _nextEval;
        private readonly List<Vector3> _otherSpots = new();
        private McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        /// <summary>Creates the layer; BigBrain calls this once per bot.</summary>
        public McsGuardLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
        }

        /// <summary>Adds the guard logics' end checks to MCS's action map.</summary>
        public override void InitActionMap()
        {
            base.InitActionMap();
            RegisterAction(typeof(GuardMoveLogic), EndGuardMove);
            RegisterAction(typeof(GuardWatchLogic), EndGuardWatch);
        }

        /// <summary>Always active for a carry bot with a living lead: this layer is the floor.</summary>
        public override bool IsActive()
        {
            return IsMcsBotPlayer && McsBotPlayerData?.LeadPlayer != null;
        }

        /// <summary>
        /// Chooses between a flank leg, walking to the guard spot and standing watch. Side effects: may
        /// pick a new guard spot and sets the bot's move target.
        /// </summary>
        public override Action GetNextAction()
        {
            try
            {
                var now = Time.time;
                _nextEval = now + EVAL_INTERVAL;

                var data = McsBotPlayerData;
                var lead = data?.LeadPlayer;
                if (data == null || lead == null)
                {
                    return new Action(typeof(HoldPositionLogic), "C2Sain:GuardNoLead");
                }

                var fork = data.Fork;
                var leadPos = lead.Position;

                if (fork.Engage.FlankActive)
                {
                    var point = new Vector3(fork.Engage.FlankPoint.X, fork.Engage.FlankPoint.Y, fork.Engage.FlankPoint.Z);
                    fork.MoveFast = Vector3.Distance(BotOwner.Position, point) > EngageTactics.FLANK_SPRINT_DISTANCE;
                    _currentMoveTarget = point;
                    ApplyMovePoint();
                    return new Action(typeof(GuardMoveLogic), "C2Sain:Flank");
                }

                var distanceToLead = Vector3.Distance(BotOwner.Position, leadPos);
                fork.MoveFast = fork.Leash.Returning || distanceToLead > 15f;

                var hasReport = TeamModes.TryGetReport(data.McsAILeadPlayer, out var reportPos, out var reportId);
                var reportChanged = fork.GuardReportId != (hasReport ? reportId : 0);
                if (!fork.HasGuardSpot || reportChanged || Vector3.Distance(fork.GuardSpotForPlayer, leadPos) > RESPOT_PLAYER_MOVE)
                {
                    PickSpot(data, leadPos, hasReport ? reportPos : null, hasReport ? reportId : 0);
                }

                if (fork.HasGuardSpot && Vector3.Distance(BotOwner.Position, fork.GuardSpot) > AT_SPOT)
                {
                    _currentMoveTarget = fork.GuardSpot;
                    ApplyMovePoint();
                    return new Action(typeof(GuardMoveLogic), fork.Leash.Returning ? "C2Sain:Return" : "C2Sain:ToGuardSpot");
                }

                if (!fork.HasGuardSpot && distanceToLead > ForkConfig.FollowDistance.Value && TryRefreshLeadTarget(leadPos, now))
                {
                    ApplyMovePoint();
                    return new Action(typeof(GuardMoveLogic), "C2Sain:GuardFollow");
                }

                return new Action(typeof(GuardWatchLogic), fork.GuardSpotCovered ? "C2Sain:WatchFromCover" : "C2Sain:Watch");
            }
            catch (Exception e)
            {
                McsLogger.LogError(e);
                return new Action(typeof(HoldPositionLogic), "C2Sain:GuardException");
            }
        }

        /// <summary>
        /// Picks a guard spot in this bot's sector, falling back to a plain follow spot, and writes the
        /// result to the fork state. Without a report, sectors split 360 degrees between the living squad
        /// members; with one, they fan out across the front facing the mark.
        /// </summary>
        /// <param name="data">The bot's MCS data.</param>
        /// <param name="leadPos">The player's position.</param>
        /// <param name="threat">The reported contact to face, or null.</param>
        /// <param name="reportId">The report's id (0 without one), remembered so a new report re-picks.</param>
        private void PickSpot(Datas.McsBotPlayerData data, Vector3 leadPos, Vector3? threat, int reportId)
        {
            var fork = data.Fork;
            fork.GuardReportId = reportId;
            _otherSpots.Clear();
            var memberCount = 0;
            var myIndex = 0;
            foreach (var member in McsMgr.GetAllAliveMcsSquadMembersByMcsLeadId(data.LeadPlayer.ProfileId))
            {
                var otherOwner = member?.AIData?.BotOwner;
                if (otherOwner == null)
                {
                    continue;
                }
                if (otherOwner == BotOwner)
                {
                    myIndex = memberCount;
                }
                else
                {
                    var otherFork = otherOwner.GetMcsBotPlayerData()?.Fork;
                    if (otherFork != null && otherFork.HasGuardSpot)
                    {
                        _otherSpots.Add(otherFork.GuardSpot);
                    }
                }
                memberCount++;
            }

            float sectorYaw;
            if (threat.HasValue)
            {
                var toThreat = threat.Value - leadPos;
                var yaw = Mathf.Atan2(toThreat.x, toThreat.z) * Mathf.Rad2Deg;
                sectorYaw = yaw + ReportSectorOffsets[myIndex % ReportSectorOffsets.Length];
            }
            else
            {
                sectorYaw = 45f + myIndex * 360f / Math.Max(1, memberCount);
            }

            if (C2Nav.TryPickGuardSpot(leadPos, BotOwner.Position, sectorYaw, _otherSpots, threat, out var spot, out var watch, out var covered))
            {
                fork.HasGuardSpot = true;
                fork.GuardSpot = spot;
                fork.GuardSpotForPlayer = leadPos;
                fork.GuardOutward = watch;
                fork.GuardSpotCovered = covered;
                return;
            }

            if (C2Nav.TryPickFollowSpot(leadPos, BotOwner.Position, out var followSpot, out _))
            {
                var away = threat.HasValue ? threat.Value - followSpot : followSpot - leadPos;
                away.y = 0f;
                fork.HasGuardSpot = true;
                fork.GuardSpot = followSpot;
                fork.GuardSpotForPlayer = leadPos;
                fork.GuardOutward = away;
                fork.GuardSpotCovered = false;
                return;
            }

            fork.HasGuardSpot = false;
            fork.GuardSpotCovered = false;
        }

        /// <summary>Ends a guard move on arrival or at the next evaluation.</summary>
        private bool EndGuardMove()
        {
            return Time.time >= _nextEval || BotOwner.GoToSomePointData.IsCome();
        }

        /// <summary>Ends a watch at the next evaluation so a moving player or new threat is noticed.</summary>
        private bool EndGuardWatch()
        {
            return Time.time >= _nextEval;
        }
    }
}
