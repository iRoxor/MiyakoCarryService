namespace MiyakoCarryService.Client.Enums
{
    public enum ECommandType
    {
        Teleport,
        GoToPoint,
        HoldPosition,
        Regroup,
        ReportAboutEnemy,
        OnYourOwn,
        EscortWorld,
        GoToExfil,
        AimingBodyPart,
        QuestProxyAction,
        LootProxyAction,
        InteractionProxyAction,
        EndProxyAction,
        DropTargetLoot,
        ReportAboutSelf,
        ClearArea,
        OpenInventory,
        ChangeFormation,
        StationaryWeaponProxyAction,
        FollowMe,
        EscortBtr,
        ExcludeOrTakeOver,
        // Fork: the C2/SAIN fork's commands (C2Sain/ForkCommands.cs, C2Sain/ContactReports.cs).
        SpotEnemy,
        EngageEnemy,
        EngageEnemyReckless,
        DefendMe,
        // Fork v11: the stances (C2Sain/ForkCommands.cs). EngageEnemy and EngageEnemyReckless above are
        // no longer in the menus or registered; the names stay so the enum's other values keep their numbers.
        TeamStancePassive,
        TeamStanceDefensive,
        TeamStanceAggressive,
        TeamStanceHyperAggressive,
        StancePassive,
        StanceDefensive,
        StanceAggressive,
        StanceHyperAggressive,
#if DEBUG
        DebugSpawnAI,
        DebugTeleport,
        DebugInitAirdrop,
#endif
    }
}