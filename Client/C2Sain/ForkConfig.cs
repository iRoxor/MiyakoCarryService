// ForkConfig.cs -- the fork's BepInEx settings.
//
// Bound once by MiyakoCarryServicePlugin.SetupConfig (plugin start) into MCS's
// own config file, BepInEx/config/top.himesamanoyume.miyakocarryservice.cfg,
// under the section key "McsFork/Section" (shown as "D. C2 / SAIN brains (fork)").
// Display names and descriptions come from ForkText. Read every tick by the
// C2/SAIN classes, so changes apply live. Each entry's default and practical
// effect are described in ForkText.

using BepInEx.Configuration;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Which aggressive SAIN personality the Hyper-aggressive stance lends a bot.</summary>
    public enum ERecklessStyle
    {
        Random,
        GigaChad,
        Wreckless,
    }

    /// <summary>Owner of the fork's settings. Entries are null until <see cref="Bind"/> runs.</summary>
    internal static class ForkConfig
    {
        private const string SECTION = "McsFork/Section";
        private const int SECTION_ORDER = 400;

        public static ConfigEntry<float> IdleRadius;
        public static ConfigEntry<float> IdleSeconds;
        public static ConfigEntry<float> GuardRadius;
        public static ConfigEntry<float> GuardPostMin;
        public static ConfigEntry<float> GuardPostMax;
        public static ConfigEntry<float> CombatEndDelay;
        public static ConfigEntry<float> ChaseMinSeconds;
        public static ConfigEntry<float> ChaseMaxSeconds;
        public static ConfigEntry<float> ReturnRadius;
        public static ConfigEntry<float> FollowDistance;
        public static ConfigEntry<float> BreakOffArriveRadius;
        public static ConfigEntry<float> BreakOffTimeout;
        public static ConfigEntry<float> BreakOffGrace;
        public static ConfigEntry<Core.CarryStance> Stance;
        public static ConfigEntry<bool> PassiveEngagesWhenYouShoot;
        public static ConfigEntry<float> AggressiveLeash;
        public static ConfigEntry<bool> EngageFlanking;
        public static ConfigEntry<ERecklessStyle> RecklessStyle;
        public static ConfigEntry<float> FoliageSightRadius;
        public static ConfigEntry<float> EscortSprintSeconds;
        public static ConfigEntry<float> EscortWalkMinSeconds;
        public static ConfigEntry<float> EscortWalkMaxSeconds;
        public static ConfigEntry<float> EscortWaitDistance;
        public static ConfigEntry<bool> OutOfCombatStims;
        public static ConfigEntry<bool> GapCrossing;
        public static ConfigEntry<bool> McsFastDoors;
        public static ConfigEntry<bool> HearFootsteps;
        public static ConfigEntry<float> GunshotHearingRange;
        public static ConfigEntry<float> FollowFootstepRange;
        public static ConfigEntry<bool> SayIHearSomething;
        public static ConfigEntry<float> PlannerBudgetMs;
        public static ConfigEntry<bool> StrobeThermal;
        public static ConfigEntry<bool> StrobeIr;
        public static ConfigEntry<Core.IrPattern> StrobeIrPattern;
        public static ConfigEntry<float> StrobeThermalPeriod;
        public static ConfigEntry<float> StrobeThermalHeat;
        public static ConfigEntry<float> StrobeHeight;
        public static ConfigEntry<float> StrobeSize;
        public static ConfigEntry<float> StrobeIrIntensity;
        public static ConfigEntry<float> StrobeIrRange;
        public static ConfigEntry<bool> CostPlanner;
        public static ConfigEntry<bool> CostDefend;
        public static ConfigEntry<bool> CostFlank;
        public static ConfigEntry<bool> CostReach;
        public static ConfigEntry<bool> CostFollowSpot;
        public static ConfigEntry<bool> CostFoliageSight;
        public static ConfigEntry<bool> CostDecisions;
        public static ConfigEntry<bool> CostStrobe;
        public static ConfigEntry<float> CostThresholdMs;
        public static ConfigEntry<bool> DebugLog;

        /// <summary>The switch that turns timing of a kind of work on (fork v12; each needs "Debug: diagnostic log" on too).</summary>
        internal static ConfigEntry<bool> CostSwitch(Core.CostKind kind)
        {
            return kind switch
            {
                Core.CostKind.Planner => CostPlanner,
                Core.CostKind.Defend => CostDefend,
                Core.CostKind.Flank => CostFlank,
                Core.CostKind.Reach => CostReach,
                Core.CostKind.FollowSpot => CostFollowSpot,
                Core.CostKind.FoliageSight => CostFoliageSight,
                Core.CostKind.Strobe => CostStrobe,
                _ => CostDecisions,
            };
        }

        /// <summary>
        /// Registers every fork setting with MCS's config helper.
        /// Side effect: BepInEx creates any missing entries in the config file.
        /// </summary>
        /// <param name="plugin">The MCS plugin instance that owns the config file.</param>
        internal static void Bind(MiyakoCarryServicePlugin plugin)
        {
            IdleRadius = Float(plugin, "IdleRadius", 3f, 1f, 10f);
            IdleSeconds = Float(plugin, "IdleSeconds", 3f, 1f, 30f);
            GuardRadius = Float(plugin, "GuardRadius", 15f, 5f, 50f);
            GuardPostMin = Float(plugin, "GuardPostMin", 3f, 1.5f, 8f);
            GuardPostMax = Float(plugin, "GuardPostMax", 12f, 5f, 25f);
            CombatEndDelay = Float(plugin, "CombatEndDelay", 10f, 3f, 60f);
            ChaseMinSeconds = Float(plugin, "ChaseMin", 3f, 0f, 30f);
            ChaseMaxSeconds = Float(plugin, "ChaseMax", 5f, 0f, 60f);
            ReturnRadius = Float(plugin, "ReturnRadius", 6f, 2f, 30f);
            FollowDistance = Float(plugin, "FollowDistance", 8f, 3f, 20f);
            BreakOffArriveRadius = Float(plugin, "BreakOffArrive", 3f, 1.5f, 10f);
            BreakOffTimeout = Float(plugin, "BreakOffTimeout", 20f, 5f, 60f);
            BreakOffGrace = Float(plugin, "BreakOffGrace", 10f, 0f, 60f);
            // Fork v11: the stances replace AllowedPersonalities, EngageMax and ReportEngages (their old
            // entries stay in the file, unread). The Team stance commands write Stance, so it carries over.
            Stance = Enum(plugin, "Stance", Core.CarryStance.Defensive);
            PassiveEngagesWhenYouShoot = Bool(plugin, "PassiveEngagesWhenYouShoot", true);
            AggressiveLeash = Float(plugin, "AggressiveLeash", 100f, 20f, 300f);
            EngageFlanking = Bool(plugin, "EngageFlanking", true);
            RecklessStyle = Enum(plugin, "RecklessStyle", ERecklessStyle.Random);
            FoliageSightRadius = Float(plugin, "FoliageSight", 2f, 0f, 4f);
            EscortSprintSeconds = Float(plugin, "EscortSprint", 3f, 0.5f, 10f);
            EscortWalkMinSeconds = Float(plugin, "EscortWalkMin", 3f, 0f, 20f);
            EscortWalkMaxSeconds = Float(plugin, "EscortWalkMax", 5f, 0f, 20f);
            EscortWaitDistance = Float(plugin, "EscortWait", 20f, 10f, 60f);
            OutOfCombatStims = Bool(plugin, "OutOfCombatStims", false);
            GapCrossing = Bool(plugin, "GapCrossing", false);
            McsFastDoors = Bool(plugin, "McsFastDoors", false);
            HearFootsteps = Bool(plugin, "HearFootsteps", true);
            GunshotHearingRange = Float(plugin, "GunshotRange", 25f, 0f, 300f);
            FollowFootstepRange = Float(plugin, "FollowFootstepRange", 30f, 0f, 70f);
            SayIHearSomething = Bool(plugin, "SayIHearSomething", true);
            PlannerBudgetMs = Float(plugin, "PlannerBudget", 3f, 0.5f, 30f);
            StrobeThermal = Bool(plugin, "StrobeThermal", true);
            StrobeIr = Bool(plugin, "StrobeIr", true);
            StrobeIrPattern = Enum(plugin, "StrobeIrPattern", Core.IrPattern.DoubleFlash);
            StrobeThermalPeriod = Float(plugin, "StrobeThermalPeriod", 2f, 0.5f, 10f);
            StrobeThermalHeat = Float(plugin, "StrobeThermalHeat", 65f, 30f, 120f);
            StrobeHeight = Float(plugin, "StrobeHeight", 0.4f, 0.1f, 1.5f);
            StrobeSize = Float(plugin, "StrobeSize", 0.22f, 0.05f, 0.8f);
            StrobeIrIntensity = Float(plugin, "StrobeIrIntensity", 1.5f, 0.1f, 10f);
            StrobeIrRange = Float(plugin, "StrobeIrRange", 6f, 1f, 20f);
            CostPlanner = Bool(plugin, "CostPlanner", false);
            CostDefend = Bool(plugin, "CostDefend", false);
            CostFlank = Bool(plugin, "CostFlank", false);
            CostReach = Bool(plugin, "CostReach", false);
            CostFollowSpot = Bool(plugin, "CostFollowSpot", false);
            CostFoliageSight = Bool(plugin, "CostFoliageSight", false);
            CostDecisions = Bool(plugin, "CostDecisions", false);
            CostStrobe = Bool(plugin, "CostStrobe", false);
            CostThresholdMs = Float(plugin, "CostThreshold", 2f, 0.5f, 50f);
            DebugLog = Bool(plugin, "DebugLog", false);
        }

        /// <summary>Registers one float setting named McsFork/&lt;name&gt; Key.</summary>
        private static ConfigEntry<float> Float(MiyakoCarryServicePlugin plugin, string name, float def, float min, float max)
        {
            return plugin.Register(SECTION, SECTION_ORDER, $"McsFork/{name} Key", def, $"McsFork/{name} Description",
                new AcceptableValueRange<float>(min, max), needNotify: false);
        }

        /// <summary>Registers one on/off setting named McsFork/&lt;name&gt; Key.</summary>
        private static ConfigEntry<bool> Bool(MiyakoCarryServicePlugin plugin, string name, bool def)
        {
            return plugin.Register(SECTION, SECTION_ORDER, $"McsFork/{name} Key", def, $"McsFork/{name} Description");
        }

        /// <summary>Registers one choice setting (drawn as a drop-down) named McsFork/&lt;name&gt; Key.</summary>
        private static ConfigEntry<T> Enum<T>(MiyakoCarryServicePlugin plugin, string name, T def) where T : struct, System.Enum
        {
            return plugin.Register(SECTION, SECTION_ORDER, $"McsFork/{name} Key", def, $"McsFork/{name} Description", needNotify: false);
        }
    }
}
