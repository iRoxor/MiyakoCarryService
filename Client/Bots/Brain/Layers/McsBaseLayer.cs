using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.Vaulting;
using EFT.Vehicle;
using MiyakoCarryService.Client.Bots.Brain.Logics;
using MiyakoCarryService.Client.Bots.Navigation;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Models;
using MiyakoCarryService.Client.Utils;
using UnityEngine;
using UnityEngine.AI;

namespace MiyakoCarryService.Client.Bots.Brain.Layers
{
    public abstract class McsBaseLayer : CustomLayer
    {
        public McsBaseLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
            InitActionMap();
        }

        protected bool? _isMcsBotPlayer = null;
        public bool IsMcsBotPlayer => _isMcsBotPlayer ??= BotOwner.IsMcsBotPlayer;
        protected ConcurrentDictionary<Type, Func<bool>> _endActionMap;
        protected bool _haveCoverToShoot = false;
        protected float _nextHoldPositionTime = 0f;
        protected float _goToCoverTime = 0f;
        protected CustomNavigationPoint _currentNavigationPoint = null;
        protected float _nextPatrolTime = 0f;
        protected float _nextShootTime = 0f;
        protected float _nextWeaponSwitchTime = 0f;
        protected float _nextMeleeCheckTime = 0f;
        protected float _nextLootingCheckTime = 0f;
        protected float _nextVaultCheckTime = 0f;
        protected float _nextJumpCheckTime = float.MaxValue;
        protected float _nextUpdatePosTime = 0f;
        protected float _nextHealCheckTime = 0f;
        protected float _nextStimCheckTime = 0f;
        protected float _nextDeactivateCheckTime = 0f;
        protected float _nextFastOpenDoorCheckTime = 0f;
        protected Vector3? _currentMoveTarget = null;
        protected readonly NavBridge NavBridge = new NavBridge();
        protected Vector3? _lastTargetPos = Vector3.zero;
        protected Vector3[] _lastCalcCorners = null;
        protected bool _lastCanRunResult = false;
        protected int _currentMoveRetries = 0;
        protected int _currentHealTimes = 0;
        protected int _currentLootingRetries = 0;
        protected int _currentDeactivateRetries = 0;
        protected float _currentHealTimeout = 10f;
        protected float _nextAnimatorFixTime = 0f;
        protected string _cachedProxyTargetId;
        protected StationaryWeaponData _cachedStationaryWeaponData;
        protected float _scanPhase = 0f;
        protected float _nextRecalcGoalTime = 0f;
        protected float _goToStationaryStuckTime = -999f;
        protected float _lastSqrToOperator = float.MaxValue;
        protected float _lastCanShootTime = -999f;
        protected int _isTurnRight = 1;
        protected string _holdGroundEnemyId = null;
        protected float _holdGroundStartTime = 0f;
        protected float _holdGroundDuration = 0f;
        protected float _lastEnemyVisibleTime = -999f;
        protected float _coverEnterTime = 0f;
        protected string _lastAcceptedGoalEnemyId = null;
        protected float _lastAcceptedGoalSetTime = -999f;
        protected float _nextRushJumpTime = 0f;
        protected bool _wasProxying = false;
        protected bool _wasEscorting = false;
        protected bool _wasClearingArea = false;
        private string _lastReportedContactEnemyId = null;
        private float _lastContactReportTime = -999f;
        private bool _wasFightActive = false;
        protected const float ARRIVE_DIST = 2.5f;
        protected const float LOOK_AROUND_TIME = 2f;
        protected const float LEAD_POSITION_CHANGE_THRESHOLD = 2f;
        protected const float TOO_FAR_FROM_LEAD_DISTANCE = 20f;
        protected const float TOO_CLOSE_FROM_LEAD_DISTANCE = 2f;
        /// <summary>Fork: keep a follow spot until the player has moved this far (m) from where it was picked.</summary>
        protected const float FOLLOW_SPOT_KEEP_DISTANCE = 3f;
        protected const float VAULT_PROBE_BACKOFF = 0.75f;
        protected const float ENTER_COMMON_LOOTING_COODDOWN = 10f;
        protected const float WEAPON_SWITCH_COOLDOWN = 1f;
        protected const float SUPPRESS_TIME_SINCE_SEEN = 3f;
        protected const float RUSH_NEAR_ENEMY_SQUARE_DIST = 15f * 15f;
        protected const float SHIFT_COVER_MIN_TIME = 6f;
        protected const float FAST_OPEN_DOOR_TRIGGER_DIST = 4.5f;
        protected const float FAST_OPEN_DOOR_WINDOW = 2.5f;

        public McsBotPlayerData McsBotPlayerData
        {
            get
            {
                return field ??= BotOwner.GetMcsBotPlayerData();
            }
        }

        public string Name
        {
            get
            {
                return field ??= GetType().Name;
            }
        }

        public override string GetName()
        {
            return Name;
        }

        protected SubtitlesMgr SubtitlesMgr => field ??= MgrAccessor.Get<SubtitlesMgr>();

        public override bool IsCurrentActionEnding()
        {
            UpdateFastOpenDoor();

            if (CurrentAction == null)
            {
                return true;
            }

            return _endActionMap.TryGetValue(CurrentAction.Type, out var endFunc) ? endFunc() : true;
        }

        public void RegisterAction(Type logicType, Func<bool> func)
        {
            if (_endActionMap == null)
            {
                _endActionMap = new();
            }

            _endActionMap.AddOrUpdate(logicType, _logicType => func,
                (_logicType, oldFunc) =>
                {
                    oldFunc = func;
                    return oldFunc;
                }
            );
        }

        public virtual void InitActionMap()
        {
            RegisterAction(typeof(GoToCoverPointLogic), EndGoToCoverPoint);
            RegisterAction(typeof(HealLogic), EndHeal);
            RegisterAction(typeof(StationaryHealLogic), EndHeal);
            RegisterAction(typeof(RunToCoverLogic), EndRunToCover);
            RegisterAction(typeof(SimplePatrolLogic), EndSimplePatrol);
            RegisterAction(typeof(HoldPositionLogic), EndHoldPosition);
            RegisterAction(typeof(GoToPointLogic), EndGoToPoint);
            RegisterAction(typeof(GoToProtectLogic), EndGoToProtect);
            RegisterAction(typeof(GoToEnemyLogic), EndGoToEnemy);
            RegisterAction(typeof(AttackMovingLogic), EndAttackMoving);
            RegisterAction(typeof(GoToLootTargetLogic), EndGoToLootTarget);
            RegisterAction(typeof(ShootFromPlaceLogic), EndShootFromPlace);
            RegisterAction(typeof(ShootFromCoverLogic), EndShootFromCover);
            RegisterAction(typeof(ShootToSmokeLogic), EndShootToSmoke);
            RegisterAction(typeof(ShootFromStationaryLogic), EndShootFromStationary);
            RegisterAction(typeof(RunToEnemyLogic), EndRunToEnemy);
            RegisterAction(typeof(GoToExfiltrationPointLogic), EndGoToExfiltrationPoint);
            RegisterAction(typeof(MeleeAttackLogic), EndMeleeAttack);
            RegisterAction(typeof(RunToPointLogic), EndGoToPoint);
            RegisterAction(typeof(EscortToPointByWayLogic), EndEscortToPointByWay);
            RegisterAction(typeof(FlashedLogic), EndFlashed);
            RegisterAction(typeof(DeactivateMineLogic), EndDeactivateMine);
            RegisterAction(typeof(RunAwayGrenadeLogic), EndRunAwayGrenade);
            RegisterAction(typeof(RunAwayArtilleryLogic), EndRunAwayArtillery);
            RegisterAction(typeof(RunAwayBTRLogic), EndRunAwayBTR);
            RegisterAction(typeof(GoToExcuteProxyActionLogic), EndGoToExcuteProxyAction);
            RegisterAction(typeof(DropTargetLootLogic), EndDropTargetLootLogic);
            RegisterAction(typeof(HealStimulatorsLogic), EndHealStimulators);
            RegisterAction(typeof(GoToBtrLogic), EndGoToBtr);
            RegisterAction(typeof(DogFightLogic), EndDogFight);
            RegisterAction(typeof(SuppressFireLogic), EndSuppressFire);
            RegisterAction(typeof(BlindFireBlockedLogic), EndBlindFireBlocked);
            RegisterAction(typeof(CoverPeakLogic), EndCoverPeak);
            RegisterAction(typeof(StandAndShootLogic), EndShootFromPlace);
        }

        protected bool IsApproachingThreat()
        {
            var time = Time.time;

            var mcsBotPlayerData = McsBotPlayerData;
            if (mcsBotPlayerData != null)
            {
                var lastHitShooter = mcsBotPlayerData.LastHitShooter;
                if (lastHitShooter != null
                    && lastHitShooter.HealthController != null
                    && lastHitShooter.HealthController.IsAlive
                    && time - mcsBotPlayerData.LastHitTime <= 2f)
                {
                    return true;
                }
            }

            var goalEnemy = BotOwner.Memory.GoalEnemy;

            if (goalEnemy != null && goalEnemy.Person != null && goalEnemy.Person.HealthController.IsAlive)
            {
                var enemyDistance = goalEnemy.Distance;
                if (enemyDistance < 10f)
                {
                    return true;
                }

                if (enemyDistance < 30f)
                {
                    if (goalEnemy.IsVisible)
                    {
                        return true;
                    }

                    BotOwner.EnemyLookData.DoCheck();
                    if (BotOwner.EnemyLookData.IsEnemyLookAtMeForPeriod(2f))
                    {
                        return true;
                    }
                }
            }

            if (mcsBotPlayerData?.McsAILeadPlayer?.GetLeadThreatEnemy() != null)
            {
                return true;
            }

            return false;
        }

        public virtual bool EndHeal()
        {
            if (!BotOwner.Medecine.Using)
            {
                return true;
            }

            if (IsApproachingThreat() || BaseLogicLayer.CheckMedsToStop(BotOwner))
            {
                BotOwner.Medecine.FirstAid.CancelCurrent();
                _currentHealTimes = 0;
                return true;
            }

            if (Time.time > _nextHealCheckTime)
            {
                if (GetHealTimeout(out var timeout))
                {
                    _currentHealTimeout = timeout;
                }
                _nextHealCheckTime = Time.time + 1f;
                _currentHealTimes += 1;
            }

            if (_currentHealTimes >= _currentHealTimeout)
            {
                BotOwner.WeaponManager.CheckWeaponReady();
                if (!CheckFirearmsAnimatorState())
                {
                    BotOwner.WeaponManager.CheckWeaponReady();
                }
                BotOwner.TryResetHandsState();
                _currentHealTimes = 0;
                return true;
            }

            return false;
        }

        public virtual bool GetHealTimeout(out float timeout)
        {
            timeout = 0f;
            if (BotOwner.Medecine.Stimulators.Using)
            {
                timeout = 3f;
                return true;
            }
            if (BotOwner.Medecine.FirstAid.Have2Do)
            {
                timeout = 10f;
                return true;
            }
            if (BotOwner.Medecine.SurgicalKit.HaveWork)
            {
                timeout = 20f;
                return true;
            }
            return false;
        }

        public virtual bool EndHealStimulators()
        {
            // 威胁中断：打兴奋剂是战术增益非生存必需，威胁逼近时中断应战
            if (IsApproachingThreat())
            {
                return true;
            }

            if (BotOwner.Medecine.Stimulators.Using)
            {
                return false;
            }
            return true;
        }

        public virtual bool EndGoToBtr()
        {
            return true;
        }

        public virtual bool EndRunToCover()
        {
            var mcsLeadPlayerPos = BotOwner.GetMcsLeadPlayerPos(McsBotPlayerData);
            if (BotOwner.Mover._lastTimePosChanged + 1f < Time.time)
            {
                CheckStuck();
            }

            if (mcsLeadPlayerPos != null)
            {
                TryFindCover(mcsLeadPlayerPos);
                UpdateCoverToShoot();
                if (!BotOwner.Memory.IsInCover && !_haveCoverToShoot)
                {
                    return true;
                }
            }

            if (BotOwner.Memory.IsInCover)
            {
                return true;
            }

            if (!BotOwner.CanSprintPlayer)
            {
                return true;
            }

            if (IsDogFighting())
            {
                return true;
            }

            if (BotOwner.Memory.CurCustomCoverPoint != null && BotOwner.Memory.CurCustomCoverPoint.IsSpotted)
            {
                return true;
            }

            return false;
        }

        public virtual void TryFindCover(Vector3 mcsLeadPlayerPos)
        {
            if (_goToCoverTime < Time.time)
            {
                _goToCoverTime = Time.time + 1f;
                var coverSearchData = new CoverSearchData(mcsLeadPlayerPos, BotOwner.CoverSearchInfo, CoverShootType.hide, TOO_FAR_FROM_LEAD_DISTANCE, 0f, CoverSearchType.closerToSelectedPoint, null, null, new Vector3?(mcsLeadPlayerPos), ECheckSHootHide.shootAndHide, new CoverSearchDefenceData(0f), PointsArrayType.byShootType, true, null, null, "Default");
                _currentNavigationPoint = BotOwner.BotsGroup.CoverPointMaster.GetCoverPointMain(coverSearchData, true);
                if (_currentNavigationPoint != null)
                {
                    if (mcsLeadPlayerPos.McsSqrDistance(_currentNavigationPoint.Position) < TOO_FAR_FROM_LEAD_DISTANCE * TOO_FAR_FROM_LEAD_DISTANCE && !_currentNavigationPoint.IsSpotted)
                    {
                        BotOwner.Memory.IsInCover = true;
                        return;
                    }
                }
                BotOwner.Memory.IsInCover = false;
            }
        }

        public virtual void UpdateCoverToShoot()
        {
            if (McsBotPlayerData?.LeadPlayer == null)
            {
                return;
            }

            if (_nextHoldPositionTime < Time.time)
            {
                _nextHoldPositionTime = Time.time + 1f;
                Vector3 leadPos;

                if (McsBotPlayerData.LeadPlayer.HealthController == null)
                {
                    return;
                }

                if (McsBotPlayerData.LeadPlayer.HealthController.IsAlive)
                {
                    leadPos = McsBotPlayerData.LeadPlayer.Position;
                }
                else if (BotOwner.BotFollower.HaveBoss)
                {
                    leadPos = BotOwner.BotFollower.BossToFollow.Position;
                }
                else
                {
                    leadPos = BotOwner.Position;
                }
                _currentNavigationPoint = FollowerCheckData();
                if (_currentNavigationPoint != null && _currentNavigationPoint.IsFreeById(BotOwner.Id) && !_currentNavigationPoint.IsSpotted)
                {
                    var sqrMagnitude = leadPos.McsSqrDistance(_currentNavigationPoint.Position);
                    if (sqrMagnitude >= 75f * 75f)
                    {
                        _haveCoverToShoot = false;
                        return;
                    }
                    if (ProtectCareKill())
                    {
                        _haveCoverToShoot = _currentNavigationPoint.CanIShootToEnemy;
                    }
                    else
                    {
                        _haveCoverToShoot = true;
                    }
                    if (_haveCoverToShoot && (BotOwner.Memory.CurCustomCoverPoint == null || BotOwner.Memory.CurCustomCoverPoint.Id != _currentNavigationPoint.Id))
                    {
                        BotOwner.Memory.BotCurrentCoverInfo.Spotted();
                        BotOwner.Memory.BotCurrentCoverInfo.SetCover(_currentNavigationPoint, true);
                        return;
                    }
                }
                else
                {
                    _haveCoverToShoot = false;
                }
            }
        }

        public virtual bool WasHitRecently(float timeframe)
        {
            return (Time.time - BotOwner.Memory.LastTimeHit) < timeframe;
        }

        public virtual bool EndEatDrink()
        {
            return true;
        }

        public virtual bool EndGoToCoverPoint()
        {
            if (McsBotPlayerData == null)
            {
                return true;
            }

            var mcsLeadPlayerPos = BotOwner.GetMcsLeadPlayerPos(McsBotPlayerData);
            if (BotOwner.Mover._lastTimePosChanged + 1f < Time.time)
            {
                CheckStuck();
            }

            if (mcsLeadPlayerPos != null)
            {
                TryFindCover(mcsLeadPlayerPos);
                UpdateCoverToShoot();
                if (!BotOwner.Memory.IsInCover && !_haveCoverToShoot)
                {
                    return true;
                }
            }

            if (BotOwner.Memory.IsInCover)
            {
                return true;
            }

            var goalEnemy = BotOwner.Memory.GoalEnemy;
            if (goalEnemy != null && goalEnemy.IsVisible && goalEnemy.CanShoot)
            {
                return true;
            }

            return false;
        }

        public virtual bool EndSimplePatrol()
        {
            if (McsBotPlayerData == null)
            {
                return true;
            }

            if (ShouldEndPatrol())
            {
                return true;
            }

            if (BotOwner.PatrollingData.Way.PatrolType == PatrolType.reserved)
            {
                return true;
            }

            if (McsBotPlayerData.LeadPlayer != null && McsBotPlayerData.LeadPlayer.HealthController.IsAlive)
            {
                return true;
            }

            if (BotOwner.BotFollower.HaveBoss && !BotOwner.Boss.IamBoss)
            {
                return true;
            }

            return false;
        }

        public virtual bool EndGoToPoint()
        {
            if (IsApproachingThreat())
            {
                return true;
            }

            if (McsBotPlayerData == null)
            {
                return true;
            }

            if (BotOwner.GoToSomePointData.IsCome())
            {
                if (McsBotPlayerData.HasIntent(Intents.ShouldGoToPoint) && BotOwner.Position.McsSqrDistance(McsBotPlayerData.TargetPos.Value) <= 2f * 2f)
                {
                    McsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldHoldPosition);
                    BotOwner.TalkMsg(new McsMsg
                    {
                        PhraseTrigger = EPhraseTrigger.OnPosition
                    });
                }
                return true;
            }
            else
            {
                var mcsLeadPlayerPos = BotOwner.GetMcsLeadPlayerPos(McsBotPlayerData);
                if (BotOwner.Mover._lastTimePosChanged + 1f < Time.time)
                {
                    CheckStuck();
                }

                if (Time.time - BotOwner.Mover._lastTimePosChanged > 30f && BotOwner.Position.McsSqrDistance(mcsLeadPlayerPos) >= TOO_FAR_FROM_LEAD_DISTANCE * TOO_FAR_FROM_LEAD_DISTANCE)
                {
                    BotOwner.StopMove();
                    BotOwner.Mover.AllowTeleport();
                    BotOwner.GetPlayer.Teleport(McsBotPlayerData.LeadPlayer.Position, true);
                    var playerPosition = McsBotPlayerData.Player.Position;
                    BotOwner.Mover._lastGoodCastPoint = BotOwner.Mover._prevSuccessLinkedFrom = BotOwner.Mover._prevLinkPos = BotOwner.Mover.PositionOnWayInner = playerPosition;
                    BotOwner.Mover._lastGoodCastPointTime = Time.time;
                    BotOwner.Mover._prevPosLinkedTime = 0f;
                    BotOwner.Mover.SetPlayerToNavMesh(playerPosition);
                    BotOwner.Mover.RecalcWay();
                    BotOwner.Mover.Pause = true;
                    UpdateLeadNearMoveTarget(mcsLeadPlayerPos, out float nextTime);
                    if (_currentMoveTarget.HasValue)
                    {
                        BotOwner.GoToSomePointData.SetPoint(_currentMoveTarget.Value);
                    }
                    return true;
                }

                if (Time.time - BotOwner.Mover._lastTimePosChanged > 6f)
                {
                    if (McsBotPlayerData.HasIntent(Intents.ShouldGoToPoint) && BotOwner.Position.McsSqrDistance(McsBotPlayerData.TargetPos.Value) <= 2f * 2f)
                    {
                        McsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldHoldPosition);
                        BotOwner.TalkMsg(new McsMsg
                        {
                            PhraseTrigger = EPhraseTrigger.OnPosition
                        });
                    }
                    return true;
                }
                return false;
            }
        }

        public async Task DelaySetIntents(float delaySeconds, string[] exclude = null, params string[] intents)
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            if (McsBotPlayerData != null)
            {
                McsBotPlayerData.SetIntent(exclude, intents);
            }
        }

        public virtual bool EndEscortToPointByWay()
        {
            if (IsApproachingThreat())
            {
                return true;
            }

            if (McsBotPlayerData == null)
            {
                return true;
            }

            var hasEscortToBtr = McsBotPlayerData.HasIntent(Intents.ShouldEscortToBtr);
            var hasEscort = McsBotPlayerData.HasIntent(Intents.ShouldEscort);

            if (!hasEscort && !hasEscortToBtr)
            {
                return true;
            }

            var btrController = Singleton<GameWorld>.Instance.BtrController;
            if ((hasEscort && !McsBotPlayerData.TargetPos.HasValue) || (hasEscortToBtr && !btrController.Initiated()))
            {
                return true;
            }

            Vector3? targetPos;
            if (hasEscort)
            {
                targetPos = McsBotPlayerData.TargetPos;
            }
            else
            {
                var side = btrController.BtrView.GetBtrSide(1);
                if (side == null)
                {
                    return true;
                }
                targetPos = side.GoInPoints().Item1;
            }

            var sqrDistance = targetPos.Value.McsSqrDistance(BotOwner.Position);
            if (sqrDistance < 2f * 2f)
            {
                McsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldHoldPosition);
                BotOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.OnPosition
                });
                TasksExtensions.HandleExceptions(DelaySetIntents(3f, [Intents.ShouldFollowMe, Intents.ShouldGoToPoint, Intents.ShouldEscort, Intents.ShouldEscortToBtr, Intents.ShouldKeepFormation]));
                return true;
            }
            else if (BotOwner.GoToSomePointData.IsCome())
            {
                return true;
            }
            else
            {
                var mcsLeadPlayerPos = BotOwner.GetMcsLeadPlayerPos(McsBotPlayerData);
                if (BotOwner.Mover._lastTimePosChanged + 1f < Time.time)
                {
                    CheckStuck();
                }

                if (Time.time - BotOwner.Mover._lastTimePosChanged > 30f && BotOwner.Position.McsSqrDistance(mcsLeadPlayerPos) >= TOO_FAR_FROM_LEAD_DISTANCE * TOO_FAR_FROM_LEAD_DISTANCE)
                {
                    BotOwner.StopMove();
                    BotOwner.Mover.AllowTeleport();
                    BotOwner.GetPlayer.Teleport(McsBotPlayerData.LeadPlayer.Position, true);
                    var playerPosition = McsBotPlayerData.Player.Position;
                    BotOwner.Mover._lastGoodCastPoint = BotOwner.Mover._prevSuccessLinkedFrom = BotOwner.Mover._prevLinkPos = BotOwner.Mover.PositionOnWayInner = playerPosition;
                    BotOwner.Mover._lastGoodCastPointTime = Time.time;
                    BotOwner.Mover._prevPosLinkedTime = 0f;
                    BotOwner.Mover.SetPlayerToNavMesh(playerPosition);
                    BotOwner.Mover.RecalcWay();
                    BotOwner.Mover.Pause = true;
                    BotOwner.TalkMsg(new McsMsg
                    {
                        PhraseTrigger = EPhraseTrigger.Regroup
                    });
                    UpdateLeadNearMoveTarget(mcsLeadPlayerPos, out float nextTime);
                    if (_currentMoveTarget.HasValue)
                    {
                        BotOwner.GoToSomePointData.SetPoint(_currentMoveTarget.Value);
                    }
                    return true;
                }

                if (Time.time - BotOwner.Mover._lastTimePosChanged >= 2f)
                {
                    return true;
                }

                return false;
            }
        }

        public virtual bool EndGoToProtect()
        {
            if (IsApproachingThreat())
            {
                return true;
            }

            if (McsBotPlayerData == null)
            {
                return true;
            }

            if (BotOwner.GoToSomePointData.IsCome())
            {
                return true;
            }
            else
            {
                var mcsLeadPlayerPos = BotOwner.GetMcsLeadPlayerPos(McsBotPlayerData);
                if (BotOwner.Mover._lastTimePosChanged + 1f < Time.time)
                {
                    CheckStuck();
                }

                if (Time.time - BotOwner.Mover._lastTimePosChanged > 30f && BotOwner.Position.McsSqrDistance(mcsLeadPlayerPos) >= TOO_FAR_FROM_LEAD_DISTANCE * TOO_FAR_FROM_LEAD_DISTANCE)
                {
                    BotOwner.StopMove();
                    BotOwner.Mover.AllowTeleport();
                    BotOwner.GetPlayer.Teleport(McsBotPlayerData.LeadPlayer.Position, true);
                    var playerPosition = McsBotPlayerData.Player.Position;
                    BotOwner.Mover._lastGoodCastPoint = BotOwner.Mover._prevSuccessLinkedFrom = BotOwner.Mover._prevLinkPos = BotOwner.Mover.PositionOnWayInner = playerPosition;
                    BotOwner.Mover._lastGoodCastPointTime = Time.time;
                    BotOwner.Mover._prevPosLinkedTime = 0f;
                    BotOwner.Mover.SetPlayerToNavMesh(playerPosition);
                    BotOwner.Mover.RecalcWay();
                    BotOwner.Mover.Pause = true;
                    BotOwner.TalkMsg(new McsMsg
                    {
                        PhraseTrigger = EPhraseTrigger.Regroup
                    });
                    UpdateLeadNearMoveTarget(mcsLeadPlayerPos, out float nextTime);
                    if (_currentMoveTarget.HasValue)
                    {
                        BotOwner.GoToSomePointData.SetPoint(_currentMoveTarget.Value);
                    }
                    return true;
                }

                if (Time.time - BotOwner.Mover._lastTimePosChanged > 6f)
                {
                    return true;
                }
                return false;
            }
        }

        public virtual bool EndAttackMoving()
        {
            if (BotOwner.Mover._lastTimePosChanged + 1f < Time.time)
            {
                CheckStuck();
            }
            var haveBullets = BotOwner.WeaponManager?.HaveBullets;
            if (!haveBullets.Value)
            {
                return true;
            }

            if (Time.time - BotOwner.ShootData.LastTriggerPressd > 9f)
            {
                return true;
            }
            if (BotOwner.DogFight.DogFightState > BotDogFightStatus.none)
            {
                return true;
            }
            if (BotOwner.Memory.IsInCover)
            {
                return true;
            }
            if (BotOwner.GoToSomePointData.IsCome())
            {
                return true;
            }
            return false;
        }

        protected bool CheckStuck()
        {
            var pos = BotOwner.Position;
            if (BotOwner.Mover._lastPos.McsSqrDistance(pos) > 2f * 2f)
            {
                BotOwner.Mover._lastPos = pos;
                BotOwner.Mover._lastTimePosChanged = Time.time;
                return false;
            }
            else
            {
                TrySolveStuck();
            }
            return true;
        }

        public virtual void TrySolveStuck()
        {
            if (C2Sain.ForkConfig.McsFastDoors.Value && TryFastOpenNearestDoor())
            {
                return;
            }

            if (_nextVaultCheckTime < Time.time)
            {
                _nextVaultCheckTime = Time.time + 1.5f;
                if (C2Sain.ForkConfig.GapCrossing.Value && ShouldTryVault() && TryVault())
                {
                    _nextJumpCheckTime = float.MaxValue;
                    return;
                }

                _nextJumpCheckTime = Time.time + 1.5f;
            }

            if (_nextJumpCheckTime < Time.time)
            {
                _nextJumpCheckTime = Time.time + 3f;
                TryJump();
            }
        }

        private bool TryFastOpenNearestDoor()
        {
            var mcsBotPlayerData = McsBotPlayerData;
            if (mcsBotPlayerData == null)
            {
                return false;
            }

            var nearestDoorLink = BotOwner.NearDoorData.GetNearestDoor();
            if (nearestDoorLink == null)
            {
                return false;
            }

            var door = nearestDoorLink.Door;
            if (door == null || door.DoorState != EDoorState.Shut)
            {
                return false;
            }

            var time = Time.time;
            if (mcsBotPlayerData.FastOpenDoorCooldowns.TryGetValue(door.Id, out var cooldownTime) && time < cooldownTime)
            {
                return false;
            }

            FastOpenDoor(door, time);
            return true;
        }

        public virtual bool EndHoldPosition()
        {
            if (McsBotPlayerData == null)
            {
                return true;
            }

            UpdateCoverToShoot();
            var mcsLeadPlayerPos = BotOwner.GetMcsLeadPlayerPos(McsBotPlayerData);
            if (BotOwner.Position.McsSqrDistance(mcsLeadPlayerPos) > TOO_FAR_FROM_LEAD_DISTANCE * TOO_FAR_FROM_LEAD_DISTANCE)
            {
                return true;
            }

            if (_haveCoverToShoot && ProtectWantKill() && ProtectCareKill())
            {
                return true;
            }

            var goalEnemy = BotOwner.Memory.GoalEnemy;
            if (!BotOwner.Memory.IsInCover)
            {
                return true;
            }
            if (goalEnemy == null)
            {
                if (CanSearchEnemy())
                {
                    return true;
                }
            }
            else
            {
                if (goalEnemy.IsVisible && goalEnemy.CanShoot)
                {
                    return true;
                }
                if (goalEnemy.IsVisible && goalEnemy.Distance < 100f)
                {
                    return true;
                }
            }
            return false;
        }

        public virtual bool CanSearchEnemy()
        {
            var goalEnemy = BotOwner.Memory.GoalEnemy;
            return goalEnemy == null || !WasHitRecently(10f) && !goalEnemy.IsVisible && !goalEnemy.CanShoot && goalEnemy.CanISearch && BotOwner.Tactic.IsCurTactic(BotsGroup.BotCurrentTactic.Attack) && BotOwner.Memory.LastEnemyVisionOld(BotInternalSettingsController.Core.COVER_SECONDS_AFTER_LOSE_VISION);
        }

        public virtual bool ProtectCareKill()
        {
            // return (Time.time - GetEnemyLastSeenTime()) < 10f;
            return true;
        }

        public virtual bool ProtectWantKill()
        {
            // return (Time.time - BotOwner.BotsGroup.EnemyLastSeenTimeReal) < BotOwner.Settings.FileSettings.Mind.ATTACK_ENEMY_IF_PROTECT_DELTA_LAST_TIME_SEEN;
            return true;
        }

        public virtual float GetEnemyLastSeenTime()
        {
            if (BotOwner.Settings.FileSettings.Mind.PROTECT_TIME_REAL)
            {
                return BotOwner.BotsGroup.EnemyLastSeenTimeReal;
            }
            return BotOwner.BotsGroup.EnemyLastSeenTimeSence;
        }

        public virtual CustomNavigationPoint FollowerCheckData()
        {
            Vector3 leadPos;
            if (McsBotPlayerData?.LeadPlayer != null && McsBotPlayerData.LeadPlayer.HealthController.IsAlive)
            {
                leadPos = McsBotPlayerData.LeadPlayer.Position;
            }
            else if (BotOwner.BotFollower.HaveBoss)
            {
                leadPos = BotOwner.BotFollower.BossToFollow.Position;
            }
            else
            {
                leadPos = BotOwner.Position;
            }
            var shootPointClass = BotOwner.CurrentEnemyTargetPosition(true);
            var coverShootType = CoverShootType.shoot;
            if (shootPointClass == null)
            {
                coverShootType = CoverShootType.hide;
            }
            var coverSearchData = new CoverSearchData(leadPos, BotOwner.CoverSearchInfo, coverShootType, BotInternalSettingsController.Core.START_DIST_TO_COV, 0f, CoverSearchType.closerToSelectedPoint, shootPointClass, null, new Vector3?(leadPos), ECheckSHootHide.shootAndHide, new CoverSearchDefenceData(0f), PointsArrayType.byShootType, true);
            return BotOwner.BotsGroup.CoverPointMaster.GetCoverPointMain(coverSearchData, true);
        }

        public virtual bool ShouldEndPatrol()
        {
            if (BotOwner.PeaceLook.HaveActions())
            {
                return true;
            }

            return false;
        }

        public virtual bool IsDogFighting()
        {
            return BotOwner.DogFight.DogFightState > BotDogFightStatus.none;
        }

        public virtual bool EndGoToLootTarget()
        {
            if (IsApproachingThreat())
            {
                return true;
            }

            if (McsBotPlayerData == null)
            {
                return true;
            }

            if (BotOwner.Mover._lastTimePosChanged + 1f < Time.time)
            {
                CheckStuck();
            }

            if (Time.time > _nextLootingCheckTime && !McsBotPlayerData.IsTaskRunning && !McsBotPlayerData.IsLooting)
            {
                _currentLootingRetries += 1;
                _nextLootingCheckTime = Time.time + 1f;
                return true;
            }

            if (_currentLootingRetries >= 15)
            {
                _currentLootingRetries = 0;
                McsBotPlayerData.IsLooting = false;
                return true;
            }

            if (BotOwner.GoToSomePointData.IsCome())
            {
                return true;
            }
            return false;
        }

        public virtual bool ShouldShootImmediately()
        {
            try
            {
                var goalEnemy = BotOwner.Memory.GoalEnemy;
                var flag = ((goalEnemy != null && goalEnemy.Distance < BotOwner.Settings.FileSettings.Shoot.SHOOT_IMMEDIATELY_DIST) || BotOwner.BotsGroup.AnyBodyShootImmediately) && goalEnemy.CanShoot && Time.time - goalEnemy.AddTime < 5f;
                var isActive = BotOwner.WeaponManager.UnderbarrelLauncherController.IsActive;
                BotOwner.BotsGroup.AnyBodyShootImmediately = flag || isActive;
                return BotOwner.BotsGroup.AnyBodyShootImmediately;
            }
            catch
            {
                return false;
            }
        }

        public virtual bool IsShootFromCoverConditionAllFine()
        {
            if (!BotOwner.Memory.IsInCover)
            {
                return false;
            }
            if (!BotOwner.LookSensor.EnoughDistToShoot(out var flag))
            {
                return false;
            }
            if (!BotOwner.Memory.CurCustomCoverPoint.CanShootToTargetCast(BotOwner, BotOwner.Settings.FileSettings.Cover.DELTA_SEEN_FROM_COVE_LAST_POS))
            {
                return false;
            }
            if (BotOwner.WeaponManager.Stationary.ShallEndShootFromCurrent())
            {
                return false;
            }
            return true;
        }

        public virtual bool GetCrossPoint(EnemyInfo enemy)
        {
            var nearestDoor = BotOwner.NearDoorData.GetNearestDoor();
            if (nearestDoor == null)
            {
                return false;
            }
            var position = BotOwner.Transform.position;
            var currPosition = enemy.CurrPosition;
            var gclass = new SegmentPoints(position, currPosition);
            var vector = nearestDoor.SegmentOpen.b - nearestDoor.SegmentOpen.a;
            var vector2 = nearestDoor.SegmentOpen.a - vector * 0.1f;
            var vector3 = nearestDoor.SegmentOpen.b + vector * 0.1f;
            return AIUtility.GetCrossPoint(gclass.a, gclass.b, vector2, vector3) != null;
        }

        public virtual bool CannotSeeEnemy(EnemyInfo info)
        {
            if (info == null)
            {
                return false;
            }
            var vector = info.EnemyLastPositionReal + Vector3.up * 1.6f;
            return !Physics.Linecast(BotOwner.WeaponRoot.position, vector, out var raycastHit, LayersMaskController.HighPolyWithTerrainMask);
        }

        public virtual bool CanShootNow()
        {
            var goalEnemy = BotOwner.Memory.GoalEnemy;
            return goalEnemy != null && goalEnemy.CanShoot && goalEnemy.IsVisible;
        }

        public virtual bool ShootNow()
        {
            return BotOwner.Memory.GoalEnemy.CanShoot && BotOwner.Memory.GoalEnemy.IsVisible;
        }

        public virtual bool EndShootFromPlace()
        {
            if (!BotOwner.Memory.HaveEnemy)
            {
                return true;
            }
            if (BotOwner.DogFight.ShallStartCauseHavePlace())
            {
                return true;
            }
            if (!ShootNow())
            {
                return true;
            }
            if (WasHitRecently(5f))
            {
                return true;
            }
            if (_nextShootTime < Time.time)
            {
                _nextShootTime = Time.time + 3f;
                if (BotOwner.BotLay.CanShootPos(BotOwner.Memory.GoalEnemy, true, false))
                {
                    return true;
                }
            }
            return false;
        }

        public virtual bool EndShootFromCover()
        {
            if (!BotOwner.Memory.IsInCover)
            {
                return true;
            }

            if (!BotOwner.LookSensor.EnoughDistToShoot(out var enough))
            {
                return true;
            }
            if (!BotOwner.Memory.CurCustomCoverPoint.CanShootToTargetCast(BotOwner, BotOwner.Settings.FileSettings.Cover.DELTA_SEEN_FROM_COVE_LAST_POS))
            {
                return true;
            }
            if (BotOwner.WeaponManager.Stationary.ShallEndShootFromCurrent())
            {
                return true;
            }
            return false;
        }

        public virtual bool EndGoToEnemy()
        {
            if (BotOwner.Mover._lastTimePosChanged + 1f < Time.time)
            {
                CheckStuck();
            }
            if (BotOwner.DogFight.ShallStartCauseHavePlace())
            {
                return true;
            }
            if (IsEnemyPosLost())
            {
                return true;
            }
            var goalEnemy = BotOwner.Memory.GoalEnemy;
            if (!(BotOwner.DogFight.DogFightState > BotDogFightStatus.none) && goalEnemy != null && (!goalEnemy.IsVisible || !goalEnemy.CanShoot))
            {
                return false;
            }
            return true;
        }

        public virtual bool IsEnemyPosLost()
        {
            if (Time.time - BotOwner.Memory.LastEnemyTimeSeen > 5f)
            {
                if (Time.time - BotOwner.Memory.LastEnemyTimeSeen > 10f)
                {
                    BotOwner.Memory.GoalEnemy = null;
                }
                return true;
            }
            return false;
        }

        public virtual bool EndShootToSmoke()
        {
            if (!BotOwner.SmokeGrenade.ShallShoot())
            {
                return true;
            }
            return false;
        }

        public virtual bool EndRunToEnemy()
        {
            if (McsBotPlayerData == null)
            {
                return true;
            }

            if (McsBotPlayerData.HasIntent(Intents.ShouldFollowMe))
            {
                return true;
            }

            if (BotOwner.Mover._lastTimePosChanged + 1f < Time.time)
            {
                CheckStuck();
            }

            if (BotOwner.DogFight.ShallStartCauseHavePlace())
            {
                return true;
            }
            if (IsEnemyPosLost())
            {
                return true;
            }

            if (BotOwner.Mover.IsComeTo(BotOwner.Settings.FileSettings.Move.REACH_DIST, false, null))
            {
                return true;
            }

            var goalEnemy = BotOwner.Memory.GoalEnemy;
            if (goalEnemy != null && (!goalEnemy.IsVisible || !goalEnemy.CanShoot))
            {
                return false;
            }
            return true;
        }

        public virtual bool EndShootFromStationary()
        {
            if (IsEnemyPosLost())
            {
                if (BotOwner.Medecine.FirstAid.Have2Do)
                {
                    return true;
                }
                if (BotOwner.Medecine.SurgicalKit.HaveWork)
                {
                    return true;
                }
            }
            var curLink = BotOwner.WeaponManager.Stationary.CurLink;
            if (curLink == null)
            {
                return true;
            }
            if (!curLink.HaveAmmo())
            {
                return true;
            }
            if (!curLink.IsFree(BotOwner.Id))
            {
                return true;
            }
            if (BotOwner.Memory.HaveEnemy && !BotOwner.WeaponManager.Stationary.IsEnemyAtSector(BotOwner.WeaponManager.Stationary.CurLink))
            {
                return true;
            }
            if (!BotOwner.Memory.HaveEnemy)
            {
                ScanSector(curLink);
            }
            return false;
        }

        public virtual bool EndGoToExfiltrationPoint()
        {
            if (IsApproachingThreat())
            {
                return true;
            }

            if (McsBotPlayerData == null)
            {
                return true;
            }

            if (BotOwner.Mover._lastTimePosChanged + 1f < Time.time)
            {
                CheckStuck();
            }

            if (Time.time - BotOwner.Mover._lastTimePosChanged > 30f)
            {
                BotOwner.StopMove();
                BotOwner.Mover.AllowTeleport();
                BotOwner.GetPlayer.Teleport(BotOwner.PatrollingData.ExfiltrationData.CachedExfiltrationPoint.Position, true);
                var playerPosition = McsBotPlayerData.Player.Position;
                BotOwner.Mover._lastGoodCastPoint = BotOwner.Mover._prevSuccessLinkedFrom = BotOwner.Mover._prevLinkPos = BotOwner.Mover.PositionOnWayInner = playerPosition;
                BotOwner.Mover._lastGoodCastPointTime = Time.time;
                BotOwner.Mover._prevPosLinkedTime = 0f;
                BotOwner.Mover.SetPlayerToNavMesh(playerPosition);
                BotOwner.Mover.RecalcWay();
                BotOwner.Mover.Pause = true;
                UpdateCommonMoveTarget(BotOwner.PatrollingData.ExfiltrationData.CachedExfiltrationPoint.Position, out float nextTime);
                if (_currentMoveTarget.HasValue)
                {
                    BotOwner.GoToSomePointData.SetPoint(_currentMoveTarget.Value);
                }
                return true;
            }
            return false;
        }

        public virtual bool IsWannaLeave()
        {
            if (BotOwner.Boss.IamBoss || BotOwner.BotFollower == null || BotOwner.BotFollower.BossToFollow == null)
            {
                return BotOwner.Exfiltration.WannaLeave();
            }
            IPlayer player = BotOwner.BotFollower.BossToFollow.Player();
            if (player != null && player.AIData != null && !(player.AIData.BotOwner == null) && player.AIData.BotOwner.Exfiltration != null)
            {
                return player.AIData.BotOwner.Exfiltration.WannaLeave();
            }
            return BotOwner.Exfiltration.WannaLeave();
        }

        public virtual bool EndMeleeAttack()
        {
            var weaponManager = BotOwner.WeaponManager;
            if (weaponManager == null)
            {
                return true;
            }

            var meleeData = weaponManager.Melee;
            if (meleeData == null)
            {
                return true;
            }

            var goalEnemy = BotOwner.Memory.GoalEnemy;

            if (goalEnemy == null)
            {
                return true;
            }

            if (meleeData.ShallEndRun)
            {
                return true;
            }

            if (weaponManager.HaveBullets)
            {
                return true;
            }

            if ((Time.time - goalEnemy.PersonalLastSeenTime) > 5f)
            {
                return true;
            }

            if (IsEnemyPosLost())
            {
                return true;
            }

            if (weaponManager.Reload?.Reloading == true)
            {
                return true;
            }

            if (!weaponManager.IsMelee)
            {
                return true;
            }

            return false;
        }

        public virtual EquipmentSlot CheckWeaponSwitch(bool forceRefresh = false)
        {
            var weaponManager = BotOwner.WeaponManager;
            if (weaponManager == null || weaponManager.Selector == null)
            {
                return EquipmentSlot.FirstPrimaryWeapon;
            }

            var currentSlot = weaponManager.Selector.EquipmentSlot;
            if (_nextWeaponSwitchTime > Time.time)
            {
                return currentSlot;
            }

            weaponManager.Selector.UpdateWeaponsList();
            var targetSlot = BotOwner.DetermineWeaponSlotByAmmo(currentSlot, out var total);
            if (targetSlot != currentSlot)
            {
                BotOwner.TryChangeWeaponSlot(targetSlot);
                _nextWeaponSwitchTime = Time.time + WEAPON_SWITCH_COOLDOWN;
            }
            else if (forceRefresh)
            {
                BotOwner.TryChangeWeaponSlot(currentSlot);
                _nextWeaponSwitchTime = Time.time + WEAPON_SWITCH_COOLDOWN;
            }

            return targetSlot;
        }

        public virtual bool ShouldUseMeleeAttack()
        {
            if (_nextMeleeCheckTime > Time.time)
            {
                return false;
            }

            var weaponManager = BotOwner.WeaponManager;
            if (weaponManager == null)
            {
                return false;
            }

            var targetSlot = CheckWeaponSwitch();
#if DEBUG
            // McsLogger.LogWarning($"目标武器类型: {targetSlot}");
#endif
            _nextMeleeCheckTime = Time.time + 0.5f;

            if (targetSlot == EquipmentSlot.Scabbard && weaponManager.IsMelee)
            {
                return true;
            }

            if (targetSlot == EquipmentSlot.Scabbard && !weaponManager.Selector.CanChangeToMeleeWeapons)
            {
                return false;
            }

            if (targetSlot == EquipmentSlot.Scabbard && !weaponManager.HaveBullets)
            {
                return true;
            }
            return false;
        }

        public virtual bool ShouldTryVault()
        {
            if (BotOwner.GetPlayer == null || BotOwner.GetPlayer.VaultingComponent == null || BotOwner.GetPlayer.VaultingGameplayRestrictions == null)
            {
                return false;
            }

            if (!BotOwner.Mover.HasPathAndNoComplete)
            {
                return false;
            }

            if (!BotOwner.GetPlayer.VaultingGameplayRestrictions.CanVaulting())
            {
                return false;
            }

            return true;
        }

        public virtual bool TryVault()
        {
            if (!CheckForVaultableObstacle(out var obstaclePoint))
            {
                return false;
            }

            if (BotOwner.GetPlayer.MovementContext.IsSprintEnabled)
            {
                BotOwner.Mover.Sprint(false);
            }

            VaultAim.FaceYaw(BotOwner, obstaclePoint - BotOwner.Position);

            var vaultingComponent = BotOwner.GetPlayer.VaultingComponent as VaultingComponent;
            vaultingComponent?.Tick();

            if (!VaultAim.TryAlign(BotOwner, vaultingComponent, obstaclePoint - BotOwner.Position))
            {
                return false;
            }

            var tryResult = false;
            NavGapExecutor.McsVaultScope = true;
            try
            {
                tryResult = BotOwner.GetPlayer.VaultingComponent.TryVaulting();
            }
            finally
            {
                NavGapExecutor.McsVaultScope = false;
            }

            if (tryResult)
            {
                BotOwner.GetPlayer.OnVaulting();
                return true;
            }

            var toObstacle = obstaclePoint - BotOwner.Position;
            toObstacle.y = 0f;
            if (toObstacle.magnitude <= 0.8f)
            {
                NavGapExecutor.BlacklistVault(obstaclePoint, false);
            }

            return false;
        }

        public virtual void TryJump()
        {
            var movementContext = BotOwner.GetPlayer?.MovementContext;
            if (movementContext == null)
            {
                return;
            }

            movementContext.TryJump();
        }

        public virtual bool CheckForVaultableObstacle(out Vector3 obstaclePoint)
        {
            if (PlayerVaultHints.TryFind(BotOwner.Position, BotOwner.Mover.NormDirCurPoint, out var hint)
                && ProbeVaultableObstacle(hint.Forward, out obstaclePoint))
            {
                return true;
            }

            if (ProbeVaultableObstacle(BotOwner.GetPlayer.MovementContext.PlayerRealForward, out obstaclePoint))
            {
                return true;
            }

            if (ProbeVaultableObstacle(BotOwner.Mover.NormDirCurPoint, out obstaclePoint))
            {
                return true;
            }

            if (_currentMoveTarget.HasValue && ProbeVaultableObstacle(_currentMoveTarget.Value - BotOwner.Position, out obstaclePoint))
            {
                return true;
            }

            obstaclePoint = default;
            return false;
        }

        private bool ProbeVaultableObstacle(Vector3 axis, out Vector3 obstaclePoint)
        {
            obstaclePoint = default;
            axis.y = 0f;
            if (axis.sqrMagnitude < 0.01f)
            {
                return false;
            }
            axis.Normalize();

            var position = BotOwner.Position;
            var found = false;
            var nearest = float.MaxValue;

            for (var height = 0.5f; height <= 1.4f; height += 0.3f)
            {
                var origin = position + Vector3.up * height - axis * VAULT_PROBE_BACKOFF;
                if (!Physics.SphereCast(origin, 0.1f, axis, out var hit, VAULT_PROBE_BACKOFF + 1.5f, LayersMaskController.PlayerStaticCollisionsMask)
                    || hit.collider == null)
                {
                    continue;
                }

                if (hit.distance < VAULT_PROBE_BACKOFF)
                {
                    continue;
                }

                if (hit.normal.y > 0.7f)
                {
                    continue;
                }

                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    obstaclePoint = hit.point;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// Sets the bot's follow move target near the player: a formation slot, a step back when too close,
        /// a follow spot in the player's space, or a path toward the player; since fork v10, failing all of
        /// those, the closest spot the bot can reach (C2Nav.TryClosestReachable). Writes _currentMoveTarget
        /// and _lastTargetPos; <paramref name="nextUpdateTime"/> is the delay before the next refresh.
        /// </summary>
        public virtual void UpdateLeadNearMoveTarget(Vector3? leadPos, out float nextUpdateTime)
        {
            if (!leadPos.HasValue)
            {
                nextUpdateTime = 1f;
                return;
            }

            if (TryUpdateFormationMoveTarget(leadPos.Value, out nextUpdateTime))
            {
                return;
            }

            var sqrDistanceToLead = BotOwner.Position.McsSqrDistance(leadPos.Value);
            if (sqrDistanceToLead <= TOO_CLOSE_FROM_LEAD_DISTANCE * TOO_CLOSE_FROM_LEAD_DISTANCE)
            {
                var awayDir = BotOwner.Position - leadPos.Value;
                awayDir.y = 0f;
                if (awayDir.sqrMagnitude < 0.0001f)
                {
                    awayDir = BotOwner.Transform.forward;
                    awayDir.y = 0f;
                }
                awayDir.Normalize();

                var retreatTarget = leadPos.Value + awayDir * (TOO_CLOSE_FROM_LEAD_DISTANCE + 1f);
                if (Tools.BetterDestination(3f, retreatTarget, out var betterDestination))
                {
                    retreatTarget = betterDestination;
                }

                _lastTargetPos = leadPos;
                _currentMoveTarget = retreatTarget;
                nextUpdateTime = 0.2f;
                return;
            }

            // Fork: keep the current spot until the player has really moved. Upstream never updated
            // _lastTargetPos here, so the spot was re-rolled about once a second.
            if (_currentMoveTarget.HasValue && _lastTargetPos.HasValue
                && _lastTargetPos.Value.McsSqrDistance(leadPos.Value) < FOLLOW_SPOT_KEEP_DISTANCE * FOLLOW_SPOT_KEEP_DISTANCE)
            {
                nextUpdateTime = 1f;
                return;
            }

            // Fork: only spots in the player's own space (same floor, straight navmesh line from the player),
            // so bots stop pathing into the next room or onto another floor to stand "near" the player.
            Vector3[] corners;
            if (!C2Sain.C2Nav.TryPickFollowSpot(leadPos.Value, BotOwner.Position, out _, out corners))
            {
                if (!CanGetPathToRun(BotOwner.Position, leadPos.Value, McsBotPlayerData, out corners) || corners == null)
                {
                    // Fork v10: with no path to the player (off the navmesh, or on a piece of it the bot
                    // cannot reach), walk to the closest spot the bot can reach instead of holding where it
                    // stands. Upstream left the bot without a move target, so it held position for good.
                    if (C2Sain.C2Nav.TryClosestReachable(BotOwner.Position, leadPos.Value, out corners, out var reachEnd, out var why))
                    {
                        LogReach(leadPos.Value, reachEnd, null);
                        _lastTargetPos = leadPos;
                        _currentMoveTarget = GetPointAlongPathAtDistance(corners, 15f);
                        nextUpdateTime = 1f;
                        return;
                    }
                    LogReach(leadPos.Value, reachEnd, why);
                    nextUpdateTime = why == C2Sain.C2Nav.REACH_NO_CLOSER ? 1f : 0.25f;
                    return;
                }
            }

            ClearReachLog();
            _lastTargetPos = leadPos;
            _currentMoveTarget = GetPointAlongPathAtDistance(corners, 15f);
            nextUpdateTime = 1f;
        }

        /// <summary>
        /// Fork v10: with "Debug: diagnostic log" on, logs a bot's first fallback move toward the closest
        /// spot it can reach, and the first time it can get no closer (or finds nothing), once each until
        /// a normal follow path works again (ClearReachLog). Reads and writes the bot's fork state flags.
        /// </summary>
        /// <param name="leadPos">The player's position.</param>
        /// <param name="end">The fallback spot (when <paramref name="why"/> is null) or the closest reachable spot found.</param>
        /// <param name="why">Null for a move; otherwise why the bot stays (C2Nav.TryClosestReachable).</param>
        private void LogReach(Vector3 leadPos, Vector3 end, string why)
        {
            var fork = McsBotPlayerData?.Fork;
            if (fork == null || C2Sain.ForkConfig.DebugLog?.Value != true)
            {
                return;
            }
            var name = McsBotPlayerData.Player?.Profile?.Nickname;
            var toPlayer = Vector3.Distance(BotOwner.Position, leadPos);
            if (why == null)
            {
                if (!fork.ReachMoveLogged)
                {
                    fork.ReachMoveLogged = true;
                    fork.ReachStayLogged = false;
                    McsLogger.LogInfo($"[{C2Sain.ForkLog.Tag}] reach: {name} has no path to you ({toPlayer:0} m away); "
                        + $"walking to the closest spot it can reach, {Vector3.Distance(end, leadPos):0} m from you");
                }
                return;
            }
            if (!fork.ReachStayLogged)
            {
                fork.ReachStayLogged = true;
                McsLogger.LogInfo($"[{C2Sain.ForkLog.Tag}] reach: {name} has no path to you and stays put, {toPlayer:0} m away: {why}");
            }
        }

        /// <summary>Fork v10: re-arms LogReach once a normal follow path works again.</summary>
        private void ClearReachLog()
        {
            var fork = McsBotPlayerData?.Fork;
            if (fork != null)
            {
                fork.ReachMoveLogged = false;
                fork.ReachStayLogged = false;
            }
        }

        public virtual bool TryUpdateFormationMoveTarget(Vector3 leadPos, out float nextUpdateTime)
        {
            nextUpdateTime = 1f;

            var mcsBotPlayerConfig = McsBotPlayerData?.McsAILeadPlayer?.McsBotPlayerConfig;
            if (mcsBotPlayerConfig == null)
            {
                return false;
            }

            if (!mcsBotPlayerConfig.EnableKeepFormation)
            {
                return false;
            }

            var leadPlayer = McsBotPlayerData?.LeadPlayer;
            if (leadPlayer == null)
            {
                return false;
            }

            var botIndex = Tools.GetMcsBotPlayerIndex(BotOwner.ProfileId, mcsBotPlayerConfig.FormationSequentialFill);
            if (botIndex < 5)
            {
                return false;
            }

            var predictedPos = leadPos + leadPlayer.Velocity * 2;
            if (NavMesh.SamplePosition(predictedPos, out var hit, 3f, -1))
            {
                predictedPos = hit.position;
            }
            else
            {
                predictedPos = leadPos;
            }

            var target = Tools.ComputeTarget(leadPlayer, predictedPos, botIndex, Tools.ParseFormationMatrix(mcsBotPlayerConfig.FormationMatrix), mcsBotPlayerConfig.FormationSpacing);
            if (!target.HasValue)
            {
                return false;
            }

            var groundPos = TryProjectToGround(target.Value);

            if (!CanGetPathToRun(BotOwner.Position, groundPos, McsBotPlayerData, out Vector3[] corners))
            {
                nextUpdateTime = 0.25f;
                return true;
            }

            var newMoveTarget = GetPointAlongPathAtDistance(corners, 15f);
            _currentMoveTarget = newMoveTarget;
            nextUpdateTime = 0.2f;
            return true;
        }

        public virtual void UpdateEscortMoveTarget(Vector3? escortPos, out float nextUpdateTime)
        {
            if (McsBotPlayerData == null)
            {
                nextUpdateTime = 1f;
                return;
            }

            if (!escortPos.HasValue)
            {
                nextUpdateTime = 0.25f;
                return;
            }

            if (_lastTargetPos != escortPos)
            {
                _lastTargetPos = escortPos;
                _lastCalcCorners = null;
                _lastCanRunResult = false;
                _currentMoveRetries = 0;
                _currentMoveTarget = escortPos;
            }

            var leadPos = BotOwner.GetMcsLeadPlayerPos(McsBotPlayerData);
            if (leadPos == null)
            {
                nextUpdateTime = 1f;
                return;
            }

            var leadVelocity = McsBotPlayerData.LeadPlayer.Velocity;
            var predictedPos = leadPos + leadVelocity * 2;

            if (NavMesh.SamplePosition(predictedPos, out var hit, 1f, -1))
            {
                predictedPos = hit.position;
            }
            else
            {
                predictedPos = leadPos;
            }

            var groundPos = TryProjectToGround(predictedPos);

            if (!CanGetPathToRun(groundPos, escortPos.Value, McsBotPlayerData, out Vector3[] corners))
            {
                nextUpdateTime = 0.25f;
                return;
            }

            var newMoveTarget = GetPointAlongPathAtDistance(corners, 15f);
            _currentMoveTarget = newMoveTarget;
            nextUpdateTime = 0.2f;
        }

        public virtual void UpdateCommonMoveTarget(Vector3? targetPos, out float nextUpdateTime)
        {
            if (McsBotPlayerData == null)
            {
                nextUpdateTime = 1f;
                return;
            }

            if (!targetPos.HasValue)
            {
                nextUpdateTime = 0.25f;
                return;
            }

            if (_lastTargetPos != targetPos)
            {
                _lastTargetPos = targetPos;
                _lastCalcCorners = null;
                _lastCanRunResult = false;
                _currentMoveRetries = 0;
                _currentMoveTarget = targetPos;
            }

            var selfVelocity = BotOwner.Velocity;
            var predictedPos = BotOwner.Position + selfVelocity * 2;

            if (NavMesh.SamplePosition(predictedPos, out var hit, 1f, -1))
            {
                predictedPos = hit.position;
            }
            else
            {
                predictedPos = BotOwner.Position;
            }

            var startGroundPos = TryProjectToGround(predictedPos);
            var targetGroundPos = TryProjectToGround(targetPos.Value);

            if (!CanGetPathToRun(startGroundPos, targetGroundPos, McsBotPlayerData, out Vector3[] corners))
            {
                nextUpdateTime = 0.25f;
                return;
            }

            var newMoveTarget = GetPointAlongPathAtDistance(corners, 30f);
            _currentMoveTarget = newMoveTarget;
            nextUpdateTime = 1f;
        }

        /// <summary>
        /// Fork: whether the bot should walk back toward the player. In formation, whether it is more than
        /// 1.5 m from its slot's move target; otherwise whether it is beyond the configured follow distance.
        /// Upstream compared the squared distance with 20, which tripped at about 4.5 m.
        /// </summary>
        /// <param name="sqrDistanceToLead">Squared distance from the bot to the player.</param>
        protected bool IsBeyondFollowDistance(float sqrDistanceToLead)
        {
            if (McsBotPlayerData != null && McsBotPlayerData.HasIntent(Intents.ShouldKeepFormation) && _currentMoveTarget.HasValue)
            {
                return BotOwner.Position.McsSqrDistance(_currentMoveTarget.Value) > 1.5f * 1.5f;
            }
            var followDistance = C2Sain.ForkConfig.FollowDistance?.Value ?? 8f;
            return sqrDistanceToLead >= followDistance * followDistance;
        }

        public virtual bool CanGetPathToRun(Vector3 startPos, Vector3 targetPos, McsBotPlayerData mcsBotPlayerData, out Vector3[] corners)
        {
            if (NavBridge.IsCrossing)
            {
                if (!NavBridge.ShouldAbort(targetPos))
                {
                    _currentMoveRetries = 0;
                    _lastCalcCorners = NavBridge.Way;
                    corners = _lastCalcCorners;
                    _lastCanRunResult = true;
                    return _lastCanRunResult;
                }

                NavBridge.Cancel();
            }

            var navMeshPath = new NavMeshPath();
            NavMesh.CalculatePath(startPos, targetPos, -1, navMeshPath);
            var flag = false;

            var sqrDistanceToTarget = startPos.McsSqrDistance(targetPos);
            var sampleRadius = sqrDistanceToTarget > 50f * 50f ? 5f : 1.5f;

            if (navMeshPath.status is NavMeshPathStatus.PathComplete or NavMeshPathStatus.PathPartial)
            {
                flag = true;
                var lastCorner = navMeshPath.corners[navMeshPath.corners.Length - 1];
                if ((targetPos - lastCorner).magnitude > Math.Max(2f, sampleRadius))
                {
                    flag = false;
                }

                if (flag && navMeshPath.status == NavMeshPathStatus.PathPartial && Math.Abs(lastCorner.y - targetPos.y) > 0.25f)
                {
                    flag = false;
                }
            }

            if (C2Sain.ForkConfig.GapCrossing.Value && NavBridge.TryPlanStepDown(BotOwner, targetPos, navMeshPath))
            {
                _currentMoveRetries = 0;
                _lastCalcCorners = NavBridge.Way;
                corners = _lastCalcCorners;
                _lastCanRunResult = true;
                return _lastCanRunResult;
            }

            if (!flag && Tools.BetterDestination(sampleRadius, targetPos, out var betterDest))
            {
                navMeshPath = new NavMeshPath();
                NavMesh.CalculatePath(startPos, betterDest, -1, navMeshPath);
                // Fork: the fallback destination must be fully reachable (upstream took partial paths unchecked).
                if (navMeshPath.status == NavMeshPathStatus.PathComplete)
                {
                    flag = true;
                }
            }

            if (!flag)
            {
                _currentMoveRetries += 1;
                // Fork: give up only after 5 failures in a row, and say so. Upstream also gave up on the
                // first failure when no earlier path had succeeded, silently cancelling the order.
                if (_currentMoveRetries >= 5)
                {
                    _currentMoveRetries = 0;
                    corners = null;
                    _lastCanRunResult = false;
                    if (mcsBotPlayerData.HasAnyIntent(Classification.TravelTaskIntents) || mcsBotPlayerData.HasIntent(Intents.ShouldClearArea))
                    {
                        BotOwner.TalkMsg(new McsMsg
                        {
                            PhraseTrigger = EPhraseTrigger.Negative,
                            Keys = ["McsFork/CantGetThere"]
                        });
                    }
                    mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation]);
                    mcsBotPlayerData.TargetPos = null;
                    mcsBotPlayerData.ProxyTargetId = null;
                    return _lastCanRunResult;
                }

                corners = _lastCalcCorners;
                return _lastCanRunResult;
            }

            _currentMoveRetries = 0;
            _lastCalcCorners = navMeshPath.corners;
            corners = _lastCalcCorners;
            _lastCanRunResult = true;
            return _lastCanRunResult;
        }

        protected void UpdateFastOpenDoor()
        {
            if (!C2Sain.ForkConfig.McsFastDoors.Value)
            {
                return;
            }
            var time = Time.time;
            if (_nextFastOpenDoorCheckTime > time)
            {
                return;
            }
            _nextFastOpenDoorCheckTime = time + 0.25f;

            var mcsBotPlayerData = McsBotPlayerData;
            if (mcsBotPlayerData == null)
            {
                return;
            }

            if (mcsBotPlayerData.FastOpenDoor != null && time <= mcsBotPlayerData.FastOpenDoorEndTime)
            {
                return;
            }

            mcsBotPlayerData.TryFinishFastOpenDoor();

            var targetPoint = BotOwner.Mover.TargetPoint;
            if (!targetPoint.HasValue || BotOwner.SqrDistHorizontal(targetPoint.Value) <= 0.01f)
            {
                return;
            }

            var doorLinks = BotOwner.NearDoorData.CurrentDoorLinks();
            if (doorLinks.Count == 0)
            {
                return;
            }

            var botPosition = BotOwner.Position;
            var sqrTriggerDist = FAST_OPEN_DOOR_TRIGGER_DIST * FAST_OPEN_DOOR_TRIGGER_DIST;
            for (var linkIndex = 0; linkIndex < doorLinks.Count; linkIndex++)
            {
                var doorLink = doorLinks[linkIndex];
                var door = doorLink?.Door;
                if (door == null || door.DoorState != EDoorState.Shut)
                {
                    continue;
                }

                if (botPosition.McsSqrDistance(doorLink.MidOpen) > sqrTriggerDist)
                {
                    continue;
                }

                if (mcsBotPlayerData.FastOpenDoorCooldowns.TryGetValue(door.Id, out var cooldownTime) && time < cooldownTime)
                {
                    continue;
                }

                if (!IsMoveDirectionCrossingDoorOpen(botPosition, targetPoint.Value, doorLink))
                {
                    continue;
                }

                FastOpenDoor(door, time);
                return;
            }
        }

        private bool IsMoveDirectionCrossingDoorOpen(Vector3 botPosition, Vector3 targetPoint, NavMeshDoorLink doorLink)
        {
            if (Mathf.Abs(botPosition.y - doorLink.MidOpen.y) > 3f)
            {
                return false;
            }

            var segmentOpen = doorLink.SegmentOpen;
            var openDir = segmentOpen.b - segmentOpen.a;
            var openA = segmentOpen.a - openDir * 0.1f;
            var openB = segmentOpen.b + openDir * 0.1f;

            return AIUtility.GetCrossPoint(botPosition, targetPoint, openA, openB) != null;
        }

        private void FastOpenDoor(Door door, float time)
        {
            var player = BotOwner.GetPlayer;
            if (player == null || player.MovementContext.CanInteract != null)
            {
                return;
            }

            BotOwner.Mover.Sprint(false);
            player.ExecuteInteraction(door, new InteractionResult(EInteractionType.Open));
            BotOwner.Mover.SprintPause(FAST_OPEN_DOOR_WINDOW);
            var mcsBotPlayerData = McsBotPlayerData;
            mcsBotPlayerData.FastOpenDoor = door;
            mcsBotPlayerData.FastOpenDoorEndTime = time + FAST_OPEN_DOOR_WINDOW;
            mcsBotPlayerData.FastOpenDoorCooldowns[door.Id] = time + 3.5f;
        }

        public virtual Vector3 GetPointAlongPathAtDistance(Vector3[] corners, float distance)
        {
            var accumulated = 0f;
            for (int i = 0; i < corners.Length - 1; i++)
            {
                var segLen = Vector3.Distance(corners[i], corners[i + 1]);
                if (accumulated + segLen >= distance)
                {
                    var t = (distance - accumulated) / segLen;
                    return Vector3.Lerp(corners[i], corners[i + 1], t);
                }
                accumulated += segLen;
            }
            return corners[corners.Length - 1];
        }

        public virtual Vector3 TryProjectToGround(Vector3 pos)
        {
            Vector3 result;
            if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out var rayHit, 50f, LayersMaskController.HighPolyWithTerrainMask))
            {
                if (NavMesh.SamplePosition(rayHit.point, out var navHit1, 1f, -1))
                {
                    result = navHit1.position;
                }
                else
                {
                    result = rayHit.point;
                }
            }
            else if (NavMesh.SamplePosition(pos, out var navHit2, 2f, -1)) // Fork: was 10 m, which could land on another floor
            {
                result = navHit2.position;
            }
            else
            {
                result = pos;
            }

            return result;
        }

        public virtual bool EndDeactivateMine()
        {
            if (IsApproachingThreat())
            {
                return true;
            }

            if (BotOwner.Mover._lastTimePosChanged + 1f < Time.time)
            {
                CheckStuck();
            }

            if (!BotOwner.BewarePlantedMine.CanDeactivate())
            {
                return true;
            }

            if (Time.time > _nextDeactivateCheckTime)
            {
                _currentDeactivateRetries += 1;
                _nextDeactivateCheckTime = Time.time + 1f;
            }

            if (_currentDeactivateRetries >= 15)
            {
                _currentDeactivateRetries = 0;
                return true;
            }
            return false;
        }

        public virtual bool EndRunAwayGrenade()
        {
            if (BotOwner.Mover._lastTimePosChanged + 1f < Time.time)
            {
                CheckStuck();
            }

            if (!BotOwner.BewareGrenade.ShallRunAway())
            {
                return true;
            }

            if (BotOwner.Memory.IsInCover)
            {
                return true;
            }

            return false;
        }

        public virtual bool EndRunAwayArtillery()
        {
            if (BotOwner.Mover._lastTimePosChanged + 1f < Time.time)
            {
                CheckStuck();
            }

            if (!BotOwner.ArtilleryDangerPlace.ShallRunAway())
            {
                return true;
            }

            if (BotOwner.Memory.IsInCover)
            {
                return true;
            }

            return false;
        }

        public virtual bool EndRunAwayBTR()
        {
            if (BotOwner.Mover._lastTimePosChanged + 1f < Time.time)
            {
                CheckStuck();
            }

            if (!BotOwner.BewareBTR.ShallRunAway())
            {
                return true;
            }

            if (BotOwner.Memory.IsInCover)
            {
                return true;
            }

            return false;
        }

        public virtual bool EndFlashed()
        {
            if (!BotOwner.FlashGrenade.IsFlashed)
            {
                return true;
            }

            return false;
        }

        public virtual bool EndGoToExcuteProxyAction()
        {
            if (IsApproachingThreat())
            {
                return true;
            }

            if (McsBotPlayerData == null)
            {
                return true;
            }

            if (!McsBotPlayerData.HasAnyIntent(Intents.ShouldInteractionProxyAction, Intents.ShouldLootProxyAction, Intents.ShouldQuestProxyAction))
            {
                return true;
            }
            return false;
        }

        public virtual bool EndDropTargetLootLogic()
        {
            if (IsApproachingThreat())
            {
                return true;
            }

            if (McsBotPlayerData == null)
            {
                return true;
            }

            var haveItemsToDrop = BotOwner.ExternalItemsController.HaveItemsToDrop();
            if (!haveItemsToDrop)
            {
                McsBotPlayerData.RemoveIntent([Intents.ShouldDropTargetLoot]);
                _nextLootingCheckTime = Time.time + ENTER_COMMON_LOOTING_COODDOWN * 2;
                return true;
            }

            var mcsLeadPlayerPos = BotOwner.GetMcsLeadPlayerPos(McsBotPlayerData);
            var sqrDistance = BotOwner.Position.McsSqrDistance(mcsLeadPlayerPos);
            if (sqrDistance > 9f)
            {
                return true;
            }

            return false;
        }

        public void RefreshStuckTimer()
        {
            BotOwner.Mover._lastTimePosChanged = Time.time;
        }

        public virtual bool CheckFirearmsAnimatorState()
        {
            BotOwner.WeaponManager.CheckWeaponReady();
            var time = Time.time;
            if (time < _nextAnimatorFixTime)
            {
                _nextAnimatorFixTime = time + 1;
                return true;
            }

            var player = BotOwner.GetPlayer;
            var firearmController = player?.HandsController as Player.FirearmController;
            if (firearmController == null)
            {
                _nextAnimatorFixTime = time + 1;
                BotOwner.WeaponManager.Selector.TryChangeToMain();
                return false;
            }

            if (firearmController?.FirearmsAnimator == null)
            {
                _nextAnimatorFixTime = time + 1;
                BotOwner.WeaponManager.Selector.TryChangeToMain();
                return false;
            }

            var handsIdle = !player.HandsController.IsAiming
                        && !player.HandsController.IsInventoryOpen()
                        && !player.HandsController.IsInInteractionStrictCheck()
                        && !player.HandsController.IsHandsProcessing();

            if (!BotOwner.WeaponManager.Selector.IsWeaponReady && !handsIdle)
            {
                _nextAnimatorFixTime = time + 1;
                BotOwner.WeaponManager.Selector.TryChangeToMain();
                return false;
            }

            _nextAnimatorFixTime = time + 1;
            return true;
        }

        public virtual bool TryGetBtrFollowAction(float time, out Action action)
        {
            action = null;

            var btrController = Singleton<GameWorld>.Instance.BtrController;
            if (btrController == null || btrController.BtrVehicle == null || btrController.BtrView == null)
            {
                return false;
            }

            var leadPlayer = McsBotPlayerData.LeadPlayer;
            if (leadPlayer == null)
            {
                return false;
            }

            var btrVehicle = btrController.BtrVehicle;
            var selfPlayer = BotOwner.GetPlayer;
            var selfIsPassenger = btrVehicle.IsPassenger(selfPlayer, out var selfPassenger);
            var bossInBtr = leadPlayer.BtrState == EPlayerBtrState.Inside || leadPlayer.BtrState == EPlayerBtrState.GoIn;
            var bossOutBtr = leadPlayer.BtrState == EPlayerBtrState.Outside || leadPlayer.BtrState == EPlayerBtrState.GoOut;

            if (bossInBtr && !selfIsPassenger)
            {
                if (!TryFindFreeSeat(btrController, out byte sideId, out byte slotId, out Vector3 doorPos))
                {
                    return false;
                }

                McsBotPlayerData.BtrTargetSide = sideId;
                McsBotPlayerData.BtrTargetSlot = slotId;
                McsBotPlayerData.IsBtrLeaving = false;

                if (_nextUpdatePosTime < time)
                {
                    UpdateCommonMoveTarget(doorPos, out float nextTime);
                    _nextUpdatePosTime = time + nextTime;
                }

                if (_currentMoveTarget.HasValue)
                {
                    BotOwner.GoToSomePointData.SetPoint(_currentMoveTarget.Value);
                }

                action = new Action(typeof(GoToBtrLogic), "Mcs:GoToBtr");
                return true;
            }

            if (selfIsPassenger && !bossOutBtr)
            {
                McsBotPlayerData.IsBtrLeaving = false;
                action = new Action(typeof(HoldPositionLogic), "Mcs:BtrStay");
                return true;
            }

            if (selfIsPassenger && bossOutBtr)
            {
                McsBotPlayerData.IsBtrLeaving = true;
                McsBotPlayerData.BtrTargetSide = selfPassenger.SideId;
                McsBotPlayerData.BtrTargetSlot = selfPassenger.SlotId;
                action = new Action(typeof(GoToBtrLogic), "Mcs:LeaveBtr");
                return true;
            }
            return false;
        }

        public virtual bool TryFindFreeSeat(BtrController btrController, out byte sideId, out byte slotId, out Vector3 doorPos)
        {
            sideId = 0;
            slotId = 0;
            doorPos = Vector3.zero;

            for (byte s = 0; s <= 1; s++)
            {
                var side = btrController.BtrView.GetBtrSide(s);
                if (side == null)
                {
                    continue;
                }

                var info = side.SideInfo();
                for (byte slot = 0; slot < info.Length; slot++)
                {
                    if (info[slot])
                    {
                        sideId = s;
                        slotId = slot;
                        doorPos = side.GoInPoints().Item1;
                        return true;
                    }
                }
            }
            return false;
        }

        public virtual bool IsTargetPitchReachable(StationaryWeapon stationaryWeapon, Vector3 targetPos)
        {
            var origin = stationaryWeapon.OperatorPosition;
            var delta = targetPos - origin;

            var horizontal = new Vector3(delta.x, 0f, delta.z).magnitude;
            if (horizontal < 0.01f)
            {
                return false;
            }

            var requiredPitch = Mathf.Atan2(-delta.y, horizontal) * Mathf.Rad2Deg;
            var pitchLimit = stationaryWeapon.PitchLimit;
            var min = Mathf.Min(pitchLimit.x, pitchLimit.y);
            var max = Mathf.Max(pitchLimit.x, pitchLimit.y);

            return requiredPitch >= min - 2f && requiredPitch <= max + 2f;
        }

        public virtual void ScanSector(StationaryWeaponLink link)
        {
            var weapon = link.Weapon;
            if (weapon == null)
            {
                return;
            }

            var halfAngleDeg = Mathf.Acos(Mathf.Clamp(link.CosAngleBase, -1f, 1f)) * Mathf.Rad2Deg;

            _scanPhase += Time.deltaTime / 4f;
            var tri = Mathf.PingPong(_scanPhase, 1f);
            var yawDeg = Mathf.Lerp(-halfAngleDeg, halfAngleDeg, tri);

            var baseDir = link.InitialDir;
            baseDir.y = 0f;
            if (baseDir.sqrMagnitude < 0.001f)
            {
                return;
            }
            baseDir.Normalize();

            var dir = Quaternion.AngleAxis(yawDeg, Vector3.up) * baseDir;
            dir = Quaternion.AngleAxis(2f, Vector3.Cross(dir, Vector3.up)) * dir;
            var scanPoint = weapon.OperatorPosition + dir * 30f;
            BotOwner.AimingManager.CurrentAiming.SetTarget(scanPoint);
            BotOwner.AimingManager.NodeUpdate();
        }

        public virtual bool EndDogFight()
        {
            var goalEnemy = BotOwner.Memory.GoalEnemy;
            if (goalEnemy == null || goalEnemy.Person == null || !goalEnemy.Person.HealthController.IsAlive)
            {
                return true;
            }

            if (BotOwner.Position.McsSqrDistance(goalEnemy.Person.Position) > (20f * 20f))
            {
                return true;
            }

            if (!goalEnemy.IsVisible && Time.time - GetEnemyLastSeenTime() > 8f)
            {
                return true;
            }

            return false;
        }

        public virtual bool EndSuppressFire()
        {
            var goalEnemy = BotOwner.Memory.GoalEnemy;
            if (goalEnemy == null || goalEnemy.Person == null)
            {
                return true;
            }

            if (goalEnemy.IsVisible)
            {
                return true;
            }

            if (!BotOwner.WeaponManager.HaveBullets)
            {
                return true;
            }

            var time = Time.time;
            var timeSinceSeen = time - GetEnemyLastSeenTime();
            if (timeSinceSeen > SUPPRESS_TIME_SINCE_SEEN && !IsShotByEnemyRecently(goalEnemy, time))
            {
                return true;
            }

            return false;
        }

        public virtual bool EndBlindFireBlocked()
        {
            var goalEnemy = BotOwner.Memory.GoalEnemy;
            if (goalEnemy == null || goalEnemy.Person == null)
            {
                return true;
            }

            if (!goalEnemy.IsVisible)
            {
                return true;
            }

            if (!BotOwner.WeaponManager.HaveBullets)
            {
                return true;
            }

            if (CanShootNow())
            {
                var closestFriend = BotOwner.Covers.GetClosestFriend(out var sqrDist);
                if (sqrDist >= 1f || closestFriend == null || closestFriend.Id > BotOwner.Id)
                {
                    return true;
                }
            }

            return false;
        }

        public virtual bool EndCoverPeak()
        {
            var goalEnemy = BotOwner.Memory.GoalEnemy;
            if (goalEnemy == null || goalEnemy.Person == null || !goalEnemy.Person.HealthController.IsAlive)
            {
                return true;
            }

            if (!BotOwner.Memory.IsInCover)
            {
                return true;
            }

            return false;
        }

        public virtual void UpdateHoldGroundTimer(EnemyInfo goalEnemy, float time)
        {
            var enemyId = goalEnemy.Person?.ProfileId;
            if (_holdGroundEnemyId != enemyId)
            {
                _holdGroundEnemyId = enemyId;
                _holdGroundStartTime = time;
                _holdGroundDuration = 4f * UnityEngine.Random.Range(0.66f, 1.5f);
            }
        }

        public virtual bool IsEnemyLookingAtMe(EnemyInfo goalEnemy)
        {
            var enemyBotOwner = goalEnemy.Person?.AIData?.BotOwner;
            if (enemyBotOwner != null && enemyBotOwner.BotState != EBotState.NonActive)
            {
                var enemyGoalEnemy = enemyBotOwner.Memory?.GoalEnemy;
                if (enemyGoalEnemy?.Person == null)
                {
                    return false;
                }
                return enemyGoalEnemy.Person.ProfileId == BotOwner.ProfileId;
            }
            return true;
        }

        public virtual void TrySelectLastHitShooter(float time)
        {
            var shooter = McsBotPlayerData.LastHitShooter;
            if (shooter == null || time - McsBotPlayerData.LastHitTime > 2f || !shooter.HealthController.IsAlive || Tools.IsForbiddenEnemy(shooter))
            {
                return;
            }

            var goalEnemy = BotOwner.Memory.GoalEnemy;
            if (goalEnemy != null && goalEnemy.Person?.ProfileId == shooter.ProfileId)
            {
                return;
            }

            if (BotOwner.EnemiesController.EnemyInfos.TryGetValue(shooter, out var enemyInfo))
            {
                BotOwner.Memory.GoalEnemy = enemyInfo;
                _nextRecalcGoalTime = 0f;
            }
        }

        public virtual void TrySelectLeadThreatEnemy(float time)
        {
            var mcsAILeadPlayer = McsBotPlayerData.McsAILeadPlayer;
            if (mcsAILeadPlayer == null)
            {
                return;
            }

            var threatEnemy = mcsAILeadPlayer.GetLeadThreatEnemy();
            if (threatEnemy == null || Tools.IsForbiddenEnemy(threatEnemy))
            {
                return;
            }

            var goalEnemy = BotOwner.Memory.GoalEnemy;
            if (goalEnemy != null && goalEnemy.Person?.ProfileId == threatEnemy.ProfileId)
            {
                return;
            }

            if (BotOwner.EnemiesController.EnemyInfos.TryGetValue(threatEnemy, out var enemyInfo))
            {
                BotOwner.Memory.GoalEnemy = enemyInfo;
                _nextRecalcGoalTime = 0f;
            }
        }

        public virtual bool TrySelectNearestLeadVisibleEnemy(float time)
        {
            var mcsAILeadPlayer = McsBotPlayerData.McsAILeadPlayer;
            if (mcsAILeadPlayer == null)
            {
                return false;
            }

            var leadVisibleEnemies = mcsAILeadPlayer.LeadVisibleEnemies;
            if (leadVisibleEnemies == null || leadVisibleEnemies.Count == 0)
            {
                return false;
            }

            EnemyInfo nearestEnemyInfo = null;
            var minSqrDistance = float.MaxValue;
            foreach (var leadVisibleEnemy in leadVisibleEnemies)
            {
                if (leadVisibleEnemy == null || !leadVisibleEnemy.HealthController.IsAlive || Tools.IsForbiddenEnemy(leadVisibleEnemy))
                {
                    continue;
                }

                if (!BotOwner.EnemiesController.EnemyInfos.TryGetValue(leadVisibleEnemy, out var enemyInfo))
                {
                    continue;
                }

                var sqrDistance = BotOwner.Position.McsSqrDistance(leadVisibleEnemy.Position);
                if (sqrDistance < minSqrDistance)
                {
                    nearestEnemyInfo = enemyInfo;
                    minSqrDistance = sqrDistance;
                }
            }

            if (nearestEnemyInfo == null)
            {
                return false;
            }

            BotOwner.Memory.GoalEnemy = nearestEnemyInfo;
            _nextRecalcGoalTime = 0f;
            return true;
        }

        public virtual bool IsLeadThreatEnemy(IPlayer enemyPerson)
        {
            var mcsAILeadPlayer = McsBotPlayerData?.McsAILeadPlayer;
            if (mcsAILeadPlayer == null || enemyPerson == null)
            {
                return false;
            }

            return mcsAILeadPlayer.IsLeadThreatEnemy(enemyPerson);
        }

        public virtual bool TryGetCombatCoverTarget(Vector3 mcsLeadPlayerPos, out Vector3 coverPos)
        {
            coverPos = Vector3.zero;
            CustomNavigationPoint coverPoint = null;

            if (_haveCoverToShoot && _currentNavigationPoint != null && _currentNavigationPoint.IsFreeById(BotOwner.Id) && !_currentNavigationPoint.IsSpotted)
            {
                coverPoint = _currentNavigationPoint;
            }
            else
            {
                TryFindCover(mcsLeadPlayerPos);
                if (_currentNavigationPoint != null && !_currentNavigationPoint.IsSpotted)
                {
                    coverPoint = _currentNavigationPoint;
                }
            }

            if (coverPoint == null)
            {
                return false;
            }

            if (BotOwner.Position.McsSqrDistance(coverPoint.Position) > (30f * 30f))
            {
                return false;
            }

            if (mcsLeadPlayerPos.McsSqrDistance(coverPoint.Position) > TOO_FAR_FROM_LEAD_DISTANCE * TOO_FAR_FROM_LEAD_DISTANCE)
            {
                return false;
            }

            if (BotOwner.Position.McsSqrDistance(coverPoint.Position) <= (1.5f * 1.5f))
            {
                return false;
            }

            coverPos = coverPoint.Position;
            return true;
        }

        public virtual void TrackCoverEnter(float time)
        {
            if (BotOwner.Memory.IsInCover)
            {
                if (_coverEnterTime <= 0f)
                {
                    _coverEnterTime = time;
                }
            }
            else
            {
                _coverEnterTime = 0f;
            }
        }

        public virtual void UpdateGoalEnemyWithHysteresis(float time)
        {
            var currentGoalEnemy = BotOwner.Memory.GoalEnemy;
            var currentGoalId = currentGoalEnemy?.Person?.ProfileId;
            if (currentGoalId != _lastAcceptedGoalEnemyId)
            {
                _lastAcceptedGoalEnemyId = currentGoalId;
                _lastAcceptedGoalSetTime = time;
            }

            if (BotOwner.Memory.DangerData.HaveCloseDanger)
            {
                BotOwner.Memory.GoalEnemy = null;
                return;
            }

            var candidateEnemy = BotOwner.EnemyChooser.FindDangerEnemy();
            if (candidateEnemy == null)
            {
                if (BotOwner.Memory.GoalEnemy == null && BotOwner.Memory.HaveGoal)
                {
                    BotOwner.Memory.GoalTarget.Clear();
                }

                if (currentGoalEnemy == null)
                {
                    TrySelectNearestLeadVisibleEnemy(time);
                }
                return;
            }

            if (currentGoalEnemy == null || currentGoalEnemy == candidateEnemy)
            {
                AcceptGoalEnemy(candidateEnemy, time);
                return;
            }

            var currentPerson = currentGoalEnemy.Person;
            var currentAlive = currentPerson != null && currentPerson.HealthController != null && currentPerson.HealthController.IsAlive;
            var currentStillViable = currentAlive && currentGoalEnemy.IsVisible && currentGoalEnemy.CanShoot;

            if (currentAlive && IsLeadThreatEnemy(currentPerson) && !IsLeadThreatEnemy(candidateEnemy.Person))
            {
                return;
            }

            if (!currentStillViable)
            {
                AcceptGoalEnemy(candidateEnemy, time);
                return;
            }

            if (IsLeadThreatEnemy(candidateEnemy.Person))
            {
                AcceptGoalEnemy(candidateEnemy, time);
                return;
            }

            if (currentPerson != null && currentPerson.ProfileId == _lastAcceptedGoalEnemyId && time - _lastAcceptedGoalSetTime < 2f)
            {
                return;
            }

            if (candidateEnemy.IsVisible && candidateEnemy.CanShoot
                && candidateEnemy.Distance <= currentGoalEnemy.Distance * 0.75f)
            {
                AcceptGoalEnemy(candidateEnemy, time);
                return;
            }

        }

        public virtual void AcceptGoalEnemy(EnemyInfo goalEnemy, float time)
        {
            // 目标选择链路的最后落点：即使有遗漏入口把禁敌角色送进来，也在这里挡掉
            if (goalEnemy != null && Tools.IsForbiddenEnemy(goalEnemy.Person))
            {
                return;
            }

            BotOwner.Memory.GoalEnemy = goalEnemy;
            _lastAcceptedGoalEnemyId = goalEnemy?.Person?.ProfileId;
            _lastAcceptedGoalSetTime = time;
        }

        public virtual bool ShallRushEnemy(float enemyToLeadSqrDist, EnemyInfo goalEnemy, bool hasTravelTask, float botToEnemySqrDist = float.MaxValue)
        {
            if (McsBotPlayerData.IsHeavySuppressed)
            {
                return false;
            }

            if (McsBotPlayerData.HasAnyIntent(Intents.ShouldKeepFormation, Intents.ShouldUseStationaryWeapon, Intents.ShouldHoldPosition, Intents.ShouldFollowMe))
            {
                return false;
            }

            if (!hasTravelTask && (enemyToLeadSqrDist <= (50f * 50f) || botToEnemySqrDist <= RUSH_NEAR_ENEMY_SQUARE_DIST))
            {
                return true;
            }

            var enemyBotOwner = goalEnemy.Person?.AIData?.BotOwner;
            if (enemyBotOwner != null && enemyBotOwner.BotState != EBotState.NonActive)
            {
                if (enemyBotOwner.WeaponManager?.Reload?.Reloading == true || enemyBotOwner.Medecine?.Using == true)
                {
                    return true;
                }
            }

            return false;
        }

        public virtual void TryRushJump(EnemyInfo goalEnemy, float time)
        {
            if (goalEnemy?.Person == null)
            {
                return;
            }

            var botToEnemySqrDist = BotOwner.Position.McsSqrDistance(goalEnemy.Person.Position);
            if (botToEnemySqrDist < 1.5f * 1.5f || botToEnemySqrDist > 6f * 6f)
            {
                return;
            }

            if (time < _nextRushJumpTime)
            {
                return;
            }

            if (!MyExtensions.IsTrue100(35f))
            {
                return;
            }

            _nextRushJumpTime = time + 3f;
            BotOwner.GetPlayer?.MovementContext?.TryJump();
        }

        public virtual bool IsShotByEnemyRecently(EnemyInfo goalEnemy, float time)
        {
            var shooter = McsBotPlayerData.LastHitShooter;
            if (shooter == null || goalEnemy.Person == null)
            {
                return false;
            }
            return shooter.ProfileId == goalEnemy.Person.ProfileId && time - McsBotPlayerData.LastHitTime <= 6f;
        }

        public virtual bool TryRefreshCommonTarget(Vector3? targetPos, float time)
        {
            if (_nextUpdatePosTime < time)
            {
                UpdateCommonMoveTarget(targetPos, out float nextTime);
                _nextUpdatePosTime = time + nextTime;
            }
            return _currentMoveTarget.HasValue;
        }

        public virtual bool TryRefreshEscortTarget(Vector3? escortPos, float time)
        {
            if (_nextUpdatePosTime < time)
            {
                UpdateEscortMoveTarget(escortPos, out float nextTime);
                _nextUpdatePosTime = time + nextTime;
            }
            return _currentMoveTarget.HasValue;
        }

        public virtual bool TryRefreshLeadTarget(Vector3? leadPos, float time)
        {
            if (_nextUpdatePosTime < time)
            {
                UpdateLeadNearMoveTarget(leadPos, out float nextTime);
                _nextUpdatePosTime = time + nextTime;
            }
            return _currentMoveTarget.HasValue;
        }

        public virtual void ApplyMovePoint()
        {
            if (NavBridge.IsCrossing)
            {
                return;
            }

            if (_currentMoveTarget.HasValue)
            {
                BotOwner.GoToSomePointData.SetPoint(_currentMoveTarget.Value);
            }
        }

        public virtual void FinishClearArea()
        {
            McsBotPlayerData.ClearAreaPoints = null;
            McsBotPlayerData.ClearAreaIndex = 0;
            McsBotPlayerData.ClearAreaLookAroundUntil = 0f;
            McsBotPlayerData.TargetPos = null;
            McsBotPlayerData.RemoveIntent(Intents.ShouldClearArea);
            BotOwner.TalkMsg(new McsMsg
            {
                PhraseTrigger = EPhraseTrigger.Clear
            });
        }

        public virtual void ReportFirstContact(EnemyInfo goalEnemy, float time)
        {
            var enemyId = goalEnemy?.Person?.ProfileId;
            if (enemyId == null || enemyId == _lastReportedContactEnemyId)
            {
                return;
            }

            if (time - _lastContactReportTime < 3f)
            {
                return;
            }

            _lastReportedContactEnemyId = enemyId;
            _lastContactReportTime = time;
            BotOwner.TalkMsg(new McsMsg
            {
                PhraseTrigger = EPhraseTrigger.OnFirstContact,
                Position = goalEnemy.EnemyLastPosition
            });
        }

        public virtual void ReportFightState(bool fightActive, float time)
        {
            if (fightActive)
            {
                _wasFightActive = true;
                return;
            }

            if (!_wasFightActive)
            {
                return;
            }

            _wasFightActive = false;
            _lastReportedContactEnemyId = null;

            if (time - _lastContactReportTime < 0.5f)
            {
                return;
            }

            foreach (var member in BotOwner.BotsGroup._members)
            {
                if (member.Memory.HaveEnemy)
                {
                    return;
                }
            }

            BotOwner.TalkMsg(new McsMsg
            {
                PhraseTrigger = EPhraseTrigger.Clear
            });
        }

        public virtual void ReportTaskStart(ref bool wasActive, bool isActive, EPhraseTrigger phraseTrigger, string[] keys = null)
        {
            if (wasActive == isActive)
            {
                return;
            }

            wasActive = isActive;
            if (!isActive)
            {
                return;
            }

            BotOwner.TalkMsg(new McsMsg
            {
                PhraseTrigger = phraseTrigger,
                Keys = keys
            });
        }
    }
}