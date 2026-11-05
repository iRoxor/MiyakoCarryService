// ForkInfo.cs -- identity of the C2/SAIN fork build.
//
// Read by MiyakoCarryServiceClient.Start (startup log line), ForkDebug (the
// in-raid debug overlay), since v4 MenuTaskBarAwakePatch (MCS's debug dump) and,
// since v7, ForkLog (the tag on every diagnostic line, with the raid clock), so a
// log, screenshot or dump names the build it came from. The fork's only
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
        ///  6 (2026-11-04) Test build 6, built while the user was away, before any raid with v5. v5's
        ///    changes ship in this build too, so its falsifiers carry over.
        ///    Added: Defend Me (Team > Positions), to the user's design in the fork plan. It places the
        ///    squad's Defend zone (5 m) at the player (TeamModes.StartDefend). Each bot takes a spot 3-5 m
        ///    out in its own sector, behind cover where there is any (GuardPlanner.PlanDefend). A bot at
        ///    its spot rolls every 2-3 s: 25% to shift to another spot in its sector (80% for the last bot
        ///    alive), on a walk of at most 6 m, about 3 s, while watching outward (DefendShuffle,
        ///    ShiftDefend). In combat SAIN fights as usual and the spots resume afterwards. Leaving the
        ///    zone or Regroup ends it.
        ///    Changed: the command menus are grouped into submenus. Team > Combat / Positions / Tasks /
        ///    Reports / Settings; member > Combat / Positions / Tasks / Reports / Gear and aiming, plus
        ///    Exclude / Take Over. Every command keeps its behaviour.
        ///    Fixed: v5's wider guard sectors (up to 80 degrees) could put a post straight ahead of the
        ///    player again. Outdoor posts and Defend spots now keep 20 degrees clear of where the player
        ///    looks (of the reported enemy, for posts facing a contact report;
        ///    GuardFormationSettings.LineOfFireClearance).
        ///    Measured: nothing yet; no raid has run v5 or v6.
        ///    Pooling: guard plans and open-ground numbers stay comparable with v5. Defend Me has its own
        ///    plan header ("defend plan for") and reasons (C2Sain:ToDefendSpot, C2Sain:Defend,
        ///    C2Sain:DefendFromCover), so leave those out when comparing guard numbers with v5.
        ///    Falsifiers: Defend bots more than 6 m from a player standing still, a bot never shifting
        ///    in a minute with squadmates alive, or a Defend bot in the player's line of fire, sends
        ///    Defend Me back. Any command missing from the menus, or behaving differently from v5,
        ///    means the menu change broke something.
        ///  7 (2026-11-05) Test build 7, from the user's Customs raid with v6 (logs in
        ///    miyako-carry-service/evidence/2026-11-05-build-6-raid, Raid Review export in
        ///    user-added-files/analyzed). The user flagged 04:14-04:20, a carry bot outside the room
        ///    they healed in while a Reshala guard walked past it and up the stairs; they had been
        ///    seeing views "glued to a wall".
        ///    Measured in the export: the bot reached its spot at 04:10.8, then kept head and body at
        ///    198.0 deg, its last walking direction, until 04:22.8, while the guard passed 155-180
        ///    deg off that view at 12-14 m. The same raid's log has a 24 s C2Sain:ToGuardSpot stand
        ///    1.3 m from a post, and v4's had one 2.0 m short. Across the whole export, read with
        ///    this build's frozen-view rule (tools/raid_review_summary.py): 39 runs of 8 s or more,
        ///    570 s; 492 s of them while a fork layer drove, 25% of the carry bots' 32.5 minutes not
        ///    under SAIN, longest 35 s. By the log's squad mode: 350 s in Guard, 72 s in Combat, 38 s
        ///    in Defend, 33 s in C2.
        ///    Traced: the game ends a walk at its own reach distance (BotGoToPointData.IsCome), but
        ///    McsGuardLayer counted a bot as arrived only within 1.25 m. A bot stopped short stayed
        ///    in GuardMoveLogic, which only looks where the bot is moving.
        ///    Changed:
        ///    - a bot that stalls (no 0.3 m of progress in 2 s) within 3 m of its spot counts as
        ///      arrived (Core/ArrivalWatch.cs);
        ///    - a stalled walk looks around instead of staring ahead;
        ///    - the watch scan tries five offsets per look and takes the most open sight line, and
        ///      checks behind every fourth look (Core/LookScan.cs).
        ///    Diagnostics, at the user's request: every fork log line carries the raid clock, started
        ///    where Raid Review starts its own (ForkLog). The diagnostic log adds each carry bot's
        ///    brain-layer changes with SAIN's decision (BrainWatch), frozen views of 8 s or more with
        ///    what the guard logics were turning the head toward (FrozenViewWatch,
        ///    BotForkState.LookSource; also a "Frozen View (s)" dump line), stall arrivals and
        ///    deaths. SAIN's own decision logging exists only in its debug builds, and BigBrain logs
        ///    no layer changes.
        ///    Pooling: the dump's other counts keep their meaning. Log lines now carry a clock and the
        ///    summarizers read both forms. Open-ground and frozen-view times compare across builds,
        ///    but v7's arrival rule ends ToGuardSpot stands sooner, so ToGuardSpot open-ground time is
        ///    not comparable with v6.
        ///    Falsifiers, from raids with the diagnostic log on and their Raid Review exports:
        ///    - a frozen view of 10 s or more in C2Sain:ToGuardSpot, ToDefendSpot or Return within 3 m
        ///      of the spot, the head "where it walks", sends the arrival rule back;
        ///    - a frozen view while "scanning" longer than 13 s (three looks of at most 4 s, then a
        ///      check behind), or more than two of 10 s or more in a raid, sends the scan back. Runs
        ///      facing the squad's threat or a contact report are by design and do not count;
        ///    - in the export, frozen views while McsGuardLayer drove (the runs raid_review_summary.py
        ///      labels "fork: McsGuardLayer") above 8% of the carry bots' McsGuardLayer time (the log
        ///      summary's brain timeline) mean the look fixes fell short of v6's quarter;
        ///    - bots leaving their posts early (an arrival line far more than 1.25 m from the spot while
        ///      the path was clear) sends the 3 m radius back.
        /// </summary>
        public const int ForkVersion = 7;

        /// <summary>Short label used in logs and the overlay.</summary>
        public const string Label = "C2/SAIN fork v7";
    }
}
