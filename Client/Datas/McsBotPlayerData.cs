

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using EFT.Interactive;
using MiyakoCarryService.Client.Enums;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Misc;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.Datas
{
    public class McsBotPlayerData : PlayerData
    {
        private WeakReference<BotOwner> _botOwnerRef;
        public BotOwner BotOwner => _botOwnerRef.TryGetTarget(out var botOwner) ? botOwner : null;
        private WeakReference<Player> _leadPlayeRef;
        public Player LeadPlayer => _leadPlayeRef.TryGetTarget(out var leadPlayer) ? leadPlayer : null;
        public GamePlayerOwner LeadPlayerGamePlayerOwner => McsAILeadPlayer.GamePlayerOwner;
        private WeakReference<McsAILeadPlayer> _mcsAILeadPlayerRef;
        public McsAILeadPlayer McsAILeadPlayer => _mcsAILeadPlayerRef.TryGetTarget(out var mcsAILeadPlayer) ? mcsAILeadPlayer : null;
        public BodyPartType AimingBodyPartType = BodyPartType.head;
        public int CarryServiceLevel
        {
            get
            {
                if (field <= 0)
                {
                    var player = Player;
                    if (player == null)
                    {
                        return 5;
                    }

                    field = BotSettingUtils.GetCarryServiceLevel(player.Profile.Info.Level);
                }

                return field;
            }
            set => field = value;
        }

        public Vector3? TargetPos = null;
        public bool MoveResetRequested = false;
        public string ProxyTargetId = null;
        public LootData LootingTarget = null;
        public List<Vector3> ClearAreaPoints = null;
        public int ClearAreaIndex = 0;
        public float ClearAreaLookAroundUntil = 0f;
        public bool IsLooting
        {
            get => field;
            set
            {
                field = value;
                if (!field && LootingTarget != null)
                {
                    LootDataMgr.UnlockLootingTarget(LootingTarget);
                    LootDataMgr.UnlockLootingTargetRootTransform(LootingTarget.RootTransform);
                    LootingTarget = null;
                    ProxyTargetId = null;
                    TargetPos = null;
                    RemoveIntent([Intents.ShouldLootProxyAction, Intents.ShouldQuestProxyAction, Intents.ShouldInteractionProxyAction, Intents.ShouldStationaryWeaponProxyAction]);
                }
            }
        }
        public bool IsTaskRunning = false;
        private HashSet<string> _intents = new();
        private HashSet<LootData> _vanishingCurseLootItems = new();
        private const float EMERGENCY_NEED_CHECK_INTERVAL = 5f;
        private const float AMMO_SUFFICIENT_MULTIPLIER = 3f;
        private const int DEFAULT_MAG_CAPACITY = 30;
        private const int REQUIRED_MAG_COUNT = 3;
        private float _nextEmergencyNeedCheckTime = 0f;
        private ELootNeedType _cachedEmergencyLootNeed = ELootNeedType.None;
        private HashSet<EDamageEffectType> _missingMedEffects = new();
        private string _enemyVelocityKey = null;
        private Vector3 _enemyVelocityPosition = Vector3.zero;
        private float _enemyVelocityTime = 0f;
        private Vector3 _enemyVelocity = Vector3.zero;

        public Vector3 GetEstimatedEnemyVelocity(EnemyInfo enemyInfo)
        {
            var person = enemyInfo?.Person;
            if (person == null)
            {
                return Vector3.zero;
            }

            var key = person.ProfileId;
            var position = enemyInfo.CurrPosition;
            var now = Time.time;

            if (key != _enemyVelocityKey)
            {
                _enemyVelocityKey = key;
                _enemyVelocityPosition = position;
                _enemyVelocityTime = now;
                _enemyVelocity = Vector3.zero;
                return Vector3.zero;
            }

            var deltaTime = now - _enemyVelocityTime;
            if (deltaTime < 0.05f)
            {
                return _enemyVelocity;
            }

            _enemyVelocity = (position - _enemyVelocityPosition) / deltaTime;
            _enemyVelocityPosition = position;
            _enemyVelocityTime = now;
            return _enemyVelocity;
        }

        public bool IsBtrLeaving = false;
        public byte BtrTargetSide = 0;
        public byte BtrTargetSlot = 0;
        public bool IsExcluded = false;
        public Door FastOpenDoor = null;
        public float FastOpenDoorEndTime = 0f;
        public Dictionary<string, float> FastOpenDoorCooldowns = new();
        public float LastHitTime = -999f;
        public Player LastHitShooter = null;
        public float LastShotAtTime = -999f;
        /// <summary>Fork: per-bot C2/SAIN state (C2Sain/BotForkState.cs).</summary>
        internal C2Sain.BotForkState Fork { get; } = new();
        public float SuppressionNumber = 0f;
        public bool IsHeavySuppressed => SuppressionNumber >= 3f;
        private const float SUPPRESSION_MAX = 6f;
        private float _lastSuppressionDecayTime = 0f;

        public void TryFinishFastOpenDoor()
        {
            var fastOpenDoor = FastOpenDoor;
            if (fastOpenDoor == null)
            {
                return;
            }

            if (Time.time <= FastOpenDoorEndTime && fastOpenDoor)
            {
                return;
            }

            FastOpenDoor = null;
        }

        public void UpdateSuppressionDecay()
        {
            var time = Time.time;
            var delta = time - _lastSuppressionDecayTime;
            _lastSuppressionDecayTime = time;
            if (delta < 0.25f || SuppressionNumber <= 0f)
            {
                return;
            }

            SuppressionNumber = Mathf.Max(0f, SuppressionNumber - 0.75f * delta);
        }

        public void AddSuppression(float damage)
        {
            SuppressionNumber = Mathf.Min(SUPPRESSION_MAX, SuppressionNumber + Mathf.Clamp(damage / 20f, 0.5f, 2f));
        }

        public void AddSuppressionFromNearMiss()
        {
            SuppressionNumber = Mathf.Min(SUPPRESSION_MAX, SuppressionNumber + 0.15f);
        }

        public Vector2 BottomScreenPos = Vector2.zero;
        public string Info = "";
        public bool IsVisible = false;
        public Color Color
        {
            get;
            set
            {
                if (field != value)
                {
                    GUIStyle.normal.textColor = value;
                }
                field = value;
            }
        }
        public GUIStyle GUIStyle { get; protected set; }
        public Rect Rect;
        public int Distance = 0;
        public string TeamInfo = "";
        public string BaseInfo = "";
        public string WeaponInfo = "";
        public string EffectsInfo = "";
        public string SuppliesInfo = "";
        public StringBuilder BaseInfoBuilder = new StringBuilder();
        public StringBuilder WeaponInfoBuilder = new StringBuilder();
        public StringBuilder EffectsInfoBuilder = new StringBuilder();
        public StringBuilder SuppliesInfoBuilder = new StringBuilder();
        private readonly GUIContent _measureContent = new GUIContent();
        private string _measuredInfo = null;
        private int _measuredFontSize = -1;
        private Vector2 _measuredGuiSize = Vector2.zero;
        private readonly List<string> _effectEntries = new();

        public void SetIntent(string[] exclude = null, params string[] intents)
        {
            List<string> preserved = null;
            if (exclude != null)
            {
                preserved = new List<string>();
                foreach (var e in exclude)
                {
                    if (_intents.Contains(e))
                    {
                        preserved.Add(e);
                    }
                }
            }

            _intents.Clear();

            if (intents != null)
            {
                foreach (var intent in intents)
                {
                    _intents.Add(intent);
                }
            }

            if (preserved != null)
            {
                foreach (var p in preserved)
                {
                    _intents.Add(p);
                }
            }
        }

        public bool HasIntent(params string[] intents)
        {
            foreach (var intent in intents)
            {
                if (!_intents.Contains(intent))
                {
                    return false;
                }
            }
            return true;
        }

        public bool HasAnyIntent(params string[] intents)
        {
            if (intents == null)
            {
                return false;
            }

            foreach (var intent in intents)
            {
                if (_intents.Contains(intent))
                {
                    return true;
                }
            }
            return false;
        }

        public void AddIntent(params string[] intents)
        {
            foreach (var intent in intents)
            {
                _intents.Add(intent);
            }
        }

        public void RemoveIntent(params string[] intents)
        {
            foreach (var intent in intents)
            {
                _intents.Remove(intent);
            }
        }

        public McsBotPlayerData(Player bossPlayer, McsAILeadPlayer mcsAILeadPlayer, Player player, Item item) : base(player, item)
        {
            _botOwnerRef = new(player.AIData.BotOwner);
            BotOwner.SetMcsBotPlayerData(this);
            _mcsAILeadPlayerRef = new(mcsAILeadPlayer);
            _leadPlayeRef = new(bossPlayer);
            CollectVanishingCurseLootItems();
            if (mcsAILeadPlayer.McsBotPlayerConfig.EnableKeepFormation)
            {
                AddIntent(Intents.ShouldKeepFormation);
            }
            else
            {
                RemoveIntent(Intents.ShouldKeepFormation);
            }

            GUIStyle = new GUIStyle(Draw.GuiCommonStyle);

            var role = player.Profile.Info.Settings.Role;
            var stringBuilder = new StringBuilder();
            if (player.Profile.Side == EPlayerSide.Savage)
            {
                stringBuilder.Append(Tools.IsBoss(role) ? "BOSS/" : "");
                stringBuilder.Append(Tools.GetTypeName(role));
            }
            else
            {
                stringBuilder.Append(player.Profile.Side);
            }
            TeamInfo = stringBuilder.ToString();
        }

        public void CollectVanishingCurseLootItems()
        {
            if (_vanishingCurseLootItems == null)
            {
                _vanishingCurseLootItems = new();
            }

            var slots = InventoryEquipment.AllSlotNames
                .Where(slotName => slotName is not EquipmentSlot.Dogtag)
                .Select(BotOwner.Profile.Inventory.Equipment.GetSlot).ToArray();

            foreach (var slot in slots)
            {
                if (slot.ContainedItem == null)
                {
                    continue;
                }

                var allItems = slot.ContainedItem.GetAllItems();
                foreach (var item in allItems)
                {
                    var itemData = item.GetData();
                    if (itemData == null)
                    {
                        continue;
                    }

                    if (itemData is not LootData lootData)
                    {
                        continue;
                    }

                    if (lootData.ItemType is EItemType.Backpack or EItemType.Equipment)
                    {
                        continue;
                    }

                    if (lootData.VanishingCurse)
                    {
                        _vanishingCurseLootItems.Add(lootData);
                    }
                }
            }
        }

        public void SetLootingTarget(List<ItemData> itemDatas)
        {
            if (HasIntent(Intents.ShouldLootProxyAction))
            {
                return;
            }

            var emergencyLootNeed = GetEmergencyLootNeed();
            if (emergencyLootNeed != ELootNeedType.None)
            {
                if (TrySetEmergencyLootingTarget(itemDatas, emergencyLootNeed))
                {
                    return;
                }

                var botOwner = BotOwner;
                if (emergencyLootNeed != ELootNeedType.Ammo && botOwner != null && NeedAmmo(botOwner) && TrySetEmergencyLootingTarget(itemDatas, ELootNeedType.Ammo))
                {
                    return;
                }

                if (emergencyLootNeed != ELootNeedType.Meds && GetMissingMedEffects().Count > 0 && TrySetEmergencyLootingTarget(itemDatas, ELootNeedType.Meds))
                {
                    return;
                }

                return;
            }

            if (!McsAILeadPlayer.McsBotPlayerConfig.EnableLooting)
            {
                return;
            }

            var filtedLootDatas = new List<LootData>(itemDatas.Count);
            var usefulContainers = new List<LootData>();
            foreach (var itemData in itemDatas)
            {
                if (itemData == null)
                {
                    continue;
                }

                if (itemData is not LootData lootData)
                {
                    continue;
                }

                if (lootData.IsInSecureContainerItem)
                {
                    continue;
                }

                if (!lootData.LootProps.TryGetValue(McsAILeadPlayer, out var lootProp))
                {
                    continue;
                }

                if (lootProp.IsBlockItem)
                {
                    continue;
                }

                if (lootProp.IsLootOnColdown(BotOwner))
                {
                    continue;
                }

                if (lootProp.IsShouldTakeContainer(BotOwner) || lootProp.IsShouldEquipContainer(BotOwner) || lootProp.IsShouldSwapContainer(BotOwner) || lootProp.IsShouldNestContainer(BotOwner) is ENestType.In or ENestType.Out)
                {
                    usefulContainers.Add(lootData);
                    continue;
                }

                if (!lootProp.IsHighPriceItem && (!McsAILeadPlayer.McsBotPlayerConfig.LootingKeywordItem || !lootProp.IsKeywordItem))
                {
                    continue;
                }

                filtedLootDatas.Add(lootData);
            }

            usefulContainers.Sort((a, b) => b.ContainerGridCount.CompareTo(a.ContainerGridCount));
            foreach (var containerData in usefulContainers)
            {
                if (LootDataMgr.IsLockedLootingTarget(containerData))
                {
                    continue;
                }

                if (LootDataMgr.IsLockedLootingTargetRootTransform(containerData.RootTransform))
                {
                    continue;
                }

                LootDataMgr.LockLootItemToTarget(containerData);
                LootDataMgr.LockLootingTargetRootTransform(containerData.RootTransform);
                LootingTarget = containerData;
                return;
            }

            filtedLootDatas.Sort((a, b) => b.Offer.Price.CompareTo(a.Offer.Price));
            foreach (var lootData in filtedLootDatas)
            {
                if (LootDataMgr.IsLockedLootingTarget(lootData))
                {
                    continue;
                }

                if (LootDataMgr.IsLockedLootingTargetRootTransform(lootData.RootTransform))
                {
                    continue;
                }

                LootDataMgr.LockLootItemToTarget(lootData);
                LootDataMgr.LockLootingTargetRootTransform(lootData.RootTransform);
                LootingTarget = lootData;
                return;
            }
        }

        public void SetInfo()
        {
            var fontSize = GUIStyle.fontSize;
            if (!ReferenceEquals(_measuredInfo, Info) || _measuredFontSize != fontSize)
            {
                _measuredInfo = Info;
                _measuredFontSize = fontSize;
                _measureContent.text = Info;
                _measuredGuiSize = GUIStyle.CalcSize(_measureContent);
            }

            Rect = new Rect(new Vector2(BottomScreenPos.x - (_measuredGuiSize.x / 2), BottomScreenPos.y), _measuredGuiSize);
        }

        public void SyncFontSize()
        {
            GUIStyle.fontSize = Draw.GuiCommonStyle.fontSize;
        }

        public Vector2 GetBottomScreenPos()
        {
            try
            {
                BottomScreenPos = WorldPointToVisibleScreenPoint(Player.Position);
                return BottomScreenPos;
            }
            catch
            {
                BottomScreenPos = new Vector2(-10000, -10000);
                return BottomScreenPos;
            }
        }

        public bool IsInCameraView()
        {
            var pos = GetBottomScreenPos();
            return pos.x != -10000 && pos.y != -10000;
        }

        public Vector2 WorldPointToVisibleScreenPoint(Vector3 worldPoint)
        {
            var mainCamera = Gameloop.MainCamera;
            var opticCamera = Gameloop.OpticCamera;

            if (!mainCamera)
            {
                return new Vector2(-10000, -10000);
            }

            var screenWidth = Screen.width;
            var screenHeight = Screen.height;
            Vector3 screenPoint;

            var isOpticAiming = Gameloop.OpticCamera != null && Gameloop.OpticCamera.gameObject.activeSelf;

            if (Gameloop.IsAiming && isOpticAiming)
            {
                if (!opticCamera)
                {
                    return new Vector2(-10000, -10000);
                }

                screenPoint = opticCamera.WorldToScreenPoint(worldPoint);

                var scopeSize = Mathf.Min(screenWidth, screenHeight) * 0.6766f;
                var scopeX = (screenWidth - scopeSize) * 0.5f;
                var scopeY = (screenHeight - scopeSize) * 0.5f;

                var uvX = screenPoint.x / opticCamera.pixelWidth;
                var uvY = screenPoint.y / opticCamera.pixelHeight;

                screenPoint.x = scopeX + uvX * scopeSize;
                screenPoint.y = scopeY + (1 - uvY) * scopeSize;
            }
            else
            {
                screenPoint = mainCamera.WorldToScreenPoint(worldPoint);
                var scale = screenHeight / (float)mainCamera.scaledPixelHeight;
                screenPoint.x = screenPoint.x * scale;
                screenPoint.y = screenHeight - screenPoint.y * scale;
            }

            if (screenPoint.z <= 0.01f)
            {
                return new Vector2(-10000, -10000);
            }
            else if (screenPoint.x < -5f || screenPoint.x > screenWidth + 5f)
            {
                return new Vector2(-10000, -10000);
            }
            else if (screenPoint.y < -5f || screenPoint.y > screenHeight + 5f)
            {
                return new Vector2(-10000, -10000);
            }

            return screenPoint;
        }

        public void UpdateBaseInfo()
        {
            var player = Player;
            if (player == null)
            {
                return;
            }

            var isAlive = player.HealthController.IsAlive;
            BaseInfoBuilder.Clear();

            BaseInfoBuilder.Append(player.Profile.McsNickname);
            BaseInfoBuilder.Append('(').Append(TeamInfo).Append(')');
            BaseInfoBuilder.Append('\n');

            if (isAlive)
            {
                var hp = player.HealthController.GetBodyPartHealth(EBodyPart.Common, true).Current;
                var hpmax = player.HealthController.GetBodyPartHealth(EBodyPart.Common, true).Maximum;
                BaseInfoBuilder.Append(hp).Append('/').Append(hpmax).Append(' ');
            }

            BaseInfoBuilder.Append('[').Append(Distance).Append("]M");

            try
            {
                var botOwner = player.AIData?.BotOwner;
                if (botOwner?.Memory?.GoalEnemy != null)
                {
                    BaseInfoBuilder.Append('\n').Append("GoalEnemy: ").Append(botOwner.Memory.GoalEnemy.Person.Profile.Nickname);
                    BaseInfoBuilder.Append('\n').Append("GoalEnemyDist: [").Append(botOwner.Memory.GoalEnemy.Distance).Append("] M");
                }

                var brain = botOwner?.Brain;
                if (brain?.BaseBrain != null)
                {
                    BaseInfoBuilder.Append('\n').Append("Brain: ").Append(brain.BaseBrain.ShortName());
                    BaseInfoBuilder.Append('\n').Append("Layer: ").Append(brain.ActiveLayerName());
                    BaseInfoBuilder.Append('\n').Append("EnterBy: ").Append(brain.GetActiveNodeReason());
                }
                BaseInfoBuilder.Append('\n').Append(C2Sain.ForkDebug.Describe(this));
            }
            catch
            {

            }

            BaseInfo = BaseInfoBuilder.ToString();
            UpdateWeaponInfo(isAlive);
            UpdateEffectsInfo(isAlive);
            UpdateSuppliesInfo(isAlive);
        }

        private void UpdateWeaponInfo(bool isAlive)
        {
            if (!isAlive)
            {
                WeaponInfo = "";
                return;
            }

            var player = Player;
            if (player?.HandsController?.Item is Weapon weapon)
            {
                WeaponInfoBuilder.Clear();
                var magazine = weapon.GetCurrentMagazine();
                if (magazine != null)
                {
                    WeaponInfoBuilder.Append('\n')
                                .Append(weapon.ChamberAmmoCount)
                                .Append('+')
                                .Append(magazine.Count)
                                .Append('/')
                                .Append(magazine.MaxCount)
                                .Append(' ');
                }
                else
                {
                    WeaponInfoBuilder.Append('\n')
                                .Append(weapon.ChamberAmmoCount)
                                .Append(' ');
                }

                var weaponName = weapon.ShortName.McsLocalized();
                if (weaponName == "")
                {
                    weaponName = weapon.Name.McsLocalized();
                }

                WeaponInfoBuilder.Append(weaponName)
                            .Append(' ')
                            .Append(weapon.SelectedFireMode.ToString().McsLocalized())
                            .Append(' ')
                            .Append(weapon.WeapClass.McsLocalized());

                WeaponInfo = WeaponInfoBuilder.ToString();
            }
            else
            {
                WeaponInfo = "";
            }
        }

        private void UpdateEffectsInfo(bool isAlive)
        {
            EffectsInfo = "";
            if (!isAlive)
            {
                return;
            }

            var player = Player;
            if (player?.HealthController == null)
            {
                return;
            }

            _effectEntries.Clear();
            try
            {
                foreach (var activeEffect in player.HealthController.GetAllActiveEffects())
                {
                    if (Classification.EffectTypeFilter.Contains(activeEffect.Type))
                    {
                        continue;
                    }

                    var effectType = HealthHelper.EffectName(activeEffect);
                    if (string.IsNullOrEmpty(effectType))
                    {
                        continue;
                    }

                    _effectEntries.Add(activeEffect.BodyPart.ToString().McsLocalized() + " " + effectType.McsLocalized());
                }
            }
            catch
            {

            }

            if (_effectEntries.Count == 0)
            {
                return;
            }

            EffectsInfoBuilder.Clear();
            EffectsInfoBuilder.Append("\nEffects: ");
            for (int i = 0; i < _effectEntries.Count; i++)
            {
                if (i > 0)
                {
                    EffectsInfoBuilder.Append(i % 2 == 0 ? ",\n" : ", ");
                }
                EffectsInfoBuilder.Append(_effectEntries[i]);
            }

            EffectsInfo = EffectsInfoBuilder.ToString();
        }

        private void UpdateSuppliesInfo(bool isAlive)
        {
            SuppliesInfo = "";
            if (!isAlive)
            {
                return;
            }

            var player = Player;
            if (player?.InventoryController == null)
            {
                return;
            }

            SuppliesInfoBuilder.Clear();

            if (player.HandsController?.Item is Weapon currentWeapon
                && CollectWeaponAmmoAndMagCount(currentWeapon, out var ammoCount, out var magCount))
            {
                SuppliesInfoBuilder.Append("\nAmmo: ").Append(ammoCount).Append(" Mag: ").Append(magCount);
            }

            CountMedSupplies(out var firstAidCount, out var surgicalKitCount, out var splintCount);
            SuppliesInfoBuilder.Append("\nFirstAid: ").Append(firstAidCount)
                        .Append(" Splint: ").Append(splintCount)
                        .Append("\nSurgicalKit: ").Append(surgicalKitCount);

            SuppliesInfo = SuppliesInfoBuilder.ToString();
        }

        public void UpdateData()
        {
            try
            {
                GetBottomScreenPos();
                if (Player == null)
                {
                    return;
                }

                Info = BaseInfo + WeaponInfo + EffectsInfo + SuppliesInfo;
                SetInfo();
            }
            catch
            {

            }
        }

        public override void Dispose()
        {
            base.Dispose();
            _botOwnerRef = null;
            _leadPlayeRef = null;
            _mcsAILeadPlayerRef = null;
            _enemyVelocityKey = null;
            _enemyVelocityPosition = Vector3.zero;
            _enemyVelocityTime = 0f;
            _enemyVelocity = Vector3.zero;
            IsLooting = false;
            LootingTarget = null;
        }

        public void HandleBalanceRestriction()
        {
            foreach (var lootData in _vanishingCurseLootItems)
            {
                var item = lootData.Item;
                if (item?.CurrentAddress == null || !lootData.VanishingCurse)
                {
                    continue;
                }

                if (item.CurrentAddress.Container is Slot slot && slot.ContainedItem == item && Enum.TryParse<EquipmentSlot>(slot.ID, out var equipmentSlot))
                {
                    if (equipmentSlot is EquipmentSlot.Backpack or EquipmentSlot.TacticalVest or EquipmentSlot.Pockets)
                    {
                        continue;
                    }

                    var parentItem = item.Parent.GetRootItem();
                    var itemData = parentItem.GetData();
                    if (itemData is PlayerData playerData)
                    {
                        if ((equipmentSlot is EquipmentSlot.FirstPrimaryWeapon && playerData.Player.HandsController.Item == item)
                        || (equipmentSlot is EquipmentSlot.SecondPrimaryWeapon && playerData.Player.HandsController.Item == item)
                        || (equipmentSlot is EquipmentSlot.Holster && playerData.Player.HandsController.Item == item)
                        || (equipmentSlot is EquipmentSlot.Scabbard && playerData.Player.HandsController.Item == item))
                        {
                            continue;
                        }
                    }

                    slot.RemoveItemWithoutRestrictions();
                }
                else
                {
                    item.McsRemoveItem();
                }
            }
        }

        public ELootNeedType GetEmergencyLootNeed()
        {
            if (Time.time < _nextEmergencyNeedCheckTime)
            {
                return _cachedEmergencyLootNeed;
            }

            _nextEmergencyNeedCheckTime = Time.time + EMERGENCY_NEED_CHECK_INTERVAL;
            _cachedEmergencyLootNeed = ELootNeedType.None;

            var botOwner = BotOwner;
            var player = Player;
            if (botOwner == null || player == null)
            {
                return _cachedEmergencyLootNeed;
            }

            var weaponManager = botOwner.WeaponManager;
            if (weaponManager == null || player.InventoryController == null)
            {
                return _cachedEmergencyLootNeed;
            }

            if (NeedMagazine(weaponManager.CurrentWeapon))
            {
                _cachedEmergencyLootNeed = ELootNeedType.Magazine;
                return _cachedEmergencyLootNeed;
            }

            if (NeedAmmo(botOwner))
            {
                _cachedEmergencyLootNeed = ELootNeedType.Ammo;
                return _cachedEmergencyLootNeed;
            }

            if (GetMissingMedEffects().Count > 0)
            {
                _cachedEmergencyLootNeed = ELootNeedType.Meds;
                return _cachedEmergencyLootNeed;
            }

            return _cachedEmergencyLootNeed;
        }

        public bool HasEmergencyLootNeed()
        {
            return GetEmergencyLootNeed() != ELootNeedType.None;
        }

        private bool NeedMagazine(Weapon weapon)
        {
            if (weapon == null || weapon.GetMagazineSlot() == null)
            {
                return false;
            }

            if (weapon.ReloadMode == Weapon.EReloadMode.InternalMagazine)
            {
                return false;
            }

            CollectWeaponAmmoAndMagCount(weapon, out _, out var magCount);
            return magCount < REQUIRED_MAG_COUNT;
        }

        private bool NeedAmmo(BotOwner botOwner)
        {
            var totalAmmo = CollectAllAmmoCount(botOwner);
            if (totalAmmo <= 0)
            {
                return true;
            }

            var weapon = botOwner.WeaponManager?.CurrentWeapon;
            if (weapon == null)
            {
                return false;
            }

            var magCapacity = GetMagCapacity(weapon, botOwner);
            if (magCapacity <= 0)
            {
                return false;
            }

            return totalAmmo < magCapacity * AMMO_SUFFICIENT_MULTIPLIER;
        }

        private int CollectAllAmmoCount(BotOwner botOwner)
        {
            var total = 0;
            var player = botOwner.GetPlayer;
            if (player == null)
            {
                return total;
            }

            var inventoryController = player.InventoryController;

            var allAmmoList = new List<Ammo>();
            inventoryController.GetAcceptableItemsNonAlloc(BotReload._availableEquipmentSlots, allAmmoList, null, null);
            foreach (var ammo in allAmmoList)
            {
                if (ammo != null && ammo.StackObjectsCount > 0)
                {
                    total += ammo.StackObjectsCount;
                }
            }

            var allMagList = new List<Magazine>();
            inventoryController.GetAcceptableItemsNonAlloc(BotReload._availableEquipmentSlots, allMagList, null, null);
            foreach (var magazine in allMagList)
            {
                if (magazine != null)
                {
                    total += magazine.Count;
                }
            }

            var equipment = inventoryController.Inventory.Equipment;
            foreach (var slot in new[] { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster })
            {
                if (equipment.GetSlot(slot).ContainedItem is Weapon weapon)
                {
                    total += weapon.GetCurrentMagazine()?.Count ?? 0;
                    total += weapon.ChamberAmmoCount;
                }
            }

            return total;
        }

        private int GetMagCapacity(Weapon weapon, BotOwner botOwner)
        {
            var currentMagazine = weapon.GetCurrentMagazine();
            if (currentMagazine != null && currentMagazine.MaxCount > 0)
            {
                return currentMagazine.MaxCount;
            }

            var magazineSlot = weapon.GetMagazineSlot();
            if (magazineSlot != null)
            {
                var player = botOwner.GetPlayer;
                if (player != null)
                {
                    var allMagList = new List<Magazine>();
                    player.InventoryController.GetAcceptableItemsNonAlloc(BotReload._availableEquipmentSlots, allMagList, null, null);
                    var maxCapacity = 0;
                    foreach (var magazine in allMagList)
                    {
                        if (magazine != null && magazineSlot.CanAccept(magazine) && magazine.MaxCount > maxCapacity)
                        {
                            maxCapacity = magazine.MaxCount;
                        }
                    }

                    if (maxCapacity > 0)
                    {
                        return maxCapacity;
                    }
                }
            }

            return DEFAULT_MAG_CAPACITY;
        }

        public bool CollectCurrentWeaponAmmoAndMagCount(out int ammoCount, out int magCount)
        {
            ammoCount = 0;
            magCount = 0;

            var player = Player;
            if (player?.HandsController?.Item is not Weapon weapon)
            {
                return false;
            }

            return CollectWeaponAmmoAndMagCount(weapon, out ammoCount, out magCount);
        }

        public bool CollectWeaponAmmoAndMagCount(Weapon weapon, out int ammoCount, out int magCount)
        {
            ammoCount = 0;
            magCount = 0;

            var player = Player;
            var inventoryController = player?.InventoryController;
            if (weapon == null || inventoryController == null)
            {
                return false;
            }

            ammoCount += weapon.ChamberAmmoCount;

            var magazineSlot = weapon.GetMagazineSlot();
            if (magazineSlot != null)
            {
                var currentMagazine = weapon.GetCurrentMagazine();
                if (currentMagazine != null)
                {
                    magCount++;
                    ammoCount += currentMagazine.Count;
                }

                var magazineList = new List<Magazine>();
                inventoryController.GetAcceptableItemsNonAlloc(BotReload._availableEquipmentSlots, magazineList, null, null);
                foreach (var magazine in magazineList)
                {
                    if (magazine != null && magazineSlot.CanAccept(magazine))
                    {
                        magCount++;
                        ammoCount += magazine.Count;
                    }
                }
            }

            var ammoList = new List<Ammo>();
            inventoryController.GetAcceptableItemsNonAlloc(BotReload._availableEquipmentSlots, ammoList, null, null);
            foreach (var ammo in ammoList)
            {
                if (ammo != null && ammo.StackObjectsCount > 0 && IsAmmoCompatible(ammo, weapon))
                {
                    ammoCount += ammo.StackObjectsCount;
                }
            }

            return true;
        }

        public void CountMedSupplies(out int firstAidCount, out int surgicalKitCount, out int splintCount)
        {
            firstAidCount = 0;
            surgicalKitCount = 0;
            splintCount = 0;

            var player = Player;
            if (player?.InventoryController == null)
            {
                return;
            }

            var medsList = new List<Meds>();
            player.InventoryController.GetAcceptableItemsNonAlloc(BotMedecine.anySlots, medsList);
            foreach (var meds in medsList)
            {
                if (meds == null)
                {
                    continue;
                }

                var healthEffectsComponent = meds.HealthEffectsComponent;
                if (healthEffectsComponent == null)
                {
                    continue;
                }

                var damageEffects = healthEffectsComponent.DamageEffects;
                if (damageEffects == null)
                {
                    continue;
                }

                if (damageEffects.ContainsKey(EDamageEffectType.DestroyedPart))
                {
                    surgicalKitCount++;
                    continue;
                }

                if (damageEffects.ContainsKey(EDamageEffectType.HeavyBleeding)
                    || damageEffects.ContainsKey(EDamageEffectType.LightBleeding)
                    || meds is MedKit
                    || (healthEffectsComponent.HealthEffects != null && healthEffectsComponent.HealthEffects.ContainsKey(EHealthFactorType.Health)))
                {
                    firstAidCount++;
                    continue;
                }

                if (damageEffects.ContainsKey(EDamageEffectType.Fracture))
                {
                    splintCount++;
                }
            }
        }

        public HashSet<EDamageEffectType> GetMissingMedEffects()
        {
            _missingMedEffects.Clear();
            _missingMedEffects.Add(EDamageEffectType.Fracture);
            _missingMedEffects.Add(EDamageEffectType.HeavyBleeding);
            _missingMedEffects.Add(EDamageEffectType.LightBleeding);
            _missingMedEffects.Add(EDamageEffectType.DestroyedPart);

            var player = Player;
            if (player == null)
            {
                return _missingMedEffects;
            }

            var medsList = new List<Meds>();
            player.InventoryController.GetAcceptableItemsNonAlloc(BotMedecine.anySlots, medsList);
            foreach (var meds in medsList)
            {
                if (meds == null)
                {
                    continue;
                }

                var damageEffects = meds.HealthEffectsComponent?.DamageEffects;
                if (damageEffects == null)
                {
                    continue;
                }

                if (damageEffects.ContainsKey(EDamageEffectType.Fracture) && !damageEffects.ContainsKey(EDamageEffectType.DestroyedPart))
                {
                    _missingMedEffects.Remove(EDamageEffectType.Fracture);
                }

                if (damageEffects.ContainsKey(EDamageEffectType.HeavyBleeding))
                {
                    _missingMedEffects.Remove(EDamageEffectType.HeavyBleeding);
                }

                if (damageEffects.ContainsKey(EDamageEffectType.LightBleeding))
                {
                    _missingMedEffects.Remove(EDamageEffectType.LightBleeding);
                }

                if (damageEffects.ContainsKey(EDamageEffectType.DestroyedPart))
                {
                    _missingMedEffects.Remove(EDamageEffectType.DestroyedPart);
                }
            }

            return _missingMedEffects;
        }

        private bool TrySetEmergencyLootingTarget(List<ItemData> itemDatas, ELootNeedType needType)
        {
            if (itemDatas == null)
            {
                return false;
            }

            var botOwner = BotOwner;
            if (botOwner == null)
            {
                return false;
            }

            var weapon = botOwner.WeaponManager?.CurrentWeapon;
            if (weapon == null && needType != ELootNeedType.Meds)
            {
                return false;
            }

            var missingMedEffects = needType == ELootNeedType.Meds ? GetMissingMedEffects() : null;
            var candidateLootDatas = new List<LootData>();
            var coveredMedEffectCounts = new Dictionary<LootData, int>();

            foreach (var itemData in itemDatas)
            {
                if (itemData is not LootData lootData)
                {
                    continue;
                }

                if (lootData.IsInSecureContainerItem)
                {
                    continue;
                }

                if (lootData.RootTransform == null)
                {
                    continue;
                }

                if (LootDataMgr.IsLockedLootingTarget(lootData))
                {
                    continue;
                }

                if (LootDataMgr.IsLockedLootingTargetRootTransform(lootData.RootTransform))
                {
                    continue;
                }

                if (!lootData.LootProps.TryGetValue(McsAILeadPlayer, out var lootProp))
                {
                    continue;
                }

                if (lootProp.IsLootOnColdown(botOwner))
                {
                    continue;
                }

                switch (needType)
                {
                    case ELootNeedType.Magazine:
                        if (IsCompatibleMagazine(lootData, weapon))
                        {
                            candidateLootDatas.Add(lootData);
                        }
                        break;
                    case ELootNeedType.Ammo:
                        if (IsCompatibleAmmo(lootData, weapon))
                        {
                            candidateLootDatas.Add(lootData);
                        }
                        break;
                    case ELootNeedType.Meds:
                        {
                            var coveredCount = GetCoveredMissingMedEffectCount(lootData, missingMedEffects);
                            if (coveredCount > 0)
                            {
                                candidateLootDatas.Add(lootData);
                                coveredMedEffectCounts[lootData] = coveredCount;
                            }
                            break;
                        }
                }
            }

            if (candidateLootDatas.Count == 0)
            {
                return false;
            }

            var botPos = botOwner.Position;
            if (needType == ELootNeedType.Meds)
            {
                candidateLootDatas.Sort((a, b) =>
                {
                    var countCompare = coveredMedEffectCounts[b].CompareTo(coveredMedEffectCounts[a]);
                    if (countCompare != 0)
                    {
                        return countCompare;
                    }

                    return a.RootTransform.position.McsSqrDistance(botPos).CompareTo(b.RootTransform.position.McsSqrDistance(botPos));
                });
            }
            else if (needType == ELootNeedType.Magazine)
            {
                candidateLootDatas.Sort((a, b) =>
                {
                    var aHasAmmo = (a.Item as Magazine)?.Count > 0;
                    var bHasAmmo = (b.Item as Magazine)?.Count > 0;
                    if (aHasAmmo != bHasAmmo)
                    {
                        return aHasAmmo ? -1 : 1;
                    }

                    return a.RootTransform.position.McsSqrDistance(botPos).CompareTo(b.RootTransform.position.McsSqrDistance(botPos));
                });
            }
            else
            {
                candidateLootDatas.Sort((a, b) => a.RootTransform.position.McsSqrDistance(botPos).CompareTo(b.RootTransform.position.McsSqrDistance(botPos)));
            }

            var targetLootData = candidateLootDatas.FirstOrDefault();
            LootDataMgr.LockLootItemToTarget(targetLootData);
            LootDataMgr.LockLootingTargetRootTransform(targetLootData.RootTransform);
            LootingTarget = targetLootData;
            return true;
        }

        private bool IsCompatibleMagazine(LootData lootData, Weapon weapon)
        {
            if (lootData.Item is not Magazine magazine)
            {
                return false;
            }

            var magazineSlot = weapon.GetMagazineSlot();
            if (magazineSlot == null)
            {
                return false;
            }

            return magazineSlot.CanAccept(magazine);
        }

        private bool IsCompatibleAmmo(LootData lootData, Weapon weapon)
        {
            if (lootData.Item is not Ammo ammo)
            {
                return false;
            }

            return IsAmmoCompatible(ammo, weapon);
        }

        private bool IsAmmoCompatible(Ammo ammo, Weapon weapon)
        {
            if (weapon.Chambers != null)
            {
                foreach (var chamber in weapon.Chambers)
                {
                    if (chamber != null && chamber.CanAccept(ammo))
                    {
                        return true;
                    }
                }
            }

            var currentMagazine = weapon.GetCurrentMagazine();
            if (currentMagazine?.Cartridges?.Filters != null && currentMagazine.Cartridges.Filters.CheckItemFilter(ammo))
            {
                return true;
            }

            return false;
        }

        private int GetCoveredMissingMedEffectCount(LootData lootData, HashSet<EDamageEffectType> missingMedEffects)
        {
            if (lootData.Item is not Meds meds)
            {
                return 0;
            }

            var damageEffects = meds.HealthEffectsComponent?.DamageEffects;
            if (damageEffects == null)
            {
                return 0;
            }

            var coveredCount = 0;
            foreach (var missingEffect in missingMedEffects)
            {
                switch (missingEffect)
                {
                    case EDamageEffectType.Fracture:
                        if (damageEffects.ContainsKey(EDamageEffectType.Fracture) && !damageEffects.ContainsKey(EDamageEffectType.DestroyedPart))
                        {
                            coveredCount++;
                        }
                        break;
                    default:
                        if (damageEffects.ContainsKey(missingEffect))
                        {
                            coveredCount++;
                        }
                        break;
                }
            }

            return coveredCount;
        }
    }
}