using System;
using Comfort.Common;
using EFT;
using MiyakoCarryService.Client.Bots.Brain.Logics;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.Bots.Brain.Layers
{
    public class McsBrainLayer : McsBaseLayer
    {
        public McsBrainLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {

        }

        public override Action GetNextAction()
        {
            try
            {
                var time = Time.time;
                if (McsBotPlayerData != null && (McsBotPlayerData.MoveResetRequested || McsBotPlayerData.HasIntent(Intents.ShouldTeleport)))
                {
                    McsBotPlayerData.MoveResetRequested = false;
                    McsBotPlayerData.RemoveIntent(Intents.ShouldTeleport);
                    NavBridge.Cancel();
                    _currentMoveTarget = null;
                    _lastTargetPos = Vector3.zero;
                    _lastCalcCorners = null;
                    _lastCanRunResult = false;
                    _currentMoveRetries = 0;
                    _nextUpdatePosTime = 0f;
                }

                var mcsLeadPlayerPos = Vector3.zero;
                var sqrDistance = 0f;
                var tooClose = false;
                var needHeal = false;
                var goalEnemy = BotOwner.Memory.GoalEnemy;

                if (goalEnemy != null && Tools.IsForbiddenEnemy(goalEnemy.Person))
                {
                    BotOwner.Memory.GoalEnemy = null;
                    if (BotOwner.EnemiesController.EnemyInfos.ContainsKey(goalEnemy.Person))
                    {
                        BotOwner.EnemiesController.Remove(goalEnemy.Person);
                    }
                    goalEnemy = null;
                }

                var canShootNow = CanShootNow();
                if (canShootNow)
                {
                    _lastCanShootTime = time;
                }

                #region McsAvoidDangerLayer

                if (BotOwner.FlashGrenade.IsFlashed)
                {
                    return new Action(typeof(FlashedLogic), "Mcs:Flashed");
                }

                if (BotOwner.BotTurnAwayLight.IsActive)
                {
                    return new Action(typeof(HoldPositionLogic), "Mcs:TurnAwayLight");
                }

                if (BotOwner.ArtilleryDangerPlace.ShallRunAway())
                {
                    return new Action(typeof(RunAwayArtilleryLogic), "Mcs:RunAwayArtillery");
                }

                if (BotOwner.BewareGrenade.McsShallRunAway())
                {
                    return new Action(typeof(RunAwayGrenadeLogic), "Mcs:RunAwayGrenade");
                }

                if (BotOwner.BewareBTR.ShallRunAway())
                {
                    return new Action(typeof(RunAwayBTRLogic), "Mcs:RunAwayBTR");
                }

                if (!BotOwner.Memory.HaveEnemy && BotOwner.SmokeGrenade.IsInSmoke)
                {
                    return new Action(typeof(GoToCoverPointLogic), "Mcs:PeaceSmoke");
                }

                if (!IsApproachingThreat() && BotOwner.BewarePlantedMine.CanDeactivate())
                {
                    return new Action(typeof(DeactivateMineLogic), "Mcs:DeactivateMine");
                }

                #endregion

                if (McsBotPlayerData == null)
                {
                    return new Action(typeof(HoldPositionLogic), "Mcs:LeadPosNull");
                }

                if (McsBotPlayerData.LeadPlayer == null)
                {
                    return new Action(typeof(HoldPositionLogic), "Mcs:LeadPlayerNull");
                }

                // Fork: Break off from combat -- run back to the player; C2Gate hands over to SAIN on arrival.
                if (McsBotPlayerData.Fork.BreakOff.Active)
                {
                    var breakOffLeadPos = BotOwner.GetMcsLeadPlayerPos(McsBotPlayerData);
                    if (TryRefreshLeadTarget(breakOffLeadPos, time))
                    {
                        ApplyMovePoint();
                        return new Action(typeof(GoToPointLogic), "C2Sain:BreakOff");
                    }
                    return new Action(typeof(HoldPositionLogic), "C2Sain:BreakOffNoPath");
                }

                var hasTravelTask = McsBotPlayerData.HasAnyIntent(Classification.TravelTaskIntents);
                // Fork: fighting is SAIN's job. C2 only runs MCS's own fight logic for bots SAIN does not manage.
                var fightActive = C2Sain.SainBridge.GetSain(BotOwner) == null
                    && ((goalEnemy != null && time - _lastCanShootTime <= (hasTravelTask ? 2f : 15f)) || IsApproachingThreat());

                ReportFightState(fightActive, time);

                needHeal = (BotOwner.Medecine.FirstAid.Damaged && BotOwner.Medecine.FirstAid.HaveSmth2Use) || (BotOwner.Medecine.SurgicalKit.Damaged && BotOwner.Medecine.SurgicalKit.HaveSmth2Use);
                var isEnemyPosLost = IsEnemyPosLost();
                mcsLeadPlayerPos = BotOwner.GetMcsLeadPlayerPos(McsBotPlayerData);
                sqrDistance = BotOwner.Position.McsSqrDistance(mcsLeadPlayerPos);
                tooClose = sqrDistance <= TOO_CLOSE_FROM_LEAD_DISTANCE * TOO_CLOSE_FROM_LEAD_DISTANCE;

                #region McsProxyLayer

                ReportTaskStart(
                    ref _wasProxying,
                    !fightActive
                        && McsBotPlayerData.LeadPlayer.HealthController.IsAlive
                        && (McsBotPlayerData.HasIntent(Intents.ShouldQuestProxyAction)
                            || McsBotPlayerData.HasIntent(Intents.ShouldLootProxyAction)
                            || McsBotPlayerData.HasIntent(Intents.ShouldInteractionProxyAction)
                            || McsBotPlayerData.HasIntent(Intents.ShouldStationaryWeaponProxyAction)),
                    EPhraseTrigger.Going,
                    BotOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null);

                if (!fightActive && McsBotPlayerData.LeadPlayer.HealthController.IsAlive)
                {
                    if (McsBotPlayerData.HasIntent(Intents.ShouldQuestProxyAction)
                        || McsBotPlayerData.HasIntent(Intents.ShouldLootProxyAction)
                        || McsBotPlayerData.HasIntent(Intents.ShouldInteractionProxyAction)
                        || McsBotPlayerData.HasIntent(Intents.ShouldStationaryWeaponProxyAction))
                    {

                        if (McsBotPlayerData.HasIntent(Intents.ShouldHoldPosition))
                        {
                            return new Action(typeof(HoldPositionLogic), "Mcs:HoldPositionForProxyAction");
                        }

                        if (McsBotPlayerData.TargetPos.HasValue)
                        {
                            if (TryRefreshCommonTarget(McsBotPlayerData.TargetPos, time))
                            {
                                ApplyMovePoint();
                                if (needHeal)
                                {
                                    RefreshStuckTimer();
                                    return new Action(typeof(HealLogic), "Mcs:HealWhileProxy");
                                }
                                return new Action(typeof(GoToExcuteProxyActionLogic), "Mcs:GoToExcuteProxyAction");
                            }
                        }
                    }
                }

                #endregion
                #region McsEscortLayer

                ReportTaskStart(
                    ref _wasEscorting,
                    !fightActive
                        && McsBotPlayerData.LeadPlayer.HealthController.IsAlive
                        && ((McsBotPlayerData.HasIntent(Intents.ShouldEscort) && McsBotPlayerData.TargetPos.HasValue)
                            || McsBotPlayerData.HasIntent(Intents.ShouldEscortToBtr)),
                    EPhraseTrigger.FollowMe);

                if (!fightActive && McsBotPlayerData.LeadPlayer.HealthController.IsAlive)
                {
                    if (McsBotPlayerData.HasIntent(Intents.ShouldEscortToBtr))
                    {
                        var btrController = Singleton<GameWorld>.Instance.BtrController;
                        var side = btrController.BtrView.GetBtrSide(1);
                        if (side != null)
                        {
                            var doorPos = side.GoInPoints().Item1;
                            if (_nextUpdatePosTime < time)
                            {
                                McsBotPlayerData.TargetPos = doorPos;
                                UpdateEscortMoveTarget(McsBotPlayerData.TargetPos, out float nextTime);
                                _nextUpdatePosTime = time + nextTime;
                            }

                            if (_currentMoveTarget.HasValue)
                            {
                                ApplyMovePoint();
                                if (needHeal)
                                {
                                    RefreshStuckTimer();
                                    return new Action(typeof(HealLogic), "Mcs:HealWhileEscort");
                                }
                                return new Action(typeof(EscortToPointByWayLogic), "Mcs:EscortToBtr");
                            }
                        }
                    }

                    if ((McsBotPlayerData.HasIntent(Intents.ShouldEscort) && McsBotPlayerData.TargetPos.HasValue)
                        || (McsBotPlayerData.HasIntent(Intents.ShouldEscortToBtr) && Singleton<GameWorld>.Instance.BtrController.BtrView.GetBtrSide(1) != null))
                    {
                        if (TryRefreshEscortTarget(McsBotPlayerData.TargetPos, time))
                        {
                            ApplyMovePoint();
                            if (needHeal)
                            {
                                RefreshStuckTimer();
                                return new Action(typeof(HealLogic), "Mcs:HealWhileEscort");
                            }
                            return new Action(typeof(EscortToPointByWayLogic), "Mcs:EscortToPoint");
                        }
                    }
                }

                #endregion
                #region StationaryWeapon

                if (McsBotPlayerData.HasIntent(Intents.ShouldUseStationaryWeapon))
                {
                    var stationary = BotOwner.WeaponManager.Stationary;
                    if (_cachedProxyTargetId == null || _cachedStationaryWeaponData == null || McsBotPlayerData.ProxyTargetId != _cachedProxyTargetId)
                    {
                        _cachedProxyTargetId = McsBotPlayerData.ProxyTargetId;
                        _cachedStationaryWeaponData = Singleton<GameWorld>.Instance.FindInteractableObjectData(McsBotPlayerData.ProxyTargetId) as StationaryWeaponData;
                    }

                    var stationaryWeapon = _cachedStationaryWeaponData?.StationaryWeapon;
                    var stationaryWeaponLink = _cachedStationaryWeaponData?.StationaryWeaponLink;

                    if (_cachedProxyTargetId != null && stationaryWeapon != null && stationaryWeaponLink != null)
                    {
                        var operatorPos = stationaryWeapon.OperatorPosition;
                        var sqrToOperator = BotOwner.Position.McsSqrDistance(operatorPos);

                        if (needHeal && isEnemyPosLost)
                        {
                            if (stationary.CurLink != null && stationary.Taken)
                            {
                                stationary.DropCurWeapon(false, true);
                            }
                            RefreshStuckTimer();
                            return new Action(typeof(StationaryHealLogic), "Mcs:StationaryHealing");
                        }

                        if (stationary.CurLink == null)
                        {
                            stationary.SetTargetStationary(stationaryWeaponLink);
                        }

                        TryRefreshCommonTarget(operatorPos, time);

                        if (sqrToOperator >= 1.5f && _currentMoveTarget.HasValue)
                        {
                            if (sqrToOperator < _lastSqrToOperator - 0.5f)
                            {
                                _goToStationaryStuckTime = time;
                                _lastSqrToOperator = sqrToOperator;
                            }
                            else if (_goToStationaryStuckTime <= 0f || _goToStationaryStuckTime > time)
                            {
                                _goToStationaryStuckTime = time;
                                _lastSqrToOperator = sqrToOperator;
                            }

                            if (sqrToOperator < 9f && time - _goToStationaryStuckTime > 8f)
                            {
                                BotOwner.StopMove();
                                BotOwner.Mover.AllowTeleport();
                                BotOwner.GetPlayer.Teleport(operatorPos, true);
                                BotOwner.Mover._lastGoodCastPoint = BotOwner.Mover._prevSuccessLinkedFrom = BotOwner.Mover._prevLinkPos = BotOwner.Mover.PositionOnWayInner = operatorPos;
                                BotOwner.Mover._lastGoodCastPointTime = time;
                                BotOwner.Mover._prevPosLinkedTime = 0f;
                                BotOwner.Mover.SetPlayerToNavMesh(operatorPos);
                                BotOwner.Mover.RecalcWay();
                                BotOwner.Mover.Pause = true;

                                _goToStationaryStuckTime = time;
                                _lastSqrToOperator = float.MaxValue;
                                RefreshStuckTimer();

                                if (stationary.CurLink == null)
                                {
                                    stationary.SetTargetStationary(stationaryWeaponLink);
                                }
                                return new Action(typeof(GoToPointLogic), "Mcs:GoToStationaryPos");
                            }

                            ApplyMovePoint();
                            return new Action(typeof(GoToPointLogic), "Mcs:GoToStationaryPos");
                        }
                        else
                        {
                            _goToStationaryStuckTime = -999f;
                            _lastSqrToOperator = float.MaxValue;
                        }

                        var isEnemyAtSector = stationary.IsEnemyAtSector(stationary.CurLink);

                        if (goalEnemy == null)
                        {
                            TrySelectLastHitShooter(time);
                            TrySelectLeadThreatEnemy(time);
                            goalEnemy = BotOwner.Memory.GoalEnemy;
                        }

                        if (goalEnemy == null)
                        {
                            ScanSector(stationaryWeaponLink);
                            return new Action(typeof(HoldPositionLogic), "Mcs:ScanSector");
                        }

                        if (isEnemyAtSector)
                        {
                            if (stationaryWeaponLink.HaveAmmo()
                                && stationary.GetCurrentDecision() == BotLogicDecision.shootFromStationary
                                && goalEnemy.CanShoot
                                && IsTargetPitchReachable(stationaryWeapon, goalEnemy.CurrPosition))
                            {
                                if (McsBotPlayerData.HasIntent(Intents.ShouldHoldPosition))
                                {
                                    McsBotPlayerData.RemoveIntent(Intents.ShouldHoldPosition);
                                }
                                BotOwner.ShootData.EndShoot();
                                return new Action(typeof(ShootFromStationaryLogic), "Mcs:UseStationaryWeapon");
                            }

                            ScanSector(stationaryWeaponLink);
                            return new Action(typeof(HoldPositionLogic), "Mcs:ScanSector");
                        }

                        if (stationary.Taken)
                        {
                            stationary.DropCurWeapon(false, true);
                        }
                        McsBotPlayerData.AddIntent(Intents.ShouldHoldPosition);
                    }
                }
                else
                {
                    _goToStationaryStuckTime = -999f;
                    _lastSqrToOperator = float.MaxValue;
                }

                if (McsBotPlayerData.HasIntent(Intents.ShouldUseStationaryWeapon) && goalEnemy == null)
                {
                    return new Action(typeof(HoldPositionLogic), "Mcs:ScanSector");
                }

                #endregion
                #region McsFightLayer

                if (fightActive)
                {
                    if (goalEnemy != null && (goalEnemy.Person == null || goalEnemy.Person.HealthController == null || !goalEnemy.Person.HealthController.IsAlive || goalEnemy.Person.AIData.BotOwner.Brain == null || goalEnemy.Person.AIData.BotOwner.BotState is EBotState.NonActive))
                    {
                        BotOwner.Memory.GoalEnemy = null;
                        if (BotOwner.EnemiesController.EnemyInfos.ContainsKey(goalEnemy.Person))
                        {
                            BotOwner.EnemiesController.Remove(goalEnemy.Person);
                        }
                        _nextRecalcGoalTime = 0f;
                    }

                    if (time >= _nextRecalcGoalTime)
                    {
                        _nextRecalcGoalTime = time + 0.1f;
                        UpdateGoalEnemyWithHysteresis(time);
                    }

                    goalEnemy = BotOwner.Memory.GoalEnemy;

                    if (goalEnemy == null)
                    {
                        return new Action(typeof(HoldPositionLogic), "Mcs:FightNoEnemy");
                    }

                    ReportFirstContact(goalEnemy, time);

                    var haveBullets = BotOwner?.WeaponManager?.HaveBullets;

                    TrackCoverEnter(time);

                    if (_coverEnterTime > 0f
                        && time - _coverEnterTime > SHIFT_COVER_MIN_TIME
                        && time - GetEnemyLastSeenTime() > 30f
                        && !McsBotPlayerData.HasAnyIntent(Intents.ShouldHoldPosition, Intents.ShouldFollowMe, Intents.ShouldKeepFormation))
                    {
                        if (TryGetCombatCoverTarget(mcsLeadPlayerPos, out var shiftCoverPos))
                        {
                            UpdateCommonMoveTarget(shiftCoverPos, out var shiftNextTime);
                            if (_currentMoveTarget.HasValue)
                            {
                                _coverEnterTime = 0f;
                                ApplyMovePoint();
                                return new Action(typeof(GoToPointLogic), "Mcs:ShiftCover");
                            }
                        }
                        else
                        {
                            _coverEnterTime = time - SHIFT_COVER_MIN_TIME + 5f;
                        }
                    }

                    if (haveBullets.Value && IsShootFromCoverConditionAllFine())
                    {
                        return new Action(typeof(CoverPeakLogic), "Mcs:CoverPeak");
                    }

                    if (BotOwner.NearDoorData.RecentlyClosedDoorCheckTime + 0.3f < time && BotOwner.BotsGroup.EnemyLastSeenTimeReal + 7f >= time && GetCrossPoint(goalEnemy))
                    {
                        BotOwner.Memory.Spotted(false, null, null);
                    }

                    if (!CheckFirearmsAnimatorState())
                    {
                        BotOwner.TryResetHandsState();
                    }

                    if (ShouldUseMeleeAttack())
                    {
                        return new Action(typeof(MeleeAttackLogic), "Mcs:MeleeAttack");
                    }

                    if (!haveBullets.Value)
                    {
                        BotOwner.WeaponManager.Reload.McsTryReload();
                    }
                    else if (!goalEnemy.CanShoot && BotOwner.McsGetCurrentMagAmmoRatio() <= 0.3f)
                    {
                        BotOwner.WeaponManager.Reload.McsTryReload();
                    }

                    if (BotOwner.WeaponManager.UnderbarrelLauncherController.NeedToReload())
                    {
                        BotOwner.WeaponManager.UnderbarrelLauncherController.TryReload();
                    }

                    McsBotPlayerData.UpdateSuppressionDecay();
                    TrySelectLastHitShooter(time);

                    TrySelectLeadThreatEnemy(time);
                    goalEnemy = BotOwner.Memory.GoalEnemy;

                    UpdateCoverToShoot();

                    if (!goalEnemy.IsVisible && BotOwner.SmokeGrenade.ShallShoot() && BotOwner.Position.McsSqrDistance(goalEnemy.Person.Position) <= 40f * 40f)
                    {
                        return new Action(typeof(ShootToSmokeLogic), "Mcs:SmokeGrenad");
                    }
                    else
                    {
                        var botToEnemySqrDist = BotOwner.Position.McsSqrDistance(goalEnemy.Person.Position);
                        var enemyToLeadSqrDist = mcsLeadPlayerPos.McsSqrDistance(goalEnemy.Person.Position);

                        if (goalEnemy.IsVisible)
                        {
                            _lastEnemyVisibleTime = time;
                        }

                        if (haveBullets.Value
                            && botToEnemySqrDist <= (15f * 15f)
                            && (time - _lastEnemyVisibleTime <= 1f || time - McsBotPlayerData.LastHitTime <= 2f)
                            && !McsBotPlayerData.HasAnyIntent(Intents.ShouldHoldPosition, Intents.ShouldFollowMe, Intents.ShouldKeepFormation, Intents.ShouldUseStationaryWeapon))
                        {
                            return new Action(typeof(DogFightLogic), "Mcs:DogFight");
                        }

                        var safeFire = false;
                        BotOwner closestFriend = null;
                        var sqrDist = float.MaxValue;
                        if (canShootNow)
                        {
                            closestFriend = BotOwner.Covers.GetClosestFriend(out sqrDist);
                            safeFire = sqrDist >= 1f || closestFriend == null || closestFriend.Id > BotOwner.Id;
                        }

                        if (safeFire && haveBullets.Value)
                        {
                            if (goalEnemy.IsVisible)
                            {
                                UpdateHoldGroundTimer(goalEnemy, time);

                                if (botToEnemySqrDist <= (20f * 20f)
                                    && !McsBotPlayerData.IsHeavySuppressed
                                    && !McsBotPlayerData.HasAnyIntent(Intents.ShouldHoldPosition, Intents.ShouldFollowMe, Intents.ShouldKeepFormation)
                                    && !BotOwner.GoToSomePointData.IsCome())
                                {
                                    return new Action(typeof(AttackMovingLogic), "Mcs:AttackMovingClose");
                                }

                                var holdGroundExpired = IsEnemyLookingAtMe(goalEnemy) && time - _holdGroundStartTime >= _holdGroundDuration;
                                var heavySuppressed = McsBotPlayerData.IsHeavySuppressed;

                                if ((holdGroundExpired || heavySuppressed)
                                    && !McsBotPlayerData.HasAnyIntent(Intents.ShouldHoldPosition, Intents.ShouldFollowMe, Intents.ShouldKeepFormation)
                                    && TryGetCombatCoverTarget(mcsLeadPlayerPos, out var coverPos))
                                {
                                    UpdateCommonMoveTarget(coverPos, out var coverNextTime);
                                    if (_currentMoveTarget.HasValue)
                                    {
                                        ApplyMovePoint();
                                        return new Action(typeof(GoToPointLogic), heavySuppressed ? "Mcs:SuppressedSeekCover" : "Mcs:SeekCover");
                                    }
                                }

                                if (!BotOwner.GoToSomePointData.IsCome() && !McsBotPlayerData.HasAnyIntent(Intents.ShouldHoldPosition, Intents.ShouldFollowMe, Intents.ShouldKeepFormation))
                                {
                                    return new Action(typeof(AttackMovingLogic), "Mcs:AttackMoving");
                                }
                                else
                                {
                                    return new Action(typeof(StandAndShootLogic), "Mcs:StandAndShoot");
                                }
                            }
                            else
                            {
                                _holdGroundEnemyId = null;

                                if (botToEnemySqrDist <= RUSH_NEAR_ENEMY_SQUARE_DIST && ShallRushEnemy(enemyToLeadSqrDist, goalEnemy, hasTravelTask))
                                {
                                    TryRushJump(goalEnemy, time);
                                    return new Action(typeof(RunToEnemyLogic), "Mcs:RushEnemyNear");
                                }

                                var timeSinceSeen = time - GetEnemyLastSeenTime();
                                var shotByThisEnemy = IsShotByEnemyRecently(goalEnemy, time);
                                if (timeSinceSeen <= SUPPRESS_TIME_SINCE_SEEN || shotByThisEnemy)
                                {
                                    return new Action(typeof(SuppressFireLogic), "Mcs:SuppressFire");
                                }

                                if (ShallRushEnemy(enemyToLeadSqrDist, goalEnemy, hasTravelTask))
                                {
                                    TryRushJump(goalEnemy, time);
                                    return new Action(typeof(RunToEnemyLogic), "Mcs:RushEnemy");
                                }

                                if (McsBotPlayerData.HasIntent(Intents.ShouldGoToPoint))
                                {
                                    if (TryRefreshCommonTarget(McsBotPlayerData.TargetPos, time))
                                    {
                                        ApplyMovePoint();
                                        return new Action(typeof(GoToPointLogic), "Mcs:GoToPointCommand");
                                    }
                                    else
                                    {
                                        return new Action(typeof(HoldPositionLogic), "Mcs:GoToLootTargetPosNotFound");
                                    }
                                }

                                if (McsBotPlayerData.HasIntent(Intents.ShouldHoldPosition))
                                {
                                    if (needHeal && isEnemyPosLost)
                                    {
                                        RefreshStuckTimer();
                                        return new Action(typeof(HealLogic), "Mcs:FightHealing4");
                                    }
                                    return new Action(typeof(HoldPositionLogic), "Mcs:HoldPositionCommand");
                                }

                                TryRefreshLeadTarget(mcsLeadPlayerPos, time);

                                if (needHeal && isEnemyPosLost)
                                {
                                    RefreshStuckTimer();
                                    ApplyMovePoint();
                                    return new Action(typeof(HealLogic), "Mcs:FightHealing5");
                                }

                                if (IsBeyondFollowDistance(sqrDistance) || tooClose)
                                {
                                    if (_currentMoveTarget.HasValue)
                                    {
                                        ApplyMovePoint();
                                        return new Action(typeof(GoToPointLogic), tooClose ? "Mcs:TooClose" : "Mcs:TooFar");
                                    }
                                }
                                else
                                {
                                    if (_nextPatrolTime + 4f < time)
                                    {
                                        _nextPatrolTime = time + 4f;
                                        if (_currentMoveTarget.HasValue)
                                        {
                                            ApplyMovePoint();
                                            return new Action(typeof(GoToPointLogic), "Mcs:Partoling");
                                        }
                                    }
                                    else
                                    {
                                        if (needHeal && isEnemyPosLost)
                                        {
                                            RefreshStuckTimer();
                                            return new Action(typeof(HealLogic), "Mcs:FightHealing6");
                                        }
                                    }
                                }
                            }
                        }
                        else if (goalEnemy.IsVisible && haveBullets.Value && !McsBotPlayerData.HasIntent(Intents.ShouldUseStationaryWeapon))
                        {
                            if (closestFriend != null && sqrDist < 0.5f * 0.5f)
                            {
                                return new Action(typeof(HoldPositionLogic), "Mcs:MonitorBlockedLine");
                            }

                            if (TryGetCombatCoverTarget(mcsLeadPlayerPos, out var flankCoverPos))
                            {
                                UpdateCommonMoveTarget(flankCoverPos, out var flankNextTime);
                                if (_currentMoveTarget.HasValue)
                                {
                                    ApplyMovePoint();
                                    return new Action(typeof(GoToPointLogic), "Mcs:FlankForClearShot");
                                }
                            }

                            return new Action(typeof(BlindFireBlockedLogic), "Mcs:BlindFireBlockedLine");
                        }
                        else
                        {
                            if (!haveBullets.Value && TryGetCombatCoverTarget(mcsLeadPlayerPos, out var reloadCoverPos))
                            {
                                UpdateCommonMoveTarget(reloadCoverPos, out var reloadNextTime);
                                if (_currentMoveTarget.HasValue)
                                {
                                    ApplyMovePoint();
                                    return new Action(typeof(GoToPointLogic), "Mcs:ReloadInCover");
                                }
                            }

                            if (ShallRushEnemy(enemyToLeadSqrDist, goalEnemy, hasTravelTask, botToEnemySqrDist))
                            {
                                TryRushJump(goalEnemy, time);
                                return new Action(typeof(RunToEnemyLogic), "Mcs:RushEnemy");
                            }

                            if (McsBotPlayerData.HasIntent(Intents.ShouldGoToPoint))
                            {
                                if (TryRefreshCommonTarget(McsBotPlayerData.TargetPos, time))
                                {
                                    ApplyMovePoint();
                                    return new Action(typeof(GoToPointLogic), "Mcs:GoToPointCommand");
                                }
                                else
                                {
                                    return new Action(typeof(HoldPositionLogic), "Mcs:GoToLootTargetPosNotFound");
                                }
                            }

                            if (McsBotPlayerData.HasIntent(Intents.ShouldHoldPosition))
                            {
                                if (needHeal && isEnemyPosLost)
                                {
                                    RefreshStuckTimer();
                                    return new Action(typeof(HealLogic), "Mcs:FightHealing4");
                                }
                                return new Action(typeof(HoldPositionLogic), "Mcs:HoldPositionCommand");
                            }

                            TryRefreshLeadTarget(mcsLeadPlayerPos, time);

                            if (needHeal && isEnemyPosLost)
                            {
                                RefreshStuckTimer();
                                ApplyMovePoint();
                                return new Action(typeof(HealLogic), "Mcs:FightHealing5");
                            }

                            if (IsBeyondFollowDistance(sqrDistance) || tooClose)
                            {
                                if (_currentMoveTarget.HasValue)
                                {
                                    ApplyMovePoint();
                                    return new Action(typeof(GoToPointLogic), tooClose ? "Mcs:TooClose" : "Mcs:TooFar");
                                }
                            }
                            else
                            {
                                if (_nextPatrolTime + 4f < time)
                                {
                                    _nextPatrolTime = time + 4f;
                                    if (_currentMoveTarget.HasValue)
                                    {
                                        ApplyMovePoint();
                                        return new Action(typeof(GoToPointLogic), "Mcs:Partoling");
                                    }
                                }
                                else
                                {
                                    if (needHeal && isEnemyPosLost)
                                    {
                                        RefreshStuckTimer();
                                        return new Action(typeof(HealLogic), "Mcs:FightHealing6");
                                    }
                                }
                            }
                        }
                    }
                }

                #endregion
                #region McsExfiltrationLayer

                if (McsBotPlayerData.LeadPlayer == null || !McsBotPlayerData.LeadPlayer.HealthController.IsAlive || McsBotPlayerData.HasIntent(Intents.ShouldExfil))
                {
                    if (BotOwner.PatrollingData.ExfiltrationData.HaveActions())
                    {
                        return new Action(typeof(GoToExfiltrationPointLogic), "Mcs:GotoExit");
                    }
                }

                #endregion
                #region McsClearAreaLayer

                ReportTaskStart(
                    ref _wasClearingArea,
                    McsBotPlayerData.HasIntent(Intents.ShouldClearArea)
                        && McsBotPlayerData.ClearAreaPoints != null
                        && McsBotPlayerData.ClearAreaPoints.Count > 0,
                    EPhraseTrigger.Going,
                    BotOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null);

                if (McsBotPlayerData.HasIntent(Intents.ShouldClearArea) && McsBotPlayerData.ClearAreaPoints != null && McsBotPlayerData.ClearAreaPoints.Count > 0)
                {
                    if (McsBotPlayerData.ClearAreaIndex >= McsBotPlayerData.ClearAreaPoints.Count)
                    {
                        FinishClearArea();
                        return new Action(typeof(HoldPositionLogic), "Mcs:ClearAreaDone");
                    }

                    var targetPos = McsBotPlayerData.ClearAreaPoints[McsBotPlayerData.ClearAreaIndex];
                    McsBotPlayerData.TargetPos = targetPos;

                    var arrived = BotOwner.Position.McsSqrDistance(targetPos) <= ARRIVE_DIST * ARRIVE_DIST;
                    var stuck = BotOwner.Mover._lastTimePosChanged + 8f < time;

                    if (arrived || stuck)
                    {
                        if (arrived && LOOK_AROUND_TIME > 0f)
                        {
                            if (McsBotPlayerData.ClearAreaLookAroundUntil <= 0f && MyExtensions.IsTrue100(30f))
                            {
                                _isTurnRight = MyExtensions.RandomSing();
                                McsBotPlayerData.ClearAreaLookAroundUntil = time + LOOK_AROUND_TIME;
                                BotOwner.StopMove();
                            }

                            if (time < McsBotPlayerData.ClearAreaLookAroundUntil)
                            {
                                var yaw = time * 90f % 360f * _isTurnRight;
                                var dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                                BotOwner.Steering.LookToDirection(dir, 120f);
                                return new Action(typeof(HoldPositionLogic), "Mcs:ClearAreaLookAround");
                            }
                        }

                        McsBotPlayerData.ClearAreaLookAroundUntil = 0f;
                        McsBotPlayerData.ClearAreaIndex++;
                        BotOwner.Mover._lastTimePosChanged = time;

                        if (McsBotPlayerData.ClearAreaIndex >= McsBotPlayerData.ClearAreaPoints.Count)
                        {
                            FinishClearArea();
                            return new Action(typeof(HoldPositionLogic), "Mcs:ClearAreaDone");
                        }

                        targetPos = McsBotPlayerData.ClearAreaPoints[McsBotPlayerData.ClearAreaIndex];
                        McsBotPlayerData.TargetPos = targetPos;
                    }

                    if (TryRefreshCommonTarget(McsBotPlayerData.TargetPos, time))
                    {
                        ApplyMovePoint();
                        return new Action(typeof(GoToPointLogic), "Mcs:ClearAreaGoToPoint");
                    }
                }

                #endregion
                #region McsCommonLayer

                if (McsBotPlayerData.HasIntent(Intents.ShouldDropTargetLoot) && BotOwner.ExternalItemsController.HaveItemsToDrop())
                {
                    if (TryRefreshLeadTarget(mcsLeadPlayerPos, time))
                    {
                        ApplyMovePoint();
                        return new Action(typeof(DropTargetLootLogic), "Mcs:DropTargetLootCommand");
                    }
                }

                if (McsBotPlayerData.HasIntent(Intents.ShouldGoToPoint))
                {
                    if (TryRefreshCommonTarget(McsBotPlayerData.TargetPos, time))
                    {
                        ApplyMovePoint();
                        return new Action(typeof(GoToPointLogic), "Mcs:GoToPointCommand");
                    }
                }

                if (McsBotPlayerData.HasIntent(Intents.ShouldHoldPosition))
                {
                    if (needHeal)
                    {
                        RefreshStuckTimer();
                        return new Action(typeof(HealLogic), "Mcs:CommonHealing1");
                    }

                    return new Action(typeof(HoldPositionLogic), "Mcs:HoldPositionCommand");
                }

                // Fork: upstream injected any stim every 30 s, even out of combat; now only with the
                // "Stims outside combat" setting (C2Sain/ForkConfig.cs). SAIN still uses stims in a fight.
                if (C2Sain.ForkConfig.OutOfCombatStims.Value && BotOwner.Medecine.Stimulators.HaveSmt && Time.time > _nextStimCheckTime)
                {
                    _nextStimCheckTime = Time.time + 30f;
                    return new Action(typeof(HealStimulatorsLogic), "Mcs:UseStim");
                }

                if (!CheckFirearmsAnimatorState())
                {
                    BotOwner.TryResetHandsState();
                }

                CheckWeaponSwitch();

                if (!BotOwner.WeaponManager.Reload.Reloading)
                {
                    var haveBullets = BotOwner.WeaponManager.HaveBullets;
                    if (!haveBullets || BotOwner.McsGetCurrentMagAmmoRatio() <= 0.3f)
                    {
                        BotOwner.WeaponManager.Reload.McsTryReload();
                    }
                }

                if (needHeal)
                {
                    TryRefreshLeadTarget(mcsLeadPlayerPos, time);
                    RefreshStuckTimer();
                    ApplyMovePoint();
                    return new Action(typeof(HealLogic), "Mcs:CommonHealing2");
                }
                else if (TryGetBtrFollowAction(time, out var btrAction))
                {
                    return btrAction;
                }
                else if (_nextLootingCheckTime < time && McsBotPlayerData.LootingTarget != null && McsBotPlayerData.McsAILeadPlayer != null && !McsBotPlayerData.HasIntent(Intents.ShouldFollowMe))
                {
                    var enableLooting = McsBotPlayerData.McsAILeadPlayer.McsBotPlayerConfig.EnableLooting;
                    var hasEmergencyLootNeed = McsBotPlayerData.HasEmergencyLootNeed();

                    if (!enableLooting && !hasEmergencyLootNeed)
                    {
                        McsBotPlayerData.IsLooting = false;
                    }
                    else
                    {
                        if (TryRefreshCommonTarget(McsBotPlayerData.LootingTarget.RootTransform.position, time))
                        {
                            ApplyMovePoint();
                            return new Action(typeof(GoToLootTargetLogic), "Mcs:GoToLootTarget");
                        }
                    }
                }

                TryRefreshLeadTarget(mcsLeadPlayerPos, time);

                if (IsBeyondFollowDistance(sqrDistance) || tooClose)
                {
                    if (_currentMoveTarget.HasValue)
                    {
                        ApplyMovePoint();
                        return new Action(typeof(GoToPointLogic), tooClose ? "Mcs:TooClose" : "Mcs:TooFar");
                    }
                }
                else
                {
                    if (_nextPatrolTime < time)
                    {
                        _nextPatrolTime = time + 8f;
                        if (_currentMoveTarget.HasValue)
                        {
                            ApplyMovePoint();
                            return new Action(typeof(GoToPointLogic), "Mcs:Partoling");
                        }
                    }
                }

                #endregion

                return new Action(typeof(HoldPositionLogic), "Mcs:Default");
            }
            catch (Exception e)
            {
                McsLogger.LogError(e);
                return new Action(typeof(HoldPositionLogic), "Mcs:Exception");
            }
        }

        public override bool IsActive()
        {
            if (!IsMcsBotPlayer)
            {
                return false;
            }

#if DEBUG
            if (!MiyakoCarryServicePlugin.EnableMcsLayer.Value)
            {
                return false;
            }
#endif

            var mcsBotPlayerData = BotOwner.GetMcsBotPlayerData();

            // Fork: decide whether C2 runs or steps aside for SAIN and the Guard layer (C2Sain/C2Gate.cs).
            var wantsC2 = (mcsBotPlayerData != null && mcsBotPlayerData.HasIntent(Intents.ShouldUseStationaryWeapon))
                || C2Sain.C2Gate.WantsC2(BotOwner, mcsBotPlayerData);
            if (mcsBotPlayerData != null)
            {
                mcsBotPlayerData.Fork.C2Active = wantsC2;
            }
            if (wantsC2)
            {
                C2Sain.SainBridge.ReleaseSain(BotOwner);
            }

            if (BotOwner.Memory.HaveEnemy)
            {
                var goalEnemy = BotOwner.Memory.GoalEnemy;
                var enemyExist = goalEnemy != null && goalEnemy.Person != null;
                if (BotOwner.Profile.Info.Settings.Role is WildSpawnType.bossZryachiy or WildSpawnType.followerZryachiy)
                {
                    if (enemyExist)
                    {
                        if (BotOwner.Boss.BossLogic is BossZryachiy bossZryachiy)
                        {
                            bossZryachiy.AddEnemy(goalEnemy.Person, EBotEnemyCause.zryachiyLogic);
                        }
                    }
                }
            }
            return wantsC2;
        }
    }
}