using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using System.Collections.Generic;
using System;
using MiyakoCarryService.Client.Enums;
using MiyakoCarryService.Client.Utils;
using MiyakoCarryService.Client.Patches.RefreshQuests;
using MiyakoCarryService.Client.Patches.Raid;
using MiyakoCarryService.Client.Patches.Bots;
using MiyakoCarryService.Client.Patches.Group;
using MiyakoCarryService.Client.Patches.BepInEx;
using MiyakoCarryService.Client.Patches.Events;
using System.Text.RegularExpressions;
using MiyakoCarryService.Client.Patches.BigSurvey;
using MiyakoCarryService.Client.Extensions;
using BepInEx.Bootstrap;
using MiyakoCarryService.Client.Models;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Events;
using MiyakoCarryService.Client.Patches.Inventory;
using MiyakoCarryService.Client.Patches.SAIN;
using System.Reflection;
using SPT.Reflection.Patching;
using MiyakoCarryService.Client.Patches.Interactive;
using EFT;
using EFT.Communications;

namespace MiyakoCarryService.Client
{
    [BepInPlugin(McsGUID, McsPluginName, BepInExClientVersion)]
    [BepInProcess(EFTapp)]
    [BepInDependency(BigBrainGUID, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("me.sol.sain", BepInDependency.DependencyFlags.HardDependency)] // Fork: SAIN is the combat brain
    public sealed class MiyakoCarryServicePlugin : BaseUnityPlugin
    {
        public const string BepInExClientVersion = "1.1.4.100"; // Fork: upstream 1.1.4.0 + C2/SAIN fork (C2Sain/ForkInfo.cs)
        public static System.Version ClientVersion { get; } = new(BepInExClientVersion);
        public const string EFTapp = "EscapeFromTarkov.exe";
        public const string McsGUID = "top.himesamanoyume.miyakocarryservice";
        public const string FikaGUID = "com.fika.core";
        public const string BigBrainGUID = "xyz.drakia.bigbrain";
#if DEBUG
        public const string McsPluginName = "姫様の夢 MiyakoCarryService DebugBuild";
#else
        public const string McsPluginName = "姫様の夢 MiyakoCarryService";
#endif
        public const string MiyakoTraderId = "6952ced4bcc1dd1e3c80dfcb";
        public static MiyakoCarryServicePlugin Instance;
        public static McsPluginClientConfig McsPluginClientConfig = null;
        private List<ModulePatch> _patches = new();
        public static bool IsLoadedByScriptEngine = false;
        public static bool FikaInstalled { get; private set; } = false;
        public static bool IsFikaHeadless { get; private set; } = false;
        public static bool SAINInstalled { get; private set; } = false;
        private Regex _stackRegex = new(@"\s*\(at <[^>]+>:\d+\)", RegexOptions.Compiled);
        internal static LogBuffer LogBuffer = new LogBuffer();
        private Debouncer<string, McsBotPlayerConfig> _configDebouncer;

        #region BASIC

        public static ConfigEntry<bool> EnableLooting;
        public static ConfigEntry<KeyboardShortcut> EnableLootingHotKey;
        public static ConfigEntry<int> PriceThreshold;
        public static ConfigEntry<string> KeywordItemText;
        public static ConfigEntry<bool> LootingKeywordItem;
        public static ConfigEntry<EBlockItemType> BlockItemType;
        public static ConfigEntry<bool> EnableKeepFormation;
        public static ConfigEntry<KeyboardShortcut> EnableKeepFormationHotKey;
        public static ConfigEntry<string> FormationMatrix;
        public static ConfigEntry<float> FormationSpacing;
        public static ConfigEntry<bool> FormationSequentialFill;
        public static ConfigEntry<KeyboardShortcut> SaveFormationPresetHotKey;
        public static ConfigEntry<string> FormationPresets;
        public static ConfigEntry<bool> PhrasesSilent;
        public static ConfigEntry<bool> EnableSubtitles;

        #endregion

        #region COMMAND

        public static ConfigEntry<KeyboardShortcut> CommandHotKey;

        #endregion

        #region PLAYER

        public static ConfigEntry<bool> TeammateHighlight;
        public static ConfigEntry<KeyboardShortcut> TeammateHighlightHotKey;
        public static ConfigEntry<Color> TeammateHighlightColor;
        public static ConfigEntry<bool> ShowBrevityCode;

        #endregion

        #region DEBUG

        public static ConfigEntry<bool> DrawDebugInfo;
        public static ConfigEntry<int> DebugTextSize;

#if DEBUG
        public static ConfigEntry<bool> McsBotPlayerNoDamage;
        public static ConfigEntry<bool> McsBotPlayerKeepAlive;
        public static ConfigEntry<bool> EnableMcsLayer;
#endif

        #endregion

        void Awake()
        {
            Instance = this;
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(assemblyLocation))
            {
                IsLoadedByScriptEngine = true;
            }
        }

        void Start()
        {
            Application.logMessageReceived += OnLog;
            FikaInstalled = !CheckPlugin([FikaGUID]);
            IsFikaHeadless = !CheckPlugin(["com.fika.headless"]);
            SAINInstalled = !CheckPlugin(["me.sol.sain"]);
            SetupConfig();
            McsPluginClientConfig = McsRequestHandler.GetMcsPluginClientConfig();
            DefaultLang = LocalizationManager.Instance.Culture;
            foreach (var kvp in LocalLocales.LoadingLocales)
            {
                LocalizationManager.Instance.UpdateLocales(kvp.Key, kvp.Value);
            }
            EnableAllPatches();
            McsLogger.LogInfo($"[{C2Sain.ForkInfo.Label}] loaded: SAIN fights, MCS commands");
            GameLoop.Instance.Init();
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (LogBuffer == null)
            {
                return;
            }

            if (condition.Contains("GUI Error: You are pushing more GUIClips than you are popping. Make sure they are balanced."))
            {
                return;
            }

            if (type is LogType.Exception or LogType.Error)
            {
                LogBuffer.AddEntryIfNotFull(condition, _stackRegex.Replace(stackTrace, ""));
            }
        }

        public bool CheckPlugin(List<string> pluginList)
        {
            var pluginInfos = new List<PluginInfo>(Chainloader.PluginInfos.Values);

            foreach (PluginInfo Info in pluginInfos)
            {
                if (pluginList.Contains(Info.Metadata.GUID))
                {
                    return false;
                }
            }
            return true;
        }

        public static string DefaultLang = "en";

        public bool CheckUnsupportedPlugin()
        {
            return CheckPlugin([]);
        }

        private void EnableAllPatches()
        {
            _patches.Add(new ContainsSearchStringPatch());
            _patches.Add(new DrawSinglePluginPatch());
            _patches.Add(new DrawFlagsFieldPatch());
            _patches.Add(new TraderScreensGroupShowPatch());
            _patches.Add(new RaidSettingsLocalPatch());
            _patches.Add(new MainMenuControllerClassPatch());
            _patches.Add(new MatchMakerAcceptScreenReadyPatch());
            _patches.Add(new BotsControllerInitPatch());
            _patches.Add(new AddEnemyPatch());
            _patches.Add(new MakingShotPatch());
            _patches.Add(new BotDoorOpenerUpdateStatusPatch());
            _patches.Add(new TraderClassConstructorPatch());
            _patches.Add(new TraderControllerClassConstructorPatch());
            _patches.Add(new TraderControllerClassAddItemEventInvokePatch());
            _patches.Add(new TraderControllerClassRemoveItemEventInvokePatch());
            _patches.Add(new TraderControllerClassOutProcessPatch());
            _patches.Add(new TraderControllerClassInProcessPatch());
            _patches.Add(new ApplyDamagePatch());
            _patches.Add(new OnGameStartedPatch());
            _patches.Add(new RaidEndedPatch());
            _patches.Add(new TransitPointPatch1());
            _patches.Add(new TransitPointPatch2());
            _patches.Add(new MatchmakerTimeHasComePatch());
            _patches.Add(new MatchMakerAcceptScreenCallbackPatch());
            _patches.Add(new GroupPlayerViewModelClassPatch());
            _patches.Add(new GetDailyQuestsPatch());
            _patches.Add(new MatchmakerAcceptScreenShowPatch());
            _patches.Add(new MatchingAbortPatch());
            _patches.Add(new DisbandRaidGroupPatch());
            _patches.Add(new MenuTaskBarAwakePatch());
            _patches.Add(new NewNewsCountPatch());
            _patches.Add(new SetGoalEnemyPatch());
            _patches.Add(new MessageReceivedHandlerPatch());
            _patches.Add(new LocalQuestControllerClassPatch());
            _patches.Add(new GetContextInteractionsPatch());
            _patches.Add(new ContextInteractionsClassPatch());
            _patches.Add(new GetProfilesPatch());
            _patches.Add(new MenuScreenPatch());
            _patches.Add(new TryFindChangedContainerPatch());
            _patches.Add(new CanModifyItemPatch());
            _patches.Add(new ItemSubtract1Patch());
            _patches.Add(new ItemSubtract2Patch());
            _patches.Add(new TryReloadPatch());
            _patches.Add(new BotWeaponSelectorPatch());
            _patches.Add(new AdvAssaultTargetPatch());
            _patches.Add(new InitVaultComponentPatch());
            _patches.Add(new DoVaultingTickPatch());
            _patches.Add(new VaultStateEnterPatch());
            _patches.Add(new VaultStateExitPatch());
            _patches.Add(new ClimbStateEnterPatch());
            _patches.Add(new ClimbStateExitPatch());
            _patches.Add(new VaultInteractDistancePatch());
            _patches.Add(new VaultAutoInteractDistancePatch());
            _patches.Add(new ClimbInteractDistancePatch());
            _patches.Add(new ClimbAutoInteractDistancePatch());
            _patches.Add(new BotMoverHopGuardPatch());
            _patches.Add(new BotMoverFixedUpdatePatch());
            _patches.Add(new NavBridgeObstaclePatch());
            _patches.Add(new CastFromPosPatch());
            _patches.Add(new FindBetterPositionPatch());
            _patches.Add(new SetPlayerToNavMeshPatch());
            _patches.Add(new PlayerTeleportPatch());
            _patches.Add(new MatchMakerSideSelectionScreenPatch());
            _patches.Add(new ActionPanelAnchorPatch());
            _patches.Add(new PartyInfoPanelScrollPatch());
            _patches.Add(new EquipmentBuildsScreenPatch());
            _patches.Add(new BotFirstAidClassMinPercentPatch());
            _patches.Add(new OnBeenKilledByAggressorPatch());
            _patches.Add(new TriggerWithIdEnterPatch());
            _patches.Add(new TriggerWithIdExitPatch());
            _patches.Add(new GetPartToShootPatch());
            _patches.Add(new IsAllowedPlayerPatch());
            _patches.Add(new DoorGetActionsClassPatch());
            _patches.Add(new LootItemGetActionsClassPatch());
            _patches.Add(new StationaryWeaponGetActionsClassPatch());
            _patches.Add(new RefreshMedsPatch());
            _patches.Add(new BotHearingSensorPatch());
            _patches.Add(new PlayerSayPatch());
            _patches.Add(new PlayHitEffectPatch());
            _patches.Add(new BotBewarePlantedMineUpdatePatch());
            _patches.Add(new AvailableInteractionStateSetterPatch());
            _patches.Add(new FindDamagedPartPatch());
            _patches.Add(new C2Sain.Patches.SainSetDecisionsPatch());
            _patches.Add(new C2Sain.Patches.SainFoliageSightPatch()); // Fork: carry bots see out of their bush.
            _patches.Add(new C2Sain.Patches.SainGunshotRangePatch()); // Fork v8: carry bots ignore distant gunshots.
            _patches.Add(new C2Sain.Patches.SainHeardPatch()); // Fork v8: a carry bot hearing an enemy alerts the squad.

            if (FikaInstalled)
            {
                if (IsLoadedByScriptEngine)
                {
                    _patches.Add(new PlayerOnDeadPatch());
                }
            }
            else
            {
                _patches.Add(new PlayerOnDeadPatch());
            }

            if (SAINInstalled)
            {
                _patches.Add(new SainPlayVoiceLinePatch());
            }

#if DEBUG

#endif

            foreach (var patch in _patches)
            {
                patch.Enable();
            }
        }

        private void OnDestroy()
        {
            foreach (var patch in _patches)
            {
                patch.Disable();
            }
            GameLoop.Instance.Destroy();
            Destroy(this);
        }

        private static readonly Dictionary<string, ConfigSection> _sections = new();
        public static readonly List<string> HideList = new();

        internal ConfigEntry<T> Register<T>(
            string sectionName,
            int order,
            string key,
            T defaultValue,
            string description = "",
            AcceptableValueBase acceptableValues = null,
            ConfigurationManagerAttributes customAttributes = null,
            bool needNotify = true,
            bool isHide = false
        )
        {
            if (!_sections.TryGetValue(sectionName, out var section))
            {
                section = new ConfigSection(sectionName, order);
                _sections[sectionName] = section;
            }

            var attributes = customAttributes ?? new ConfigurationManagerAttributes();

            if (isHide)
            {
                HideList.Add(key);
            }

            attributes.Order = section.GetNextOrder();

            var configDescription = new ConfigDescription(
                description,
                acceptableValues,
                attributes
            );

            var configEntry = Config.Bind(
                section.Name,
                key,
                defaultValue,
                configDescription
            );

            if (typeof(T) == typeof(bool) && needNotify)
            {
                configEntry.SettingChanged += (object sender, EventArgs e) =>
                {
                    var entry = (ConfigEntryBase)sender;
                    if ((bool)entry.BoxedValue)
                    {
                        NotificationManager.DisplayMessageNotification($"{configEntry.Definition.Key.McsLocalized()} <color=#00ff00>{Locales.FUNCTIONENABLED.McsLocalized()}</color>");
                    }
                    else
                    {
                        NotificationManager.DisplayMessageNotification($"{configEntry.Definition.Key.McsLocalized()} <color=#ff0000>{Locales.FUNCTIONDISABLED.McsLocalized()}</color>");
                    }
                };
            }

            if (sectionName == Locales.BASIC)
            {
                configEntry.SettingChanged += (object sender, EventArgs e) =>
                {
                    if (!GameLoop.Instance.IsVaildGameWorld)
                    {
                        return;
                    }

                    DebouncedConfigSync(new McsBotPlayerConfig
                    {
                        McsLeadPlayerId = GameLoop.Instance.Session.Profile.Id,
                        EnableLooting = EnableLooting.Value,
                        PriceThreshold = PriceThreshold.Value,
                        KeywordItemText = KeywordItemText.Value,
                        LootingKeywordItem = LootingKeywordItem.Value,
                        BlockItemType = (int)BlockItemType.Value,
                        FormationMatrix = FormationMatrix.Value,
                        EnableKeepFormation = EnableKeepFormation.Value,
                        FormationSpacing = FormationSpacing.Value,
                        FormationSequentialFill = FormationSequentialFill.Value,
                        PhrasesSilent = PhrasesSilent.Value,
                        EnableSubtitles = EnableSubtitles.Value,
                        Extensions = McsBotPlayerConfigUtils.Snapshot()
                    });
                };
            }

            return configEntry;
        }

        internal ConfigEntry<T> Register<T>(
            EConfigType type,
            string key,
            T defaultValue,
            string description = "",
            AcceptableValueBase acceptableValues = null,
            ConfigurationManagerAttributes customAttributes = null,
            bool needNotify = true,
            bool isHide = false
        )
        {
            var configTypeText = type switch
            {
                EConfigType.BASIC => Locales.BASIC,
                EConfigType.COMMAND => Locales.COMMAND,
                EConfigType.PLAYER => Locales.PLAYER,
                EConfigType.EXPERIMENTAL => Locales.EXPERIMENTAL,
                EConfigType.DEBUG or _ => Locales.DEBUG
            };
            return Register(configTypeText, (int)type, key, defaultValue, description, acceptableValues, customAttributes, needNotify, isHide);
        }

        internal void CustomDrawer<T>(ConfigEntryBase entry, Dictionary<T, string> dict, int xCount) where T : Enum
        {
            var value = (T)entry.BoxedValue;
            var values = Enum.GetValues(typeof(T));
            var options = new string[values.Length];
            var selectedIndex = 0;

            for (int i = 0; i < values.Length; i++)
            {
                var enumValue = (T)values.GetValue(i);
                options[i] = dict.ContainsKey(enumValue) ? dict[enumValue] : enumValue.ToString();
                if (enumValue.Equals(value))
                {
                    selectedIndex = i;
                }
            }

            var newIndex = GUILayout.SelectionGrid(selectedIndex, options, xCount);
            if (newIndex != selectedIndex)
            {
                entry.BoxedValue = values.GetValue(newIndex);
            }
        }

        private void SetupConfig()
        {
            C2Sain.ForkConfig.Bind(this);

            #region BASIC

            EnableLooting = Register(
                EConfigType.BASIC,
                Locales.ENABLELOOTING_KEY,
                false,
                Locales.ENABLELOOTING_DESCRIPTION
            );

            EnableLootingHotKey = Register(
                EConfigType.BASIC,
                Locales.ENABLELOOTINGHOTKEY_KEY,
                new KeyboardShortcut()
            );

            PriceThreshold = Register(
                EConfigType.BASIC,
                Locales.PRICETHRESHOLD_KEY,
                10000,
                Locales.PRICETHRESHOLD_DESCRIPTION,
                new AcceptableValueRange<int>(0, 1500000)
            );

            KeywordItemText = Register(
                EConfigType.BASIC,
                Locales.KEYWORDITEMTEXT_KEY,
                "",
                Locales.KEYWORDITEMTEXT_DESCRIPTION
            );

            LootingKeywordItem = Register(
                EConfigType.BASIC,
                Locales.LOOTINGKEYWORDITEM_KEY,
                true
            );

            BlockItemType = Register(
                EConfigType.BASIC,
                Locales.BLOCKITEMTYPE_KEY,
                (EBlockItemType)0
            );

            EnableKeepFormation = Register(
                EConfigType.BASIC,
                Locales.ENABLEKEEPFORMATION_KEY,
                false,
                Locales.ENABLEKEEPFORMATION_DESCRIPTION
            );

            EnableKeepFormationHotKey = Register(
                EConfigType.BASIC,
                Locales.ENABLEKEEPFORMATIONHOTKEY_KEY,
                new KeyboardShortcut()
            );

            FormationMatrix = Register(
                EConfigType.BASIC,
                Locales.FORMATIONMATRIX_KEY,
                Tools.ResetFormationMatrix(),
                Locales.FORMATIONMATRIX_DESCRIPTION,
                customAttributes: new ConfigurationManagerAttributes
                {
                    CustomDrawer = static entry =>
                    {
                        var oldFormationMatrix = (string)entry.BoxedValue;
                        var newFormationMatrix = Tools.DrawFormationMatrix(entry.Definition.Key, oldFormationMatrix);
                        if (newFormationMatrix != oldFormationMatrix)
                        {
                            entry.BoxedValue = newFormationMatrix;
                        }
                    }
                }
            );

            FormationSpacing = Register(
                EConfigType.BASIC,
                Locales.FORMATIONSPACING_KEY,
                3f,
                acceptableValues: new AcceptableValueRange<float>(2f, 6f)
            );

            FormationSequentialFill = Register(
                EConfigType.BASIC,
                Locales.FORMATIONSEQUENTIALFILL_KEY,
                false,
                Locales.FORMATIONSEQUENTIALFILL_DESCRIPTION
            );

            SaveFormationPresetHotKey = Register(
                EConfigType.BASIC,
                Locales.SAVEFORMATIONPRESETHOTKEY_KEY,
                new KeyboardShortcut()
            );

            PhrasesSilent = Register(
                EConfigType.BASIC,
                Locales.PHRASESSILENT_KEY,
                false,
                Locales.PHRASESSILENT_DESCRIPTION
            );

            EnableSubtitles = Register(
                EConfigType.BASIC,
                Locales.ENABLESUBTITLES_KEY,
                true
            );

            FormationPresets = Register(
                EConfigType.BASIC,
                "FormationPresets",
                "[]",
                isHide: true
            );

            #endregion
            #region COMMAND

            CommandHotKey = Register(
                EConfigType.COMMAND,
                Locales.COMMANDHOTKEY_KEY,
                new KeyboardShortcut()
            );

            #endregion
            #region PLAYER

            TeammateHighlight = Register(
                EConfigType.PLAYER,
                Locales.TEAMMATEHIGHLIGHT_KEY,
                false
            );

            TeammateHighlightHotKey = Register(
                EConfigType.PLAYER,
                Locales.TEAMMATEHIGHLIGHTHOTKEY_KEY,
                new KeyboardShortcut()
            );

            TeammateHighlightColor = Register(
                EConfigType.PLAYER,
                Locales.TEAMMATEHIGHLIGHTCOLOR_KEY,
                Draw.TranslucentTianyi.Rgb
            );

            ShowBrevityCode = Register(
                EConfigType.PLAYER,
                Locales.SHOWBREVITYCODE_KEY,
                true,
                Locales.SHOWBREVITYCODE_DESCRIPTION
            );

            #endregion
            #region DEBUG

            DrawDebugInfo = Register(
                EConfigType.DEBUG,
                Locales.DRAWDEBUGINFO_KEY,
                false
            );

            DebugTextSize = Register(
                EConfigType.DEBUG,
                Locales.DEBUGINFOTEXTSIZE_KEY,
                10,
                acceptableValues: new AcceptableValueRange<int>(5, 50)
            );

#if DEBUG
            McsBotPlayerNoDamage = Register(
                EConfigType.DEBUG,
                "护航免伤",
                true,
                customAttributes: new ConfigurationManagerAttributes() { IsAdvanced = true }
            );

            McsBotPlayerKeepAlive = Register(
                EConfigType.DEBUG,
                "护航锁血",
                true,
                customAttributes: new ConfigurationManagerAttributes() { IsAdvanced = true }
            );

            EnableMcsLayer = Register(
                EConfigType.DEBUG,
                "启用Mcs层级",
                true,
                customAttributes: new ConfigurationManagerAttributes() { IsAdvanced = true }
            );
#endif

            #endregion
        }

        public void DebouncedConfigSync(McsBotPlayerConfig mcsBotPlayerConfig)
        {
            if (_configDebouncer == null)
            {
                _configDebouncer = new Debouncer<string, McsBotPlayerConfig>(
                    this,
                    1f,
                    ExecuteConfigChanges
                );
            }

            if (_configDebouncer != null)
            {
                _configDebouncer.Trigger(McsGUID, mcsBotPlayerConfig);
            }
        }

        private void ExecuteConfigChanges(Dictionary<string, McsBotPlayerConfig> configs)
        {
            foreach (var kvp in configs)
            {
                try
                {
                    EventMgr.Notify(new ConfigEntrySettingChangedEvent
                    {
                        McsBotPlayerConfig = kvp.Value
                    });
                }
                catch (Exception e)
                {
                    McsLogger.LogError($"Batch refresh mcsBotPlayerConfig error: {e}");
                }
            }
        }
    }
}

