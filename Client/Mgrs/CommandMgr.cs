using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using UnityEngine;
using EFT;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Enums;
using MiyakoCarryService.Client.Events;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Models;
using MiyakoCarryService.Client.Utils;
using SPT.Common.Utils;
using System.Threading;
using System;
using System.Diagnostics;
using HarmonyLib;
using Diz.Utils;
using EFT.InventoryLogic;
using EFT.HealthSystem;

namespace MiyakoCarryService.Client.Mgrs
{
    public class CommandMgr : BaseMgr
    {
        public sealed override void Start()
        {
            base.Start();
            EventMgr.Subscribe<McsLeadPlayerExtractedEvent>(HandleMcsLeadPlayerExtracted, this);
            CommandUtils.RegisterCommandHandler(ECommandType.EscortWorld.ToString(), EscortToWorldPosCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.GoToPoint.ToString(), GoToPointCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.Teleport.ToString(), ForceTeleportCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.QuestProxyAction.ToString(), QuestProxyActionCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.LootProxyAction.ToString(), LootProxyActionCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.InteractionProxyAction.ToString(), InteractionProxyActionCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.Regroup.ToString(), RegroupCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.HoldPosition.ToString(), HoldPositionCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.OnYourOwn.ToString(), OnYourOwnCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.ReportAboutEnemy.ToString(), ReportAboutEnemyCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.ReportAboutSelf.ToString(), ReportAboutSelfCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.AimingBodyPart.ToString(), ChangeAimingBodyPartCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.DropTargetLoot.ToString(), DropTargetLootCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.ClearArea.ToString(), ClearAreaCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.OpenInventory.ToString(), OpenInventoryCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.GoToExfil.ToString(), GoToExfilCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.ChangeFormation.ToString(), ChangeFormationCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.StationaryWeaponProxyAction.ToString(), StationaryWeaponProxyActionCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.FollowMe.ToString(), FollowMeCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.EscortBtr.ToString(), EscortToBtrPosCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.ExcludeOrTakeOver.ToString(), ExcludeOrTakeOverCommandAction);
            // Fork: the C2/SAIN fork's commands (C2Sain/ForkCommands.cs, C2Sain/ContactReports.cs).
            CommandUtils.RegisterCommandHandler(ECommandType.SpotEnemy.ToString(), C2Sain.ContactReports.Report);
            CommandUtils.RegisterCommandHandler(ECommandType.EngageEnemy.ToString(), C2Sain.ForkCommands.EngageEnemy);
            CommandUtils.RegisterCommandHandler(ECommandType.EngageEnemyReckless.ToString(), C2Sain.ForkCommands.EngageEnemyReckless);
#if DEBUG
            CommandUtils.RegisterCommandHandler(ECommandType.DebugSpawnAI.ToString(), DebugSpawnAICommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.DebugTeleport.ToString(), DebugTeleportCommandAction);
            CommandUtils.RegisterCommandHandler(ECommandType.DebugInitAirdrop.ToString(), DebugInitAirdropCommandAction);
#endif
        }

        private McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();
        private LootDataMgr LootDataMgr => field ??= MgrAccessor.Get<LootDataMgr>();
        private FormationDataMgr FormationDataMgr => field ??= MgrAccessor.Get<FormationDataMgr>();

        public override void OnRaidStarted()
        {
            base.OnRaidStarted();
            CommandUtils.ClearGamePlayerOwner();
            CommandUtils.ClearMenuStack();
            CommandUtils.NavMeshCache();
        }

        public override void OnRaidEnded()
        {
            base.OnRaidEnded();
            CommandUtils.ClearGamePlayerOwner();
            CommandUtils.ClearMenuStack();
            CommandUtils.ClearNavMeshCache();
        }

        void Update()
        {
            if (!Gameloop.IsVaildGameWorld)
            {
                return;
            }

            if (KeyInput.BetterIsDown(MiyakoCarryServicePlugin.CommandHotKey.Value))
            {
                BuildMainCommandMenu();
            }
        }

        public Player[] GetMembers()
        {
            return McsMgr.GetAllMcsSquadMembersByMcsLeadId(Singleton<GameWorld>.Instance.MainPlayer.ProfileId).ToArray();
        }

        #region Menu  

        public virtual void BuildMainCommandMenu()
        {
            if (CommandUtils.GamePlayerOwner == null)
            {
                return;
            }

            CommandUtils.ClearMenuStack();
            CommandUtils.OpenMenu(BuildMainMenu);
        }

        public virtual void BuildMainMenu(McsCommandMenu menu)
        {
            var mcsBotPlayers = GetMembers();

            if (mcsBotPlayers.Length > 0)
            {
                menu.RegisterSubMenu(Locales.TEAMCOMMAND_NAME, Locales.TEAMCOMMAND_TARGETNAME, m => BuildTeamMenu(m, mcsBotPlayers, true), disabled: () => mcsBotPlayers.All(p => !p.HealthController.IsAlive));

                // Fork: top-level contact report. The crosshair point is only the fallback mark; the
                // handler looks for the enemy nearest the crosshair ray itself (C2Sain/ContactReports.cs).
                menu.RegisterCommand(C2Sain.ForkText.Get("McsFork/SpotEnemy Name"), "McsFork/SpotEnemy TargetName", ECommandType.SpotEnemy.ToString(), mcsBotPlayers,
                    disabled: () => mcsBotPlayers.All(p => !p.HealthController.IsAlive),
                    resolver: () =>
                    {
                        var ray = Singleton<GameWorld>.Instance.MainPlayer.InteractionRay;
                        return new McsCommandContext
                        {
                            Position = Physics.Raycast(ray, out var hit, 300f, LayersMaskController.HighPolyWithTerrainMask) ? hit.point : ray.GetPoint(100f)
                        };
                    });

                foreach (var mcsBotPlayer in mcsBotPlayers)
                {
                    menu.RegisterSubMenu(mcsBotPlayer.Profile.McsNickname, Locales.MEMBERCOMMAND_TARGETNAME, m => BuildMemberMenu(m, [mcsBotPlayer]), disabled: () => !mcsBotPlayer.HealthController.IsAlive);
                }
            }

#if DEBUG

            menu.RegisterSubMenu("Debug", "调试指令菜单", m => BuildDebugMenu(m, mcsBotPlayers));

#endif

            // 不打算对根菜单进行扩展
            // CommandUtils.Apply(EMenuId.Main.ToString(), menu, mcsBotPlayers);
        }

        public virtual List<VoiceMenuOption> GetVoiceProxyEscortOptions(Player[] members)
        {
            var options = new List<VoiceMenuOption>();
            if (members == null || members.Length == 0)
            {
                return options;
            }

            var menu = new McsCommandMenu();
            BuildProxyMenu(menu, members, false);
            BuildEscortMenu(menu, members, false);
            CollectVoiceOptions(menu, options);
            return options;
        }

        private void CollectVoiceOptions(McsCommandMenu menu, List<VoiceMenuOption> options)
        {
            foreach (var entry in menu.Entries)
            {
                if (entry.IsSubMenu)
                {
                    var sub = new McsCommandMenu();
                    entry.BuildSubMenu(sub);
                    CollectVoiceOptions(sub, options);
                }
                else if (IsVoiceOptionCommand(entry.CommandType) && !entry.Disabled)
                {
                    McsCommandContext ctx = null;
                    try
                    {
                        ctx = entry.Resolver?.Invoke();
                    }
                    catch
                    {
                        // 单个选项解析失败不阻塞其余选项
                    }
                    options.Add(new VoiceMenuOption
                    {
                        Name = entry.Name,
                        TargetName = entry.TargetName,
                        CommandType = entry.CommandType,
                        Position = ctx?.Position,
                        TargetId = ctx?.TargetId,
                    });
                }
            }
        }

        private static bool IsVoiceOptionCommand(string commandType)
        {
            return commandType is "InteractionProxyAction" or "QuestProxyAction"
                or "StationaryWeaponProxyAction" or "EscortWorld";
        }

        public virtual void BuildTeamMenu(McsCommandMenu menu, Player[] mcsBotPlayers, bool isTeam)
        {
            menu.RegisterCommand(Locales.TEAMREPORTABOUTENEMYCOMMAND_NAME, Locales.TEAMREPORTABOUTENEMYCOMMAND_TARGETNAME, ECommandType.ReportAboutEnemy.ToString(), mcsBotPlayers, shouldCheckExclude: false);
            menu.RegisterCommand(Locales.TEAMREPORTABOUTSELFCOMMAND_NAME, Locales.TEAMREPORTABOUTSELFCOMMAND_TARGETNAME, ECommandType.ReportAboutSelf.ToString(), mcsBotPlayers, shouldCheckExclude: false);
            // Fork: On Your Own removed from the menus; it also switched looting on, whatever the setting said.
            menu.RegisterCommand(Locales.TEAMREGROUPCOMMAND_NAME, Locales.TEAMREGROUPCOMMAND_TARGETNAME, ECommandType.Regroup.ToString(), mcsBotPlayers, shouldCheckExclude: isTeam);
            menu.RegisterCommand(C2Sain.ForkText.Get("McsFork/TeamBreakOff Name"), "McsFork/TeamBreakOff TargetName", ECommandType.FollowMe.ToString(), mcsBotPlayers, shouldCheckExclude: isTeam);
            // Fork: Engage Enemy orders (C2Sain/EngageTactics.cs).
            menu.RegisterCommand(C2Sain.ForkText.Get("McsFork/TeamEngage Name"), "McsFork/TeamEngage TargetName", ECommandType.EngageEnemy.ToString(), mcsBotPlayers, shouldCheckExclude: isTeam);
            menu.RegisterCommand(C2Sain.ForkText.Get("McsFork/TeamEngageReckless Name"), "McsFork/TeamEngageReckless TargetName", ECommandType.EngageEnemyReckless.ToString(), mcsBotPlayers, shouldCheckExclude: isTeam);
            menu.RegisterCommand(Locales.TEAMGOTOPOINTCOMMAND_NAME, Locales.TEAMGOTOPOINTCOMMAND_TARGETNAME, ECommandType.GoToPoint.ToString(), mcsBotPlayers, resolver: () => Physics.Raycast(Singleton<GameWorld>.Instance.MainPlayer.InteractionRay,
            out var hit, float.MaxValue, LayersMaskController.HighPolyWithTerrainMask)
            ? new McsCommandContext { Position = hit.point, ShouldCheckExclude = isTeam } : null, shouldCheckExclude: isTeam);
            menu.RegisterCommand(Locales.TEAMHOLDPOSITIONCOMMAND_NAME, Locales.TEAMHOLDPOSITIONCOMMAND_TARGETNAME, ECommandType.HoldPosition.ToString(), mcsBotPlayers, shouldCheckExclude: isTeam);
            menu.RegisterCommand(Locales.TEAMDROPTARGETLOOTCOMMAND_NAME, Locales.TEAMDROPTARGETLOOTCOMMAND_TARGETNAME, ECommandType.DropTargetLoot.ToString(), mcsBotPlayers, shouldCheckExclude: isTeam);
            menu.RegisterSubMenu(Locales.TEAMESCORTCOMMAND_NAME, Locales.TEAMESCORTCOMMAND_TARGETNAME, m => BuildEscortMenu(m, mcsBotPlayers, isTeam));
            menu.RegisterSubMenu(Locales.TEAMCHANGEAIMINGBODYPARTTYPECOMMAND_NAME, Locales.TEAMCHANGEAIMINGBODYPARTTYPECOMMAND_TARGETNAME, m => BuildAimingMenu(m, mcsBotPlayers, isTeam));
            // Fork: renamed Clear Target Area; refused in contact (ClearAreaCommandAction).
            menu.RegisterCommand(C2Sain.ForkText.Get("McsFork/TeamClearTargetArea Name"), "McsFork/TeamClearTargetArea TargetName", ECommandType.ClearArea.ToString(), mcsBotPlayers, resolver: () => Physics.Raycast(Singleton<GameWorld>.Instance.MainPlayer.InteractionRay, out var hit, float.MaxValue, LayersMaskController.HighPolyWithTerrainMask)
            ? new McsCommandContext { Position = hit.point, ShouldCheckExclude = isTeam } : null, shouldCheckExclude: isTeam);
            menu.RegisterSubMenu(Locales.CHANGEFORMATIONCOMMAND_NAME, Locales.CHANGEFORMATIONCOMMAND_TARGETNAME, m => BuildFormationMenu(m, [mcsBotPlayers.FirstOrDefault()], false));
            menu.RegisterCommand(Locales.TEAMFORCETELEPORTCOMMAND_NAME, Locales.TEAMFORCETELEPORTCOMMAND_TARGETNAME, ECommandType.Teleport.ToString(), mcsBotPlayers, shouldCheckExclude: isTeam);

            CommandUtils.Apply(EMenuId.Team.ToString(), menu, mcsBotPlayers);
        }

        public virtual void BuildMemberMenu(McsCommandMenu menu, Player[] mcsBotPlayers)
        {
            menu.RegisterCommand(Locales.EXCLUDEORTAKEOVERCOMMAND_NAME, Locales.EXCLUDEORTAKEOVERCOMMAND_TARGETNAME, ECommandType.ExcludeOrTakeOver.ToString(), mcsBotPlayers);
            menu.RegisterCommand(Locales.REPORTABOUTENEMYCOMMAND_NAME, Locales.REPORTABOUTENEMYCOMMAND_TARGETNAME, ECommandType.ReportAboutEnemy.ToString(), mcsBotPlayers);
            menu.RegisterCommand(Locales.REPORTABOUTSELFCOMMAND_NAME, Locales.REPORTABOUTSELFCOMMAND_TARGETNAME, ECommandType.ReportAboutSelf.ToString(), mcsBotPlayers);
            // Fork: On Your Own removed from the menus; it also switched looting on, whatever the setting said.
            menu.RegisterCommand(Locales.REGROUPCOMMAND_NAME, Locales.REGROUPCOMMAND_TARGETNAME, ECommandType.Regroup.ToString(), mcsBotPlayers);
            menu.RegisterCommand(C2Sain.ForkText.Get("McsFork/BreakOff Name"), "McsFork/BreakOff TargetName", ECommandType.FollowMe.ToString(), mcsBotPlayers);
            // Fork: Engage Enemy orders (C2Sain/EngageTactics.cs).
            menu.RegisterCommand(C2Sain.ForkText.Get("McsFork/Engage Name"), "McsFork/Engage TargetName", ECommandType.EngageEnemy.ToString(), mcsBotPlayers);
            menu.RegisterCommand(C2Sain.ForkText.Get("McsFork/EngageReckless Name"), "McsFork/EngageReckless TargetName", ECommandType.EngageEnemyReckless.ToString(), mcsBotPlayers);
            menu.RegisterCommand(Locales.GOTOPOINTCOMMAND_NAME, Locales.GOTOPOINTCOMMAND_TARGETNAME, ECommandType.GoToPoint.ToString(), mcsBotPlayers, resolver: () => Physics.Raycast(Singleton<GameWorld>.Instance.MainPlayer.InteractionRay,
            out var hit, float.MaxValue, LayersMaskController.HighPolyWithTerrainMask)
            ? new McsCommandContext { Position = hit.point } : null);
            menu.RegisterCommand(Locales.HOLDPOSITIONCOMMAND_NAME, Locales.HOLDPOSITIONCOMMAND_TARGETNAME, ECommandType.HoldPosition.ToString(), mcsBotPlayers);
            menu.RegisterCommand(Locales.DROPTARGETLOOTCOMMAND_NAME, Locales.DROPTARGETLOOTCOMMAND_TARGETNAME, ECommandType.DropTargetLoot.ToString(), mcsBotPlayers);
            menu.RegisterCommand(Locales.OPENINVENTORYCOMMAND_NAME, Locales.OPENINVENTORYCOMMAND_TARGETNAME, ECommandType.OpenInventory.ToString(), mcsBotPlayers, isLocal: true, disabled: () => MiyakoCarryServicePlugin.McsPluginClientConfig.BalanceRestriction);
            menu.RegisterSubMenu(Locales.CHANGEAIMINGBODYPARTTYPECOMMAND_NAME, Locales.CHANGEAIMINGBODYPARTTYPECOMMAND_TARGETNAME, m => BuildAimingMenu(m, mcsBotPlayers, false));
            menu.RegisterSubMenu(Locales.ESCORTCOMMAND_NAME, Locales.ESCORTCOMMAND_TARGETNAME, m => BuildEscortMenu(m, mcsBotPlayers, false));
            menu.RegisterSubMenu(Locales.PROXYCOMMAND_NAME, Locales.PROXYCOMMAND_TARGETNAME, m => BuildProxyMenu(m, mcsBotPlayers, false));
            // Fork: renamed Clear Target Area; refused in contact (ClearAreaCommandAction).
            menu.RegisterCommand(C2Sain.ForkText.Get("McsFork/ClearTargetArea Name"), "McsFork/ClearTargetArea TargetName", ECommandType.ClearArea.ToString(), mcsBotPlayers, resolver: () => Physics.Raycast(Singleton<GameWorld>.Instance.MainPlayer.InteractionRay,
            out var hit, float.MaxValue, LayersMaskController.HighPolyWithTerrainMask)
            ? new McsCommandContext { Position = hit.point } : null);
            menu.RegisterCommand(Locales.FORCETELEPORTCOMMAND_NAME, Locales.FORCETELEPORTCOMMAND_TARGETNAME, ECommandType.Teleport.ToString(), mcsBotPlayers);

            CommandUtils.Apply(EMenuId.Member.ToString(), menu, mcsBotPlayers);
        }

#if DEBUG
        public virtual void BuildDebugMenu(McsCommandMenu menu, Player[] mcsBotPlayers)
        {
            menu.RegisterCommand("传送", "传送至指定地点", ECommandType.DebugTeleport.ToString(), mcsBotPlayers, isLocal: true, resolver: () => Physics.Raycast(Singleton<GameWorld>.Instance.MainPlayer.InteractionRay, out var hit, float.MaxValue, LayersMaskController.HighPolyWithTerrainMask) ? new McsCommandContext { Position = hit.point } : null);

            if (!Tools.IsHost)
            {
                return;
            }

            menu.RegisterCommand("生成AI", "指定地点生成一个AI敌人", ECommandType.DebugSpawnAI.ToString(), mcsBotPlayers, isLocal: true, disabled: () => mcsBotPlayers == null || mcsBotPlayers.Length == 0, resolver: () => Physics.Raycast(Singleton<GameWorld>.Instance.MainPlayer.InteractionRay, out var hit, float.MaxValue, LayersMaskController.HighPolyWithTerrainMask) ? new McsCommandContext { Position = hit.point } : null);
            menu.RegisterCommand("生成空投", "指定地点生成空投", ECommandType.DebugInitAirdrop.ToString(), mcsBotPlayers, isLocal: true, resolver: () => Physics.Raycast(Singleton<GameWorld>.Instance.MainPlayer.InteractionRay, out var hit, float.MaxValue, LayersMaskController.HighPolyWithTerrainMask) ? new McsCommandContext { Position = hit.point } : null);
        }
#endif

        public virtual void BuildFormationMenu(McsCommandMenu menu, Player[] mcsBotPlayers, bool isTeam)
        {
            var formationDatas = FormationDataMgr.GetDatas<FormationData>();
            foreach (var formationData in formationDatas)
            {
                menu.RegisterCommand(
                    formationData.Name,
                    formationData.Name,
                    ECommandType.ChangeFormation.ToString(),
                    mcsBotPlayers,
                    isLocal: true,
                    resolver: () => new McsCommandContext 
                    { 
                        TargetId = formationData.Id,
                        ShouldCheckExclude = isTeam
                    },
                    shouldCheckExclude: isTeam
                );
            }
        }

        public virtual void BuildEscortMenu(McsCommandMenu menu, Player[] mcsBotPlayers, bool isTeam)
        {
            menu.RegisterSubMenu(
                isTeam ? Locales.TEAMQUESTESCORTCOMMAND_NAME : Locales.QUESTESCORTCOMMAND_NAME,
                isTeam ? Locales.TEAMQUESTESCORTCOMMAND_TARGETNAME : Locales.QUESTESCORTCOMMAND_TARGETNAME,
                m => BuildQuestEscortMenu(m, mcsBotPlayers, isTeam));

            menu.RegisterSubMenu(
                isTeam ? Locales.TEAMTRANSITESCORTCOMMAND_NAME : Locales.TRANSITESCORTCOMMAND_NAME,
                isTeam ? Locales.TEAMTRANSITESCORTCOMMAND_TARGETNAME : Locales.TRANSITESCORTCOMMAND_TARGETNAME,
                m => BuildWorldEscortMenu(m, mcsBotPlayers, Gameloop.GetDatas<TransitData, TransitDataMgr>(), isTeam));

            menu.RegisterSubMenu(
                isTeam ? Locales.TEAMEXFILESCORTCOMMAND_NAME : Locales.EXFILESCORTCOMMAND_NAME,
                isTeam ? Locales.TEAMEXFILESCORTCOMMAND_TARGETNAME : Locales.EXFILESCORTCOMMAND_TARGETNAME,
                m => BuildWorldEscortMenu(m, mcsBotPlayers, Gameloop.GetDatas<ExfilData, ExfilDataMgr>(), isTeam));

            menu.RegisterSubMenu(
                isTeam ? Locales.TEAMSWITCHESCORTCOMMAND_NAME : Locales.SWITCHESCORTCOMMAND_NAME,
                isTeam ? Locales.TEAMSWITCHESCORTCOMMAND_TARGETNAME : Locales.SWITCHESCORTCOMMAND_TARGETNAME,
                m => BuildWorldEscortMenu(m, mcsBotPlayers, Gameloop.GetDatas<SwitchData, SwitchDataMgr>(), isTeam));

            menu.RegisterSubMenu(
                isTeam ? Locales.TEAMSTATIONARYWEAPONESCORTCOMMAND_NAME : Locales.STATIONARYWEAPONESCORTCOMMAND_NAME,
                isTeam ? Locales.TEAMSTATIONARYWEAPONESCORTCOMMAND_TARGETNAME : Locales.STATIONARYWEAPONESCORTCOMMAND_TARGETNAME,
                m => BuildWorldEscortMenu(m, mcsBotPlayers, Gameloop.GetDatas<StationaryWeaponData, StationaryWeaponDataMgr>(), isTeam));

            var btrController = Singleton<GameWorld>.Instance.BtrController;
            if (btrController != null && btrController.Initiated())
            {
                menu.RegisterCommand(
                    isTeam ? Locales.TEAMBTRESCORTCOMMAND_NAME : Locales.BTRESCORTCOMMAND_NAME,
                    isTeam ? Locales.TEAMBTRESCORTCOMMAND_TARGETNAME : Locales.BTRESCORTCOMMAND_TARGETNAME,
                    ECommandType.EscortBtr.ToString(), mcsBotPlayers, disabled: () => btrController.BtrVehicle.VehicleRouteState == EVehicleRouteState.OnDepot, shouldCheckExclude: isTeam);
            }
            else
            {
                menu.RegisterCommand(
                    isTeam ? Locales.TEAMBTRESCORTCOMMAND_NAME : Locales.BTRESCORTCOMMAND_NAME,
                    isTeam ? Locales.TEAMBTRESCORTCOMMAND_TARGETNAME : Locales.BTRESCORTCOMMAND_TARGETNAME,
                    ECommandType.EscortBtr.ToString(), mcsBotPlayers, disabled: () => true, shouldCheckExclude: isTeam);
            }

            menu.RegisterSubMenu(
                isTeam ? Locales.TEAMAIRDROPESCORTCOMMAND_NAME : Locales.AIRDROPESCORTCOMMAND_NAME,
                isTeam ? Locales.TEAMAIRDROPESCORTCOMMAND_TARGETNAME : Locales.AIRDROPESCORTCOMMAND_TARGETNAME,
                m => BuildAirdropEscortMenu(m, mcsBotPlayers, LootDataMgr.GetAirdrops(), isTeam));
            
            CommandUtils.Apply(EMenuId.Escort.ToString(), menu, mcsBotPlayers);
        }

        public virtual void BuildAirdropEscortMenu(McsCommandMenu menu, Player[] mcsBotPlayers, IEnumerable<LootData> lootDatas, bool isTeam)
        {
            var myPlayerPos = Singleton<GameWorld>.Instance.MainPlayer.Position;
            foreach (var lootData in lootDatas)
            {
                menu.RegisterCommand(
                    lootData.Item.Name.McsLocalized(),
                    string.Format(Locales.GETACTIONTARGETNAME_TARGETNAME.McsLocalized(), Mathf.RoundToInt(Vector3.Distance(myPlayerPos, lootData.RootTransform.position))),
                    ECommandType.EscortWorld.ToString(), mcsBotPlayers,
                    resolver: () => new McsCommandContext 
                    { 
                        Position = lootData.RootTransform.position,
                        ShouldCheckExclude = isTeam
                    }, shouldCheckExclude: isTeam);
            }
        }

        public virtual void BuildWorldEscortMenu(McsCommandMenu menu, Player[] mcsBotPlayers, IEnumerable<WorldData> worldDatas, bool isTeam)
        {
            var myPlayerPos = Singleton<GameWorld>.Instance.MainPlayer.Position;
            foreach (var worldData in worldDatas)
            {
                menu.RegisterCommand(
                    worldData.GetActionName(),
                    worldData.GetActionTargetName(myPlayerPos),
                    ECommandType.EscortWorld.ToString(), mcsBotPlayers,
                    disabled: worldData.IsDisabled,
                    resolver: () => new McsCommandContext 
                    { 
                        Position = worldData.GetPos(),
                        ShouldCheckExclude = isTeam 
                    }, shouldCheckExclude: isTeam);
            }

            CommandUtils.Apply(EMenuId.WorldEscort.ToString(), menu, mcsBotPlayers);
        }

        public virtual void BuildQuestEscortMenu(McsCommandMenu menu, Player[] mcsBotPlayers, bool isTeam)
        {
            var questDataMgr = MgrAccessor.Get<QuestDataMgr>();
            if (questDataMgr == null)
            {
                return;
            }

            foreach ((var questDataClass, var questDatas) in questDataMgr.GetQuestDataByGroup())
            {
                menu.RegisterSubMenu(questDataClass.Template.Name.McsLocalized(), Locales.SUBQUESTESCORTCOMMAND_TARGETNAME, m => BuildWorldEscortMenu(m, mcsBotPlayers, questDatas.Cast<WorldData>(), isTeam));
            }
            CommandUtils.Apply(EMenuId.QuestEscort.ToString(), menu, mcsBotPlayers);
        }

        public virtual void BuildAimingMenu(McsCommandMenu menu, Player[] mcsBotPlayers, bool isTeam)
        {
            foreach (var bodyPartType in Classification.AimingBodyPartTypes)
            {
                var name = Tools.GetBodyPartTypeLocales(bodyPartType).McsLocalized();
                menu.RegisterCommand(name, name, ECommandType.AimingBodyPart.ToString(), mcsBotPlayers, resolver: () => new McsCommandContext 
                { 
                    AimingBodyPartType = bodyPartType,
                    ShouldCheckExclude = isTeam
                }, shouldCheckExclude: isTeam);
            }

            CommandUtils.Apply(EMenuId.Aiming.ToString(), menu, mcsBotPlayers);
        }

        public virtual void BuildProxyMenu(McsCommandMenu menu, Player[] mcsBotPlayers, bool isTeam)
        {
            menu.RegisterSubMenu(Locales.QUESTPROXYCOMMAND_NAME, Locales.QUESTPROXYCOMMAND_TARGETNAME, m => BuildQuestProxyMenu(m, mcsBotPlayers, isTeam));
            menu.RegisterSubMenu(Locales.SWITCHPROXYCOMMAND_NAME, Locales.SWITCHPROXYCOMMAND_TARGETNAME, m => BuildSwitchProxyMenu(m, mcsBotPlayers, isTeam));
            menu.RegisterSubMenu(Locales.STATIONARYWEAPONPROXYCOMMAND_NAME, Locales.STATIONARYWEAPONPROXYCOMMAND_TARGETNAME, m => BuildStationaryWeaponProxyMenu(m, mcsBotPlayers, isTeam));

            CommandUtils.Apply(EMenuId.Proxy.ToString(), menu, mcsBotPlayers);
        }

        public virtual void BuildQuestProxyMenu(McsCommandMenu menu, Player[] mcsBotPlayers, bool isTeam)
        {
            var questDataMgr = MgrAccessor.Get<QuestDataMgr>();
            if (questDataMgr == null)
            {
                return;
            }

            foreach ((var questDataClass, var questDatas) in questDataMgr.GetQuestDataByGroup())
            {
                menu.RegisterSubMenu(questDataClass.Template.Name.McsLocalized(), Locales.SUBQUESTPROXYCOMMAND_TARGETNAME, m => BuildSubQuestProxyMenu(m, mcsBotPlayers, questDatas, isTeam));
            }
            CommandUtils.Apply(EMenuId.QuestProxy.ToString(), menu, mcsBotPlayers);
        }

        public virtual void BuildSubQuestProxyMenu(McsCommandMenu menu, Player[] mcsBotPlayers, List<QuestData> questDatas, bool isTeam)
        {
            var myPlayerPos = Singleton<GameWorld>.Instance.MainPlayer.Position;
            foreach (var questData in questDatas)
            {
                menu.RegisterCommand(
                    questData.GetActionName(),
                    questData.GetActionTargetName(myPlayerPos),
                    ECommandType.QuestProxyAction.ToString(), mcsBotPlayers,
                    disabled: questData.IsProxyActionDisabled,
                    resolver: () => new McsCommandContext
                    {
                        Position = questData.GetPos(),
                        TargetId = questData.Id(),
                        ShouldCheckExclude = isTeam
                    },
                    shouldCheckExclude: isTeam);
            }
            // 展示内容为任务列表，不进行扩展
        }

        public virtual void BuildSwitchProxyMenu(McsCommandMenu menu, Player[] mcsBotPlayers, bool isTeam)
        {
            var myPlayerPos = Singleton<GameWorld>.Instance.MainPlayer.Position;
            foreach (var switchData in Gameloop.GetDatas<SwitchData, SwitchDataMgr>())
            {
                menu.RegisterCommand(
                    switchData.GetActionName(),
                    switchData.GetActionTargetName(myPlayerPos),
                    ECommandType.InteractionProxyAction.ToString(), mcsBotPlayers,
                    disabled: switchData.IsProxyActionDisabled,
                    resolver: () => new McsCommandContext
                    {
                        Position = switchData.GetPos(),
                        TargetId = switchData.Id(),
                        ShouldCheckExclude = isTeam
                    },
                    shouldCheckExclude: isTeam);
            }
            // 展示内容为开关列表，不进行扩展
        }

        public virtual void BuildStationaryWeaponProxyMenu(McsCommandMenu menu, Player[] mcsBotPlayers, bool isTeam)
        {
            var myPlayerPos = Singleton<GameWorld>.Instance.MainPlayer.Position;
            foreach (var stationaryWeaponData in Gameloop.GetDatas<StationaryWeaponData, StationaryWeaponDataMgr>())
            {
                menu.RegisterCommand(
                    stationaryWeaponData.GetActionName(),
                    stationaryWeaponData.GetActionTargetName(myPlayerPos),
                    ECommandType.StationaryWeaponProxyAction.ToString(), mcsBotPlayers,
                    disabled: stationaryWeaponData.IsProxyActionDisabled,
                    resolver: () => new McsCommandContext
                    {
                        Position = stationaryWeaponData.GetPos(),
                        TargetId = stationaryWeaponData.Id(),
                        ShouldCheckExclude = isTeam
                    },
                    shouldCheckExclude: isTeam);
            }
            // 展示内容为开关列表，不进行扩展
        }

        #endregion

        #region Action

#if DEBUG

        public virtual void DebugSpawnAICommandAction(McsCommandContext ctx)
        {
            Diz.Utils.AsyncWorker.RunInMainTread(async () =>
            {
                var pos = ctx.Position.Value;
                var botGame = Singleton<IBotGame>.Instance;
                var botsController = botGame?.BotsController;
                var botSpawner = botsController?.BotSpawner;
                var botCreator = botSpawner?._botCreator;
                if (botCreator == null)
                {
                    return;
                }

                // 随机敌方：这里用 pmcBEAR + normal 难度，可自行替换  
                var side = EPlayerSide.Usec;
                var wildSpawnType = WildSpawnType.pmcUSEC;
                var botDifficulty = BotDifficulty.normal;

                var botSpawnParams = new BotSpawnParams
                {
                    ShallBeGroup = new ShallBeGroupParams(false, false, 1),
                    Id_spawn = "McsDebugEnemy"
                };

                var botProfileDataClass = new GetProfileDataParams(side, wildSpawnType, botDifficulty, 1, botSpawnParams);

                var botCreationDataClass = await BotCreationData.Create(botProfileDataClass, botCreator, 0, botSpawner);
                if (botCreationDataClass == null)
                {
                    return;
                }

                var cloneProfile = ctx.McsBotPlayer.Profile.Clone();
                cloneProfile.Id = MongoID.Generate();
                cloneProfile.Info.GroupId = "McsDebug";
                cloneProfile.Info.Settings.BotDifficulty = BotDifficulty.easy;
                AccessTools.Field(typeof(Profile), nameof(cloneProfile.AccountId)).SetValue(cloneProfile, MyExtensions.RandomInclude(30560334, 33560336).ToString());
                botCreationDataClass.AddProfile(cloneProfile);

                var closestGroupPoint = botsController.CoversData.GetClosest(pos);
                botCreationDataClass.AddPosition(pos, closestGroupPoint.CorePointInGame.Id);
                var closestZone = botSpawner.GetClosestZone(pos, out _);

                var groupAction = new Func<BotOwner, BotZone, BotsGroup>((botOwner, botZone) =>
                {
                    var enemies = botSpawner.GetBotEnemiesList(botOwner);
                    var botsGroup = new BotsGroup(closestZone, botGame, botOwner, enemies.ToList(), botSpawner._deadBodiesController, botSpawner._allPlayers, false);
                    botSpawner.Groups.AddNoKey(botsGroup, botZone);
                    botsGroup.AddMember(botOwner, false);
                    return botsGroup;
                });

                var onActivate = new Action<BotOwner>(botOwner =>
                {
                    var stopWatch = new Stopwatch();
                    stopWatch.Start();
                    botSpawner.ActivateBotCallback(botOwner, botCreationDataClass, null, botCreationDataClass.SpawnParams.ShallBeGroup != null, stopWatch);
                    ctx.McsBotPlayer.BotOwner.BotsGroup.AddEnemy(botOwner.GetPlayer, EBotEnemyCause.addBotAtGroup);
                    var mcsBotPlayers = McsMgr.GetAllAliveMcsBotPlayer();
                    foreach (var mcsBotPlayer in mcsBotPlayers)
                    {

                    }
                });

                botSpawner._inSpawnProcess += 1;
                var cancellationToken = new CancellationToken();
                await botCreator.ActivateBot(
                    botCreationDataClass.Profiles[0],
                    new PositionNote(pos, botCreationDataClass.GetPosition().CorePointId, true),
                    closestZone, true, groupAction, onActivate, cancellationToken);
            });
        }

        public virtual void DebugTeleportCommandAction(McsCommandContext ctx)
        {
            Singleton<GameWorld>.Instance.MainPlayer.Teleport(ctx.Position.Value);
        }

        public virtual void DebugInitAirdropCommandAction(McsCommandContext ctx)
        {
            Singleton<GameWorld>.Instance.InitAirdrop(Classification.AirdropIds.Random(), true, ctx.Position.HasValue ? ctx.Position.Value : Singleton<GameWorld>.Instance.MainPlayer.Position);
        }

#endif

        public virtual void ChangeFormationCommandAction(McsCommandContext ctx)
        {
            FormationDataMgr.ApplyFormationData(ctx.TargetId);
            var mcsBotPlayers = McsMgr.GetAllAliveMcsSquadMembersByMcsLeadId(ctx.McsLeadPlayer.ProfileId);
            foreach (var mcsBotPlayer in mcsBotPlayers)
            {
                var botOwner = mcsBotPlayer.AIData.BotOwner;
                botOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.Roger
                });
            }
        }

        public virtual void OpenInventoryCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var gameWorld = Singleton<GameWorld>.Instance;
            var itemOwners = gameWorld.ItemOwners;
            foreach (var itemOwner in itemOwners)
            {
                var rootItem = itemOwner.Key.RootItem;
                if (!rootItem.IsPlayerInventory)
                {
                    continue;
                }

                var profileId = rootItem.Owner?.ID;
                if (string.IsNullOrEmpty(profileId))
                {
                    continue;
                }

                if (mcsBotPlayer.ProfileId == profileId && rootItem.Owner is ItemController traderControllerClass)
                {
                    var inventoryActionClass = new InteractionContextHelper.CG_GetAvailableInteractionState1
                    {
                        owner = CommandUtils.GamePlayerOwner,
                        rootItem = rootItem,
                        lootItemOwner = traderControllerClass
                    };
                    inventoryActionClass.method_3();
                }
            }
        }

        public virtual void QuestProxyActionCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            botOwner.Mover._lastTimePosChanged = Time.time;
            botOwner.StopMove();
            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData != null)
            {
                mcsBotPlayerData.IsLooting = false;
                mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldQuestProxyAction);
                mcsBotPlayerData.MoveResetRequested = true;
                mcsBotPlayerData.ProxyTargetId = ctx.TargetId;
                mcsBotPlayerData.TargetPos = ctx.Position;
                botOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.Going,
                    Keys = mcsBotPlayerData.BotOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null
                });
            }
        }

        public virtual void EscortToWorldPosCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            if (botOwner.Memory.HaveEnemy)
            {
                botOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.Negative,
                    Keys = botOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null
                });
            }
            else
            {
                botOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.FollowMe
                });
            }
            botOwner.Mover._lastTimePosChanged = Time.time;
            botOwner.StopMove();
            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData != null)
            {
                mcsBotPlayerData.IsLooting = false;
                mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldEscort);
                mcsBotPlayerData.MoveResetRequested = true;
                mcsBotPlayerData.TargetPos = ctx.Position;
            }
        }

        public virtual void ReportAboutEnemyCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            if (botOwner.Memory.HaveEnemy)
            {
                botOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.OnFirstContact,
                    Position = botOwner.Memory.GoalEnemy.EnemyLastPosition
                });
            }
            else
            {
                botOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.Clear
                });
            }
        }

        public virtual void ReportAboutSelfCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            var health = botOwner.HealthController.GetBodyPartHealth(EBodyPart.Common);
            var key1 = $"{(int)health.Current}/{health.Maximum}";
            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            var key2 = mcsBotPlayerData != null && mcsBotPlayerData.CollectCurrentWeaponAmmoAndMagCount(out var total, out _)
                ? total.ToString()
                : "0";
            var allActiveEffects = botOwner.HealthController.GetAllActiveEffects();
            var healthStates = new List<HealthState>();
            foreach (var activeEffect in allActiveEffects)
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

                healthStates.Add(new HealthState
                {
                    BodyPart = activeEffect.BodyPart.ToString(),
                    EffectType = effectType
                });
            }
            var key3 = Json.Serialize(healthStates);
            botOwner.TalkMsg(new McsMsg
            {
                PhraseTrigger = EPhraseTrigger.Mine,
                Keys = [key1, key2, key3]
            });
        }

        public void ChangeAimingBodyPartCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData != null)
            {
                mcsBotPlayerData.AimingBodyPartType = ctx.AimingBodyPartType;
            }
            botOwner.TalkMsg(new McsMsg
            {
                PhraseTrigger = EPhraseTrigger.Roger
            });
        }

        public void OnYourOwnCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            botOwner.WeaponManager.Stationary.DropCurWeapon(false, true);
            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData != null)
            {
                mcsBotPlayerData.SetIntent([Intents.ShouldKeepFormation]);
                mcsBotPlayerData.MoveResetRequested = true;
                mcsBotPlayerData.IsLooting = false;
                mcsBotPlayerData.TargetPos = null;
                mcsBotPlayerData.ProxyTargetId = null;
            }
            botOwner.TalkMsg(new McsMsg
            {
                PhraseTrigger = EPhraseTrigger.Roger
            });
        }

        public virtual void RegroupCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            botOwner.WeaponManager.Stationary.DropCurWeapon(false, true);
            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData != null)
            {
                mcsBotPlayerData.SetIntent([Intents.ShouldKeepFormation, Intents.ShouldFollowMe]);
                mcsBotPlayerData.MoveResetRequested = true;
                mcsBotPlayerData.IsLooting = false;
                mcsBotPlayerData.TargetPos = null;
                mcsBotPlayerData.ProxyTargetId = null;
            }
            botOwner.TalkMsg(new McsMsg
            {
                PhraseTrigger = EPhraseTrigger.Regroup
            });
        }

        public virtual void FollowMeCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            botOwner.WeaponManager.Stationary.DropCurWeapon(false, true);
            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData != null)
            {
                // Fork: Break off from combat -- run back to the player, then SAIN resumes. No lasting
                // passive state (upstream's Follow Me intent stayed until On Your Own removed it).
                mcsBotPlayerData.SetIntent([Intents.ShouldKeepFormation]);
                mcsBotPlayerData.MoveResetRequested = true;
                mcsBotPlayerData.IsLooting = false;
                mcsBotPlayerData.TargetPos = null;
                mcsBotPlayerData.ProxyTargetId = null;
                mcsBotPlayerData.Fork.BreakOff.Begin(Time.time);
            }
            botOwner.TalkMsg(new McsMsg
            {
                PhraseTrigger = EPhraseTrigger.Regroup
            });
        }

        public virtual void EscortToBtrPosCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            if (botOwner.Memory.HaveEnemy)
            {
                botOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.Negative,
                    Keys = botOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null
                });
            }
            botOwner.Mover._lastTimePosChanged = Time.time;
            botOwner.StopMove();
            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData != null)
            {
                mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldEscortToBtr);
                mcsBotPlayerData.MoveResetRequested = true;
                mcsBotPlayerData.IsLooting = false;
                mcsBotPlayerData.TargetPos = null;
                mcsBotPlayerData.ProxyTargetId = null;
            }
        }

        public virtual void ExcludeOrTakeOverCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData != null)
            {
                mcsBotPlayerData.IsExcluded = !mcsBotPlayerData.IsExcluded;
                if (mcsBotPlayerData.IsExcluded)
                {
                    botOwner.TalkMsg(new McsMsg
                    {
                        PhraseTrigger = EPhraseTrigger.PhraseNone,
                        Keys = [Locales.EXCLUDED]
                    });
                }
                else
                {
                    botOwner.TalkMsg(new McsMsg
                    {
                        PhraseTrigger = EPhraseTrigger.PhraseNone,
                        Keys = [Locales.TAKENOVER]
                    });
                }
            }
        }

        public virtual void GoToPointCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            var pos = Tools.GetPosNearTarget(ctx.Position.Value, botOwner);
            if (!pos.HasValue)
            {
                return;
            }

            botOwner.TalkMsg(new McsMsg
            {
                PhraseTrigger = EPhraseTrigger.Going,
                Keys = botOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null
            });

            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData != null)
            {
                mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldGoToPoint);
                mcsBotPlayerData.MoveResetRequested = true;
                mcsBotPlayerData.IsLooting = false;
                mcsBotPlayerData.TargetPos = pos.Value;
                mcsBotPlayerData.ProxyTargetId = null;
            }
            botOwner.Mover._lastTimePosChanged = Time.time;
            botOwner.StopMove();
        }

        public virtual void HoldPositionCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            botOwner.StopMove();
            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData != null)
            {
                mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldHoldPosition);
                mcsBotPlayerData.MoveResetRequested = true;
                mcsBotPlayerData.IsLooting = false;
                mcsBotPlayerData.TargetPos = null;
                mcsBotPlayerData.ProxyTargetId = null;
            }
            botOwner.TalkMsg(new McsMsg
            {
                PhraseTrigger = EPhraseTrigger.HoldPosition
            });
        }

        public virtual void DropTargetLootCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            if (botOwner.ExternalItemsController.HaveItemsToDrop())
            {
                botOwner.StopMove();
                var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
                if (mcsBotPlayerData != null)
                {
                    mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldDropTargetLoot);
                    mcsBotPlayerData.MoveResetRequested = true;
                    mcsBotPlayerData.IsLooting = false;
                    mcsBotPlayerData.TargetPos = null;
                    mcsBotPlayerData.ProxyTargetId = null;
                }
                botOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.Going,
                    Keys = botOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null
                });
            }
            else
            {
                botOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.Negative,
                    Keys = botOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null
                });
            }
        }

        public virtual void ForceTeleportCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            var stationary = botOwner.WeaponManager?.Stationary;
            if (stationary != null && stationary.CurLink != null && stationary.Taken)
            {
                stationary.DropCurWeapon(false, true);
            }
            botOwner.StopMove();
            botOwner.Mover.AllowTeleport();
            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData != null)
            {
                mcsBotPlayerData.SetIntent([Intents.ShouldKeepFormation], Intents.ShouldTeleport);
                mcsBotPlayerData.MoveResetRequested = true;
                mcsBotPlayerData.IsLooting = false;
                mcsBotPlayerData.TargetPos = null;
                mcsBotPlayerData.ProxyTargetId = null;
                mcsBotPlayer.Teleport(mcsBotPlayerData.LeadPlayer.Position, true);
            }
            var playerPosition = mcsBotPlayer.Position;
            botOwner.Mover._lastGoodCastPoint = botOwner.Mover._prevSuccessLinkedFrom = botOwner.Mover._prevLinkPos = botOwner.Mover.PositionOnWayInner = playerPosition;
            botOwner.Mover.SetPlayerToNavMesh(playerPosition);
            botOwner.GoToSomePointData.SetPoint(playerPosition);
            botOwner.Mover._lastTimePosChanged = Time.time;
            botOwner.TryResetHandsState();
            botOwner.TalkMsg(new McsMsg
            {
                PhraseTrigger = EPhraseTrigger.Roger
            });

            botOwner.Memory.GoalTarget.Clear();
            botOwner.Memory.GoalEnemy = null;
        }

        public virtual void InteractionProxyActionCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            if (botOwner.Memory.HaveEnemy)
            {
                botOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.Negative,
                    Keys = botOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null
                });
            }
            botOwner.Mover._lastTimePosChanged = Time.time;
            botOwner.StopMove();
            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData != null)
            {
                mcsBotPlayerData.IsLooting = false;
                mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldInteractionProxyAction);
                mcsBotPlayerData.MoveResetRequested = true;
                var interactableObjectData = Singleton<GameWorld>.Instance.FindInteractableObjectData(ctx.TargetId);
                if (interactableObjectData != null)
                {
                    mcsBotPlayerData.ProxyTargetId = interactableObjectData.Id();
                    mcsBotPlayerData.TargetPos = interactableObjectData.GetPos();
                    botOwner.TalkMsg(new McsMsg
                    {
                        PhraseTrigger = EPhraseTrigger.Going,
                        Keys = botOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null
                    });
                }
            }
        }

        public virtual void LootProxyActionCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (botOwner.Memory.HaveEnemy)
            {
                botOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.Negative,
                    Keys = botOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null
                });
                if (mcsBotPlayerData != null)
                {
                    mcsBotPlayerData.ProxyTargetId = null;
                    mcsBotPlayerData.TargetPos = null;
                }
            }
            botOwner.Mover._lastTimePosChanged = Time.time;
            botOwner.StopMove();

            if (mcsBotPlayerData == null)
            {
                return;
            }

            var lootData = LootDataMgr.FindLootData(ctx.TargetId);
            mcsBotPlayerData.IsLooting = false;
            mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldLootProxyAction);
            mcsBotPlayerData.MoveResetRequested = true;
            LootDataMgr.UnlockLootingTarget(lootData);
            LootDataMgr.UnlockLootingTargetRootTransform(lootData.RootTransform);
            if (!LootDataMgr.IsLockedLootingTarget(lootData) && !LootDataMgr.IsLockedLootingTargetRootTransform(lootData.RootTransform))
            {
                LootDataMgr.LockLootItemToTarget(lootData);
                LootDataMgr.LockLootingTargetRootTransform(lootData.RootTransform);
                mcsBotPlayerData.LootingTarget = lootData;
                mcsBotPlayerData.ProxyTargetId = lootData.Item.Id;
                mcsBotPlayerData.TargetPos = lootData.RootTransform.position;
                botOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.Going,
                    Keys = botOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null
                });
            }
            else
            {
                botOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.Negative,
                    Keys = botOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null
                });
                mcsBotPlayerData.RemoveIntent(Intents.ShouldLootProxyAction);
                mcsBotPlayerData.ProxyTargetId = null;
                mcsBotPlayerData.TargetPos = null;
            }
        }

        public virtual void HandleMcsLeadPlayerExtracted(McsLeadPlayerExtractedEvent @event)
        {
            var mcsBotPlayers = McsMgr.GetAllAliveMcsSquadMembersByMcsLeadId(@event.McsLeadPlayerId);
            foreach (var mcsBotPlayer in mcsBotPlayers)
            {
                CommandUtils.Dispatch(
                    ECommandType.GoToExfil.ToString(),
                    [mcsBotPlayer],
                    null
                );
            }
        }

        public virtual void GoToExfilCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer?.AIData?.BotOwner;
            if (botOwner == null)
            {
                return;
            }

            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData == null)
            {
                return;
            }

            mcsBotPlayerData.SetIntent([Intents.ShouldKeepFormation], Intents.ShouldExfil);
            mcsBotPlayerData.MoveResetRequested = true;
        }

        public virtual void ClearAreaCommandAction(McsCommandContext ctx)
        {
            if (!ctx.Position.HasValue)
            {
                return;
            }

            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer?.AIData?.BotOwner;
            if (botOwner == null)
            {
                return;
            }

            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData == null)
            {
                return;
            }
            // Fork: Clear Target Area is refused while the squad is in contact.
            if (C2Sain.ForkCommands.RefuseInContact(botOwner, mcsBotPlayerData))
            {
                return;
            }
            var center = ctx.Position.Value;
            var leadId = ctx.McsLeadPlayer.ProfileId;
            var mcsAILeadPlayer = mcsBotPlayerData.McsAILeadPlayer;
            if (mcsAILeadPlayer == null)
            {
                return;
            }

            List<Player> members;
            List<List<Vector3>> segments;

            if (mcsAILeadPlayer.ClearAreaCacheCenter == center
                && Time.time - mcsAILeadPlayer.ClearAreaCacheTime < 1f
                && mcsAILeadPlayer.ClearAreaCacheMembers != null
                && mcsAILeadPlayer.ClearAreaCacheSegments != null)
            {
                members = mcsAILeadPlayer.ClearAreaCacheMembers;
                segments = mcsAILeadPlayer.ClearAreaCacheSegments;
            }
            else
            {
                members = MgrAccessor.Get<McsMgr>()
                    .GetAllAliveMcsSquadMembersByMcsLeadId(leadId)
                    .Where(p => p != null).OrderBy(p => p.ProfileId).ToList();

                if (members.Count == 0)
                {
                    return;
                }

                var startFrom = members[0].Position;
                var fullRoute = CommandUtils.GenerateClearAreaWaypoints(center, 30f, startFrom);
                if (fullRoute.Count == 0)
                {
                    return;
                }

                segments = CommandUtils.SplitRoute(fullRoute, members.Count);

                mcsAILeadPlayer.ClearAreaCacheCenter = center;
                mcsAILeadPlayer.ClearAreaCacheTime = Time.time;
                mcsAILeadPlayer.ClearAreaCacheMembers = members;
                mcsAILeadPlayer.ClearAreaCacheSegments = segments;
            }

            var total = members.Count;
            var index = members.FindIndex(p => p.ProfileId == ctx.McsBotPlayer.ProfileId);
            if (total == 0 || index < 0)
            {
                return;
            }

            var seg = new List<Vector3>(segments[index]);
            if (seg.Count == 0)
            {
                return;
            }

            if (seg[seg.Count - 1].McsSqrDistance(botOwner.Position) < seg[0].McsSqrDistance(botOwner.Position))
            {
                seg.Reverse();
            }

            botOwner.TalkMsg(new McsMsg
            {
                PhraseTrigger = EPhraseTrigger.Going,
                Keys = botOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null
            });
            mcsBotPlayerData.IsLooting = false;
            mcsBotPlayerData.ClearAreaPoints = seg;
            mcsBotPlayerData.ClearAreaIndex = 0;
            mcsBotPlayerData.ClearAreaLookAroundUntil = 0f;
            mcsBotPlayerData.TargetPos = seg[0];
            mcsBotPlayerData.ProxyTargetId = null;
            mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldClearArea);
            mcsBotPlayerData.MoveResetRequested = true;
            botOwner.Mover._lastTimePosChanged = Time.time;
            botOwner.StopMove();
        }

        public virtual void StationaryWeaponProxyActionCommandAction(McsCommandContext ctx)
        {
            var mcsBotPlayer = ctx.McsBotPlayer;
            var botOwner = mcsBotPlayer.AIData.BotOwner;
            botOwner.Mover._lastTimePosChanged = Time.time;
            botOwner.StopMove();
            var mcsBotPlayerData = botOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData != null)
            {
                mcsBotPlayerData.IsLooting = false;
                mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldStationaryWeaponProxyAction);
                mcsBotPlayerData.MoveResetRequested = true;
                var interactableObjectData = Singleton<GameWorld>.Instance.FindInteractableObjectData(ctx.TargetId);
                if (interactableObjectData != null)
                {
                    mcsBotPlayerData.ProxyTargetId = interactableObjectData.Id();
                    mcsBotPlayerData.TargetPos = interactableObjectData.GetPos();
                    botOwner.TalkMsg(new McsMsg 
                    { 
                        PhraseTrigger = EPhraseTrigger.Going,
                        Keys = botOwner.Memory.HaveEnemy ? [Locales.ONFIGHT] : null
                    });
                }
            }
        }

        #endregion
    }
}
