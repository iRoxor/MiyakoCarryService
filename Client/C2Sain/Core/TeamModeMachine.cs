// TeamModeMachine.cs -- which brain a carry-bot squad should be using right now.
//
// One instance per player (squad), owned by C2Sain/TeamModes.cs, which ticks it
// about five times a second from the bots' brain layers. Unity-free so the logic
// harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it with
// scripted positions and times. Reads and writes no files.
//
// Modes, in the order the tick resolves them:
//   Combat  -- someone was engaged within CombatEndDelay seconds. SAIN fights.
//   Defend  -- the Defend Me zone exists (radius DefendRadius around where the
//              command was given). Bots hold spots around the player.
//   Guard   -- the player stayed within IdleRadius for IdleSeconds; the zone is
//              anchored there with radius GuardRadius. Bots guard the player.
//   C2      -- none of the above: the Command-and-Control brain follows and
//              runs commands.
// Leaving a zone (Guard or Defend) removes it, which is also how Defend Me ends.

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>The brain a squad should use. See the file header for the rules.</summary>
    public enum TeamMode
    {
        C2,
        Guard,
        Combat,
        Defend,
    }

    /// <summary>
    /// Tunables for <see cref="TeamModeMachine"/>. The plugin copies them from its
    /// BepInEx settings before every tick, so changes apply live.
    /// </summary>
    public sealed class TeamModeSettings
    {
        /// <summary>Metres the player may drift and still count as standing still.</summary>
        public float IdleRadius = 3f;

        /// <summary>Seconds the player must stay inside IdleRadius before Guard starts.</summary>
        public float IdleSeconds = 3f;

        /// <summary>Radius of the Guard zone in metres; leaving it ends Guard.</summary>
        public float GuardRadius = 15f;

        /// <summary>Radius of the Defend Me zone in metres; leaving it cancels Defend Me.</summary>
        public float DefendRadius = 5f;

        /// <summary>Seconds after the last engagement before Combat ends.</summary>
        public float CombatEndDelay = 10f;
    }

    /// <summary>Per-squad mode state. Not thread-safe; the game calls it from the main thread only.</summary>
    public sealed class TeamModeMachine
    {
        private V3 _idleAnchor;
        private float _idleSince;
        private bool _idleInitialised;
        private float _lastEngagedTime = float.NegativeInfinity;

        /// <summary>The mode decided by the most recent <see cref="Tick"/>.</summary>
        public TeamMode Mode { get; private set; } = TeamMode.C2;

        /// <summary>True while a Guard or Defend zone exists.</summary>
        public bool HasZone { get; private set; }

        /// <summary>Centre of the current zone. Meaningful only while <see cref="HasZone"/>.</summary>
        public V3 ZoneAnchor { get; private set; }

        /// <summary>Radius of the current zone in metres.</summary>
        public float ZoneRadius { get; private set; }

        /// <summary>True when the current zone came from the Defend Me command.</summary>
        public bool ZoneIsDefend { get; private set; }

        /// <summary>Game time of the last tick that reported an engagement.</summary>
        public float LastEngagedTime => _lastEngagedTime;

        /// <summary>
        /// Places the Defend Me zone at the player's position, replacing any Guard zone.
        /// </summary>
        /// <param name="playerPos">Where the player stood when the command was given.</param>
        /// <param name="settings">Supplies DefendRadius.</param>
        public void StartDefend(V3 playerPos, TeamModeSettings settings)
        {
            HasZone = true;
            ZoneAnchor = playerPos;
            ZoneRadius = settings.DefendRadius;
            ZoneIsDefend = true;
        }

        /// <summary>Removes a Defend Me zone, if one exists. A Guard zone is left alone.</summary>
        public void CancelDefend()
        {
            if (ZoneIsDefend)
            {
                HasZone = false;
                ZoneIsDefend = false;
            }
        }

        /// <summary>
        /// Advances the state and returns the mode for this moment.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="playerPos">The player's current position.</param>
        /// <param name="engagedNow">True if the player or any carry bot is engaged right now.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        /// <returns>The new <see cref="Mode"/>.</returns>
        public TeamMode Tick(float now, V3 playerPos, bool engagedNow, TeamModeSettings settings)
        {
            if (engagedNow)
            {
                _lastEngagedTime = now;
            }

            if (!_idleInitialised || V3.Distance(playerPos, _idleAnchor) > settings.IdleRadius)
            {
                _idleAnchor = playerPos;
                _idleSince = now;
                _idleInitialised = true;
            }

            if (HasZone && V3.Distance(playerPos, ZoneAnchor) > ZoneRadius)
            {
                HasZone = false;
                ZoneIsDefend = false;
                _idleAnchor = playerPos;
                _idleSince = now;
            }

            if (!HasZone && now - _idleSince >= settings.IdleSeconds)
            {
                HasZone = true;
                ZoneAnchor = _idleAnchor;
                ZoneRadius = settings.GuardRadius;
                ZoneIsDefend = false;
            }

            var inCombat = now - _lastEngagedTime <= settings.CombatEndDelay;
            if (inCombat)
            {
                Mode = TeamMode.Combat;
            }
            else if (HasZone)
            {
                Mode = ZoneIsDefend ? TeamMode.Defend : TeamMode.Guard;
            }
            else
            {
                Mode = TeamMode.C2;
            }
            return Mode;
        }
    }
}
