// McsGuardLayer.cs -- the fork's priority-19 floor layer for carry bots.
//
// Registered by BrainMgr.InitCustomLayerMaps and injected per bot like MCS's own
// layer. It is always active for carry bots, so it runs whenever nothing above
// it is: the C2 layer has stepped aside (C2Gate) and SAIN has no decision. That
// happens while guarding (player standing still), in combat when this bot has
// nothing to shoot, and after a chase is cut (it walks the bot back).
//
// Behaviour: pick a guard spot 2.5-6 m from the player in this bot's sector,
// preferring low cover (C2Nav), walk there, then stand and scan outward, or look
// at a visible enemy or the squad's last known threat. Writes the bot's fork
// state (guard spot, scan direction). Reads and writes no files.

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
    /// <summary>Guard behaviour for carry bots when neither C2 nor SAIN is driving.</summary>
    public class McsGuardLayer : McsBaseLayer
    {
        /// <summary>Re-pick the guard spot once the player has moved this far (m) from where it was picked.</summary>
        private const float RESPOT_PLAYER_MOVE = 3f;

        /// <summary>A bot this close (m) to its spot counts as there.</summary>
        private const float AT_SPOT = 1.25f;

        /// <summary>Seconds between re-evaluations while moving or watching.</summary>
        private const float EVAL_INTERVAL = 0.5f;

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
        /// Chooses between walking to the guard spot and standing watch. Side effects: may pick a
        /// new guard spot and sets the bot's move target.
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
                var distanceToLead = Vector3.Distance(BotOwner.Position, leadPos);
                fork.MoveFast = fork.Leash.Returning || distanceToLead > 15f;

                if (!fork.HasGuardSpot || Vector3.Distance(fork.GuardSpotForPlayer, leadPos) > RESPOT_PLAYER_MOVE)
                {
                    PickSpot(data, leadPos);
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

                return new Action(typeof(GuardWatchLogic), "C2Sain:Watch");
            }
            catch (Exception e)
            {
                McsLogger.LogError(e);
                return new Action(typeof(HoldPositionLogic), "C2Sain:GuardException");
            }
        }

        /// <summary>
        /// Picks a guard spot in this bot's sector (sectors split 360 degrees between the living
        /// squad members), falling back to a plain follow spot. Writes the result to the fork state.
        /// </summary>
        private void PickSpot(Datas.McsBotPlayerData data, Vector3 leadPos)
        {
            var fork = data.Fork;
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

            var sectorYaw = 45f + myIndex * 360f / Math.Max(1, memberCount);
            if (C2Nav.TryPickGuardSpot(leadPos, BotOwner.Position, sectorYaw, _otherSpots, out var spot, out var outward))
            {
                fork.HasGuardSpot = true;
                fork.GuardSpot = spot;
                fork.GuardSpotForPlayer = leadPos;
                fork.GuardOutward = outward;
                return;
            }

            if (C2Nav.TryPickFollowSpot(leadPos, BotOwner.Position, out var followSpot, out _))
            {
                var away = followSpot - leadPos;
                away.y = 0f;
                fork.HasGuardSpot = true;
                fork.GuardSpot = followSpot;
                fork.GuardSpotForPlayer = leadPos;
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
