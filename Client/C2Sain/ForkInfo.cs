// ForkInfo.cs -- identity of the C2/SAIN fork build.
//
// Read by MiyakoCarryServiceClient.Start (startup log line), ForkDebug (the
// in-raid debug overlay) and, since v4, MenuTaskBarAwakePatch (MCS's debug dump),
// so a log, screenshot or dump names the build it came from. The fork's only
// records are the BepInEx log and that dump, a one-off report per session that
// nobody pools across builds, so one behaviour counter is enough; there is no
// schema version.

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Version stamp of the fork. Bump on any change a player could observe.</summary>
    internal static class ForkInfo
    {
        /// <summary>
        /// Behaviour version of the fork, separate from the upstream MCS version (1.1.4.0).
        ///
        /// Changelog:
        ///  1 (2026-11-04) Phase 2 core: SAIN layers kept (minus Extract/Debug), the game's
        ///    AvoidDanger and Malfunction layers kept, a priority-19 Guard layer added, team modes
        ///    C2 / Guard / Combat, the SAIN decision leash (3-5 s chases), Break off from combat,
        ///    gap crossing and MCS fast doors off by default, follow spots must be in the player's
        ///    line on the navmesh, "too far" fixed (was ~4.5 m). Measured: nothing yet; this is
        ///    the first in-raid test build. Falsifier: if guard/combat hand-offs make bots freeze
        ///    or flap between layers in the user's raid test, the gate in C2Gate goes back.
        ///  2 (2026-11-04) Test build 2, from the user's raid on bigmap with build 1 (debug dump
        ///    12-51-50: McsGuardLayer chosen 3103 times, SAIN Combat Layer 634, C2Sain:ToGuardSpot
        ///    1586, C2Sain:Watch 1453, one NullReferenceException from MCS's CalcGoalEnemy in
        ///    EnemyInfo.SetVisible; the user saw no freezes or layer flapping, so build 1's
        ///    falsifier did not fire). Added: Engage Enemy (SAIN's Chad personality, flanking,
        ///    covering fire), Engage Enemy Wrecklessly (GigaChad or Wreckless), "I see the enemy"
        ///    (contact report: guard facing the mark, enemy handed to SAIN as a squadmate
        ///    sighting), a carry-bot personality setting (one of the user's four bots rolled
        ///    Coward), bush concealment for guard spots and a see-out radius for foliage around
        ///    carry bots, Escort pacing (3 s sprints, 3-5 s watchful walks, waits for the player),
        ///    out-of-combat stims off by default, On Your Own removed, Clear Area renamed Clear
        ///    Target Area and refused in contact, MCS's enemy sharing turned into SAIN squadmate
        ///    sightings. Falsifiers: an Engage order that leaves a bot more than 60 m from the
        ///    player after it ends, or carry bots shooting through bushes farther away than the
        ///    see-out radius, sends that part back.
        ///  3 (2026-11-04) Test build 3, from the user's raid with build 2 ("performing
        ///    phenomenally"; a Goons fight cost 2 of 4 carry bots, which the user wanted: a two-way
        ///    fight). Neither build-2 falsifier was reported. Their notes: guards bunched on one side
        ///    of the player, hugged the player too closely to use cover right beside them, and copied
        ///    the player's crouch during Clear Area. Traced: each bot picked its own spot in its own
        ///    sector and, when that sector had no usable spot, fell back to a follow spot on the side
        ///    it stood on (bots trail the player, so all landed behind); and one ray from a random
        ///    point rarely hits a tree trunk. Changed: guard posts are planned per squad
        ///    (GuardPlanner): 2+ bots outdoors form an even detail 3-12 m out (setting), behind trees,
        ///    rocks, walls, vehicles or bushes when near; indoors they post on the busiest approaches
        ///    (routes traced to 12-35 m away, other floors included); a lone bot watches the
        ///    player's back; contact reports still fan toward the mark. Lean-around cover (a trunk
        ///    or rock) now scores as full cover. The player's pose is copied only while following or
        ///    escorting (MovePolicy), so Escort crouch-walks with a crouching player and Clear Area,
        ///    Go To Point, proxy tasks and Break off never copy it. Falsifiers: with 2+ bots, guards
        ///    still ending up together on one side outdoors, or indoor posts inside side rooms rather
        ///    than on hallways and stairs, sends the planner back for rework.
        ///  4 (2026-11-04) Test build 4: a crash fix and diagnostics; guard behaviour is unchanged
        ///    from v3. From the user's Woods raid with v3 (debug dump 16-31-59; the game's errors.log,
        ///    16:07:15, about 10 minutes in): one NullReferenceException in the game's
        ///    BotCalcGoal.CalcGoalForBot under MCS's CalcGoalEnemy, the same stack as the one in v1's
        ///    dump. Traced: MCS's squad list still returns dead carry bots; the game disposes a dead
        ///    bot's BotOwner (EnemyChooser set to null) and SAIN drops it, so the fork's sighting
        ///    hand-over declined it and upstream's TryRegisterEnemyForFollower ran the game's goal
        ///    calculation on the disposed bot. The exception ended MCS's once-a-second sighting
        ///    coroutine for the rest of the raid: no more of the player's sightings, aiming-enemy
        ///    marks or dead-enemy cleanup after the first carry bot died. Fixed: dead and shut-down
        ///    bots are skipped (McsAILeadPlayer.IsShutDown), and the loop survives any later exception,
        ///    logged at most once per 30 s. The user also still sees carry bots standing in the open,
        ///    and the dump's bot-second counts cannot say in which state (C2Sain:ToGuardSpot 248,
        ///    C2Sain:WatchFromCover 218, Mcs:Default 92, C2Sain:Watch 45), so this build only adds
        ///    diagnostics: the dump gains "Still In The Open (s)" by reason and names the fork build,
        ///    and the diagnostic log (renamed from "log mode changes"; same config key) gains every
        ///    guard post with its cover and path, plus a line when a bot has stood still in the open
        ///    for 5 s (OpenGroundWatch). The dump's Used Brain/Layer/Reason counts keep their meaning,
        ///    so comparing them with v3 dumps is unrestricted. Falsifier: the same exception
        ///    (BotCalcGoal.CalcGoalForBot under TryRegisterEnemyForFollower) in any later dump means
        ///    the shut-down test is wrong.
        ///  5 (2026-11-04) Test build 5, from the user's two Customs raids with v4 (copies in
        ///    miyako-carry-service/evidence/2026-11-04-build-4-raids; the user saw "no issues that
        ///    stand out"). v4's falsifier did not fire: three carry bots died, and none of the game's
        ///    12,926 error entries passed through MCS or the fork. The diagnostic log showed 21 runs of
        ///    a bot still in the open for 5 s or more, 305 s in all. Of that, 212 s (9 runs) was
        ///    C2Sain:Watch at posts planned without cover, the bot standing upright, and a quarter of
        ///    detail posts (106 of 430) were planned bare. Traced: the planner tried a post's wider
        ///    sectors (25, 50, 80 degrees) only when a narrower one had no spot at all, never because
        ///    it had no cover, and bots stood upright at bare posts. Changed: detail and
        ///    contact-report posts try wider sectors until one offers cover and take the narrowest
        ///    covered spot (GuardFormation.ChooseWindow), and bots crouch at every guard post
        ///    (GuardWatchLogic). The diagnostic log also names each loot target a bot picks for itself
        ///    (what, why, whether on a body, in a container or on the ground, how far) and whether it
        ///    took it (LootLog). Emergency resupply already searched bodies within 30 m; the user
        ///    chose to keep it running whatever Enable Looting says.
        ///    Pooling: the open-ground numbers (dump and log) are measured as in v4, so compare v4
        ///    with v5 directly, but do not add them together, since the guard behaviour they measure
        ///    changed. Other dump counts are unaffected.
        ///    Falsifiers, from raids with the diagnostic log on: C2Sain:Watch at bare posts still half
        ///    or more of the open-ground time, or more than a fifth of detail posts still planned
        ///    bare, sends the widening back for rework. Crouched guards reacting late to enemies the
        ///    user saw (an in-raid report) sends the crouch at bare posts back.
        /// </summary>
        public const int ForkVersion = 5;

        /// <summary>Short label used in logs and the overlay.</summary>
        public const string Label = "C2/SAIN fork v5";
    }
}
