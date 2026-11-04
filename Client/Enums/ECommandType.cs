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
#if DEBUG
        DebugSpawnAI,
        DebugTeleport,
        DebugInitAirdrop,
#endif
    }
}