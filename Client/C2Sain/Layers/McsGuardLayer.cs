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
//   - Defend Me (the squad has a Defend zone, in combat or not): go to this bot's
//     Defend spot 3-5 m from the player (TeamModes.GetDefendPost), watching outward
//     on short shifts between spots, then watch from it;
//   - guard: go to this bot's post in the squad's guard plan (TeamModes.GetGuardPost,
//     planned by GuardPlanner: a protection detail around the player outdoors,
//     the approaches indoors, facing a contact report when there is one), then
//     watch from it. A bot counts as at its post or spot within 1.25 m, or once it
//     stalls within 3 m of it (Core/ArrivalWatch.cs, since fork v7);
//   - with no post or spot, fall back to a follow spot near the player.
// Writes the bot's fork state (guard spot, watch direction, cover flag, post
// kind, hurry and look-outward flags). Reads and writes no files.

using System;
using System.Collections.Generic;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using MiyakoCarryService.Client.Api;
using MiyakoCarryService.Client.Bots.Brain.Layers;
using MiyakoCarryService.Client.Bots.Brain.Logics;
using MiyakoCarryService.Client.C2Sain.Core;
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
        /// <summary>Re-pick a fallback follow spot once the player has moved this far (m) from where it was picked.</summary>
        private const float RESPOT_PLAYER_MOVE = 4f;

        /// <summary>When a bot counts as at its spot: within 1.25 m, or stalled within 3 m (Core/ArrivalWatch.cs).</summary>
        private static readonly ArrivalSettings _arrival = new();

        /// <summary>Seconds between re-evaluations while moving or watching.</summary>
        private const float EVAL_INTERVAL = 0.5f;

        /// <summary>
        /// Under Defend Me, a bot this close (m) to its spot keeps watching outward as it walks there (a
        /// shift); farther away (coming in from elsewhere) it looks where it is going.
        /// </summary>
        private const float SHIFT_LOOK_DISTANCE = 8f;

        private float _nextEval;

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

                var defending = TeamModes.IsDefending(data.McsAILeadPlayer);
                var post = defending ? TeamModes.GetDefendPost(data.McsAILeadPlayer, BotOwner) : TeamModes.GetGuardPost(data.McsAILeadPlayer, BotOwner);
                if (post != null)
                {
                    fork.HasGuardSpot = true;
                    fork.GuardSpot = post.Spot;
                    fork.GuardSpotForPlayer = leadPos;
                    fork.GuardOutward = post.Watch;
                    fork.GuardSpotCovered = post.Covered;
                    fork.GuardPostKind = post.Kind;
                }
                else if (fork.GuardPostKind != "follow" || !fork.HasGuardSpot || Vector3.Distance(fork.GuardSpotForPlayer, leadPos) > RESPOT_PLAYER_MOVE)
                {
                    PickFollowSpot(fork, leadPos);
                }

                var toSpot = fork.HasGuardSpot ? Vector3.Distance(BotOwner.Position, fork.GuardSpot) : 0f;
                fork.LookOutwardWhileMoving = defending && post != null && toSpot <= SHIFT_LOOK_DISTANCE;
                if (fork.HasGuardSpot && !Arrived(fork, data))
                {
                    _currentMoveTarget = fork.GuardSpot;
                    ApplyMovePoint();
                    return new Action(typeof(GuardMoveLogic), fork.Leash.Returning ? "C2Sain:Return" : defending ? "C2Sain:ToDefendSpot" : "C2Sain:ToGuardSpot");
                }

                if (!fork.HasGuardSpot && distanceToLead > ForkConfig.FollowDistance.Value && TryRefreshLeadTarget(leadPos, now))
                {
                    ApplyMovePoint();
                    return new Action(typeof(GuardMoveLogic), "C2Sain:GuardFollow");
                }

                if (defending)
                {
                    return new Action(typeof(GuardWatchLogic), fork.GuardSpotCovered ? "C2Sain:DefendFromCover" : "C2Sain:Defend");
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
        /// Whether the bot is at its guard or Defend spot: within 1.25 m, or stalled within 3 m of it
        /// (Core/ArrivalWatch.cs). Fork v3-v6 required the 1.25 m, so a bot that physically stopped
        /// short stayed in the walk step for good, looking where it last walked. Logs, with "Debug:
        /// diagnostic log" on, the first time a spot is reached by stalling.
        /// </summary>
        private bool Arrived(BotForkState fork, MiyakoCarryService.Client.Datas.McsBotPlayerData data)
        {
            var wasByStall = fork.Arrival.ArrivedByStall;
            var p = BotOwner.Position;
            var arrived = fork.Arrival.Update(Time.time, new V3(p.x, p.y, p.z), new V3(fork.GuardSpot.x, fork.GuardSpot.y, fork.GuardSpot.z), _arrival);
            if (fork.Arrival.ArrivedByStall && !wasByStall && ForkConfig.DebugLog?.Value == true)
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] arrival: {data.Player?.Profile?.Nickname} stalled {fork.Arrival.Distance:0.0} m short of its {fork.GuardPostKind} spot; watching from there");
            }
            return arrived;
        }

        /// <summary>
        /// Fallback when the squad's guard plan has no post for this bot: a follow spot near the player,
        /// watching away from them. Writes the result to the fork state.
        /// </summary>
        private void PickFollowSpot(BotForkState fork, Vector3 leadPos)
        {
            fork.GuardPostKind = "follow";
            fork.GuardSpotCovered = false;
            fork.GuardSpotForPlayer = leadPos;
            if (C2Nav.TryPickFollowSpot(leadPos, BotOwner.Position, out var followSpot, out _))
            {
                var away = followSpot - leadPos;
                away.y = 0f;
                fork.HasGuardSpot = true;
                fork.GuardSpot = followSpot;
                fork.GuardOutward = away;
                return;
            }
            fork.HasGuardSpot = false;
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
