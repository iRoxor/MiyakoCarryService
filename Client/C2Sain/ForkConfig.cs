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
    /// <summary>Owner of the fork's settings. Entries are null until <see cref="Bind"/> runs.</summary>
    internal static class ForkConfig
    {
        private const string SECTION = "McsFork/Section";
        private const int SECTION_ORDER = 400;

        public static ConfigEntry<float> IdleRadius;
        public static ConfigEntry<float> IdleSeconds;
        public static ConfigEntry<float> GuardRadius;
        public static ConfigEntry<float> CombatEndDelay;
        public static ConfigEntry<float> ChaseMinSeconds;
        public static ConfigEntry<float> ChaseMaxSeconds;
        public static ConfigEntry<float> ReturnRadius;
        public static ConfigEntry<float> FollowDistance;
        public static ConfigEntry<float> BreakOffArriveRadius;
        public static ConfigEntry<float> BreakOffTimeout;
        public static ConfigEntry<float> BreakOffGrace;
        public static ConfigEntry<bool> GapCrossing;
        public static ConfigEntry<bool> McsFastDoors;
        public static ConfigEntry<bool> DebugLog;

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
            CombatEndDelay = Float(plugin, "CombatEndDelay", 10f, 3f, 60f);
            ChaseMinSeconds = Float(plugin, "ChaseMin", 3f, 0f, 30f);
            ChaseMaxSeconds = Float(plugin, "ChaseMax", 5f, 0f, 60f);
            ReturnRadius = Float(plugin, "ReturnRadius", 6f, 2f, 30f);
            FollowDistance = Float(plugin, "FollowDistance", 8f, 3f, 20f);
            BreakOffArriveRadius = Float(plugin, "BreakOffArrive", 3f, 1.5f, 10f);
            BreakOffTimeout = Float(plugin, "BreakOffTimeout", 20f, 5f, 60f);
            BreakOffGrace = Float(plugin, "BreakOffGrace", 10f, 0f, 60f);
            GapCrossing = Bool(plugin, "GapCrossing", false);
            McsFastDoors = Bool(plugin, "McsFastDoors", false);
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
    }
}
