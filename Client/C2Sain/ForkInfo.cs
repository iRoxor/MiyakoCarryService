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
        ///    Correction (2026-11-08): Raid Review's "dir" equals its "bodyYaw" in every sample (about
        ///    290,000 checked), so the export figures above are BODY facing, not where the head looked:
        ///    "kept head and body at 198.0 deg" was the body only, and the 25% baseline is a body-facing
        ///    share. The fork's own frozen-view lines read the real view direction. The third falsifier
        ///    is read with the fork's lines instead (see v8).
        ///  8 (2026-11-08) Test build 8: hearing, from the plan's phase 3 and the user's 12 raids with v7
        ///    (11-05 to 11-08; evidence in miyako-carry-service/evidence/2026-11-08-build-7-raids).
        ///    v7 measured, from the fork's own view lines in the 4 raids that kept a log: the scan never
        ///    froze, and no v7 falsifier fired (the one 14 s walking stare was 15 m from its spot, a
        ///    creep, not a stall). What still stared: upstream Hold Position (285 s in the 11-05 Woods
        ///    raid, one run 167 s), and the whole squad facing one threat point (301 s, runs of 20-28 s,
        ///    every guard toward the same spot). Body-facing share in the exports: 25.2% (v6) to 9.4%.
        ///    Changed:
        ///    - AI footsteps: SAIN makes none for AI players, so the fork makes them (FootstepFeed,
        ///      Core/Hearing.cs FootstepCadence) and hands them to carry bots only; SAIN decides per bot
        ///      whether each is heard. While following, only steps within 30 m are handed over.
        ///    - Gunshots beyond 25 m are not heard by carry bots (SainGunshotRangePatch), so a distant
        ///      fight does not pull them away; bullets near them still count.
        ///    - A heard, unseen enemy (SainHeardPatch, HeardWatch): the bot that heard it turns to it; the
        ///      place is a squad look target for 8 s; while following, the squad halts in Guard with
        ///      posts in cover facing the sound for 15 s (TeamModes.AlertHeard); "I hear something."
        ///      once per 20 s per squad, never in Combat. Chasing heard-only enemies stays vetoed.
        ///    - Only guards whose sector covers a threat face it; the closest faces it when none does
        ///      (Core/Hearing.cs ThreatSectors, 80 degrees either side).
        ///    - The frozen-view end line names the ended run's direction (FrozenViewTracker.EndedRunYaw);
        ///      v7 printed the next run's.
        ///    Diagnostics: a "heard:" line per heard enemy (rate-limited), and plan headers say "facing
        ///    a heard sound" for an alert. Settings: Hearing: AI footsteps / gunshot range / footsteps
        ///    while following / say "I hear something.".
        ///    Pooling: guard plans, open-ground and frozen-view numbers stay comparable with v7, except
        ///    that hearing now starts alerts and threat looks that v7 never had: compare raids, do not
        ///    add v7 and v8 frozen-view seconds together. Gunshot-driven reactions beyond 25 m drop by
        ///    design.
        ///    Falsifiers, from raids with the diagnostic log on:
        ///    - no "heard:" line in a raid where AI enemies came within 30 m of the squad unseen, or the
        ///      user hearing footsteps the bots ignored, sends the footstep feed back;
        ///    - the squad stopping more than about once a minute while following with no enemy found, or
        ///      the user calling the halts annoying, sends the follow alert back (or its range down);
        ///    - a frozen view facing the squad's threat that lasts 20 s or more with every living guard
        ///      facing it at once, sends the sector rule back;
        ///    - a carry bot killed from behind by an enemy it had heard (a "heard:" line for that enemy
        ///      within 10 s before the death) means the turn toward heard enemies is too slow or too
        ///      short.
        ///    Result (2026-11-09, two Lighthouse raids; evidence/2026-11-09-build-8-lighthouse and
        ///    -lighthouse-2): 78 "heard:" lines, 9 of them footsteps at 8-18 m, which SAIN never makes
        ///    for AI, so the feed works end to end. No falsifier fired: no halts while following (every
        ///    footstep came in Guard, Defend or Combat), and the four carry-bot deaths were a 52 m
        ///    headshot and three grenades, none a shot in the back after a heard warning.
        ///  9 (2026-11-09) Test build 9: the indoor planner (GuardPlanner.TryApproachPosts,
        ///    Core/IndoorApproaches.cs), the user's HIGH priority from 2026-11-08.
        ///    Measured before it, from the indoor guard plans in the build 7 and 8 logs (evidence/
        ///    2026-11-08-build-7-raids, 2026-11-09-build-8-lighthouse-2): 86 of 317 bot places (27%)
        ///    got no post (v7 67 of 207, v8 19 of 110), so those bots just followed; the lone bot got no
        ///    post in 41 of 65 indoor plans (v7); and 105 of 173 approach posts had no cover.
        ///    Cause: an approach was the point 6 m along each route, usually past the doorway, so routes
        ///    through one door split into several approaches, and posts were searched on the straight
        ///    line toward that point, often through a wall.
        ///    Changed:
        ///    - Each route is walked in 0.75 m steps to where it leaves the player's space (out of the
        ///      player's navmesh line and line of sight, or more than 1.5 m up or down) and stays out for
        ///      2 m, then moved back to its narrowest point within 4 m when that is 1.8 m wide or less
        ///      (the doorway). Approaches are classed doorway, hallway, stairs or opening.
        ///    - A room where 60% of routes stay in view is open: the even detail is used, as outdoors.
        ///    - Guards go to the busiest approaches, an approach the player looks at counting half; a
        ///      spare bot doubles the busiest approach from the other side before a minor approach
        ///      (under 15% of routes) gets one.
        ///    - A post is 1.5-6 m from its approach on the room's side, beside the opening (20-55
        ///      degrees off its axis scores best), with a view of it at crouched eye height, not
        ///      through the player, and preferring cover and a wall beside it. It watches the opening.
        ///    - The lone bot indoors uses the same plan (the approach the player is not watching);
        ///      the old back-watching spot is the fallback.
        ///    Diagnostics: after each indoor guard plan, an "indoor approaches:" line with routes
        ///    traced, left and stayed in view, each approach (kind, routes, distance, bearing, guarded,
        ///    or why not with the failure counts), any fallback, and the time taken. The summary tool
        ///    prints these from v9.
        ///    Pooling: outdoor plans are unchanged and pool with v8. Indoor post counts do not pool
        ///    with v8 for kinds: lone bots indoors now report "approach" posts, not "solo". Indoor
        ///    no-post and open-ground rates compare across the boundary but must not be added together.
        ///    Falsifiers, from raids with the diagnostic log on:
        ///    - bots without a post in indoor guard plans at 14% or more of bot places (half of v7-v8's
        ///      27%) sends the route walk back;
        ///    - "using the even detail" in more than half of the indoor plans means the room test or
        ///      the exit rule is wrong;
        ///    - a median plan time above 30 ms, or the user noticing a hitch when the squad settles
        ///      indoors, sends MAX_ROUTES or the sampling down;
        ///    - the user seeing guards posted in the next room, facing a wall, or standing in a doorway
        ///      rather than beside it, sends the post scoring back.
        ///    Result (2026-11-09, its first raid, Lighthouse; evidence/2026-11-09-build-9-stuck-spawn):
        ///    no evidence either way. Every one of the raid's 15 guard plans was outdoors, so the indoor
        ///    planner never ran. That raid is the reason for v10.
        ///    Result (2026-11-09, two Reserve raids with v11; evidence/2026-11-09-build-11-reserve): 229
        ///    indoor approach plans, 176 used the approaches and 53 fell back to the even detail (30 open
        ///    room, 10 no post found, 10 too few routes, 3 no way out found). About 12% of indoor guard
        ///    places got no post (77 of 619 by the plan headers; falsifier at 14%). Median plan time 22 ms,
        ///    max 33 (falsifier: a median over 30 ms). No v9 falsifier fired, but each plan ran inside one
        ///    frame, 7-14 times a minute: that cost is what v12 spreads over frames.
        /// 10 (2026-11-09) Test build 10: bots stuck from spawn when the player stands off the navmesh.
        ///    Measured before it (evidence/2026-11-09-build-9-stuck-spawn, one Lighthouse raid ended by
        ///    Alt+F4): Infil spawned the player at point #13 ("Tunnel"). The 3 carry bots started 30 m away
        ///    on one spot (every earlier raid logged 0 m) and stayed there, crouched, the whole raid,
        ///    through teleport, regroup and the player walking off. All 15 guard plans had 0 posts.
        ///    Reading (inferred from the log, not proven): the player was off the navmesh. MCS sets a
        ///    follow target only when it finds a path to the player, and otherwise holds position; the
        ///    guard planner needs the player within 1.5 m of the navmesh.
        ///    Changed:
        ///    - Follow (McsBaseLayer.UpdateLeadNearMoveTarget, which also serves a guard bot with no
        ///      post): when neither a follow spot nor a path to the player works, the bot walks to the
        ///      closest spot it can reach toward the walkable point nearest the player (searched to
        ///      32 m), if that is at least 2 m closer than it is (C2Nav.TryClosestReachable,
        ///      Core/OffMesh.cs). Upstream held position for good.
        ///    - Teleport (CommandMgr.ForceTeleportCommandAction): with the player off the navmesh, the
        ///      bot lands on the walkable point nearest the player instead of at their feet, where it
        ///      could never move again.
        ///    Diagnostics: "off navmesh:" lines when the player has been off the navmesh for 3 s and
        ///    when they are back (OffMeshWatch; written whether or not the diagnostic log is on); with
        ///    the diagnostic log on, "reach:" lines for a bot's first fallback walk or its staying put,
        ///    a "teleport:" line, and "; you are off the navmesh (...)" on a plan header with fewer
        ///    posts than bots. The summary tool prints these from v10.
        ///    Pooling: UNRESTRICTED for everything earlier builds measure. The fallback runs only where
        ///    earlier builds left a bot with no move target at all, and no existing line changed format
        ///    except that plan-header suffix, which appears only with the player off the navmesh.
        ///    Falsifiers:
        ///    - a raid at Infil's Lighthouse point #13 (the Infil "Spawning at #13" line) with no
        ///      "off navmesh:" line means the player was on the navmesh there and the reading above is
        ///      wrong: look for another cause of the stuck bots;
        ///    - the user seeing a bot walk away from them, or a "reach:" walk whose spot is farther from
        ///      the player than the bot started, sends the fallback back;
        ///    - bots that again sit still for a whole raid with "reach: ... stays put: no path from the
        ///      bot" mean the bots themselves were off the navmesh (spawn snapping), which v10 does not fix.
        ///    Result (2026-11-09, one Factory raid with v10; evidence/2026-11-09-build-10-factory): no
        ///    "off navmesh:" episode. The bots spawned inside the chemical vat, a navmesh piece with no
        ///    way out that the user traced to the Expanded NavMesh mod, not MCS. v10 logged 3 "reach:"
        ///    walks from 7 m toward a spot 2 m from the player, which (a partial path ends on the bot's
        ///    own navmesh piece) was presumably still inside the vat; the user's teleport freed them.
        ///    No falsifier fired. The same raid was build 9's first indoor test: 12 of 137 indoor guard
        ///    places (9%) got no post (v7-v8 27%; falsifier at 14%), 5 of 73 indoor plans fell back to
        ///    the even detail, median plan time 20 ms (max 28). No v9 falsifier fired. Not a falsifier
        ///    but noted: 106 of 110 approach posts had no cover (v7-v8 105 of 173).
        /// 11 (2026-11-09) Test build 11: combat stances (Core/Stance.cs), the first part of the command
        ///    overhaul the user specified on 2026-11-08, with their answers of 2026-11-09.
        ///    Changed:
        ///    - Four stances replace Engage Enemy, Engage Enemy Wrecklessly, the allowed-personalities
        ///      setting, "Engage: longest order" and "I see the enemy: also engage". A Stance submenu
        ///      under Combat sets them: from the Team menu for the squad (saved to the "Stance" setting,
        ///      so it carries over), from a bot's own menu for that bot this raid only.
        ///    - Passive: SAIN's decisions become None (hold fire, let enemies pass) until a carry bot or
        ///      the player is shot at, or the player fires at an enemy ("Passive fights when you shoot",
        ///      default on); then the squad fights until it leaves combat. Normal personality.
        ///    - Defensive (the default): v10's behaviour, Normal personality, no grenades.
        ///    - Aggressive: Chad, the old Engage Enemy tactics (flank legs, covering fire) whenever SAIN
        ///      fights, chasing free within 100 m of the player ("Aggressive leash"), then back to 50 m.
        ///    - Hyper-aggressive: GigaChad or Wreckless, the old reckless rush, no leash.
        ///    - Grenades only in Aggressive and Hyper-aggressive (SainGrenadePatch on SAIN's
        ///      GrenadeThrowDecider.GetDecision, plus a ThrowGrenade-decision backstop).
        ///    - Hold Position now also binds the aggressive stances (an Engage order used to ignore it).
        ///    - Only a flank leg keeps the C2 layer off regardless of squad mode (an Engage order did for
        ///      its whole length), so Aggressive bots still follow out of combat.
        ///    Diagnostics (diagnostic log): each bot's stance at its first decision and every change,
        ///    stance orders, Passive releases with their cause and returns to holding fire, the first
        ///    held grenade throw per bot per stance, and the stance on "chase cut" lines. The summary
        ///    tool prints these from v11.
        ///    Pooling: a Defensive-only raid pools with v10 for everything v10 measures, except
        ///    personality counts (v10 kept SAIN's roll among Normal, Chad, GigaChad and Wreckless; v11
        ///    Defensive is Normal for every bot) and grenade throws (none now). Raids with any other
        ///    stance do not pool with earlier builds for chase cuts, distances from the player or
        ///    combat outcomes; split them by stance.
        ///    Falsifiers:
        ///    - a carry bot throwing a grenade in Passive or Defensive (seen, or a carry-bot grenade in
        ///      Raid Review while every bot was in those stances) means a throw path the patch misses;
        ///    - a Passive squad shooting with no "passive: ... released" line before it, or never
        ///      returning fire after a "released" line, sends the Passive gate back;
        ///    - an Aggressive bot logged chasing beyond 110 m of the player, or bots in any stance no
        ///      longer following the player out of combat, sends the leash or the C2 gate change back;
        ///    - the user finding Defensive fights noticeably different from v10 (other than no grenades
        ///      and every bot on Normal) means a v11 change leaked into Defensive.
        ///    Result (2026-11-09, two Reserve raids with v11; evidence/2026-11-09-build-11-reserve): the
        ///    user found Defensive and Aggressive working; Passive was not tried (no "passive:" line). All
        ///    16 squad stance orders applied (Aggressive from 05:18 to 16:15 in one raid, from 22:08 in the
        ///    other). The grenade patch held 6 throws, 2 for each of the 3 bots, in Defensive. All 27 chases
        ///    cut were in Defensive, at 43 m or less; none was cut in Aggressive, which logs a cut only past
        ///    its 100 m leash. Three carry bots died (13:39 in one raid; 06:43 and 22:20 in the other). No
        ///    v11 falsifier fired. The same raids' stutter check is what led to v12.
        /// 12 (2026-11-18) Test build 12: the guard planner stops holding a frame, the fork times its own
        ///    work, and carry bots wear markers that show in ECOTI's thermal view and in night vision.
        ///    Measured before it:
        ///    - Two Reserve raids with v11 (evidence/2026-11-09-build-11-reserve; the user felt
        ///      micro-stutters at fight moments): 229 indoor plans, median 22 ms, max 33, each inside one
        ///      frame (a frame holds 16-25 ms), 7-14 a minute while the squad guarded or fought, mostly
        ///      from the player moving 4 m.
        ///    - The Frame Hitch Recorder's five record files (stutter-investigation/evidence, 2026-11-11 to
        ///      2026-11-17). Seven calls of MiyakoCarryServiceClient.Update took 40 ms or more (43-177 ms).
        ///      All came 91-1438 s into a raid, none at its start, so CommandMgr.OnRaidStarted's navmesh
        ///      triangulation is not the cause; each sat in a frame with exactly one garbage collection (the
        ///      managed heap shrank 24-738 MB). That Update is four per-frame methods of the plugin
        ///      (GameLoop, CommandMgr, FormationDataMgr, HighlightMgr), so it says a collection began
        ///      there, not that the fork's code was slow. Nine BotOwner.UpdateManual calls of 20 ms or more
        ///      on carry bots: five in load frames of 475-747 ms (bots spawning), two with a collection in
        ///      the frame (86 and 108 ms) and two in ordinary frames, 26.7 ms (Woods, 2079 s; no guard plan
        ///      in the fork's log within a minute either side, so not the planner) and 46.1 ms (Lighthouse,
        ///      198 s; the diagnostic log was off). Nothing in the records ties a carry bot's long update to
        ///      the planner; the timing below is how to find out.
        ///    Changed:
        ///    - The guard planner is made in small steps (GuardPlanner.PlanSteps) that TeamModes.Pump runs
        ///      3 ms a frame, called from GameLoop.Update (setting "Guard: planning time per frame";
        ///      Core/PlanSlicer.cs, Core/PlanScheduler.cs). Until the finished plan arrives (7-9 frames for
        ///      an indoor plan by the planner harness's cost model, not measured) the bots keep their
        ///      posts, or follow the player where there are none. A plan job keeps its own lists, so a Defend
        ///      Me plan made between two of its steps cannot disturb it. Every decision is v9-v11's: the
        ///      planner harness (harnesses/mcs_fork_planner) runs build 11's planner and this one on the same
        ///      fake worlds and random numbers and requires identical posts (158 scenarios, four ways each,
        ///      also interleaved with other plans), and its mutation runner proves the harness fails when a
        ///      decision or the isolation is broken. Defend Me plans and shifts are still made in one frame.
        ///    - The fork times its own heavy work per frame (Core/FrameCost.cs, C2Sain/ForkCost.cs): the
        ///      planner's slices, Defend Me plans and shifts, the flank spot search, the closest-reachable
        ///      fallback, follow-spot picks, the two Harmony patches that run inside SAIN every frame (bush
        ///      sight, decision filter) and the markers. Each has its own F12 switch ("Timing: ..."), off by
        ///      default, and records only while the diagnostic log is on. The log gets each frame where the
        ///      timed work reaches the threshold ("Timing: log frames over", 2 ms) and a summary when the
        ///      raid ends. A call's time excludes the timed calls inside it. The frame number is Time.frameCount,
        ///      the recorder's "frame" (give or take one).
        ///    - Carry bots wear two markers (C2Sain/StrobeMarkers.cs, Core/StrobeRules.cs), each switched in
        ///      F12 and both on by default, with no menu order (the user's choice): a thermal ID beacon over
        ///      the head whose heat pulses hot and cold every 2 s (ECOTI reads no side or team; the beacon
        ///      carries the heat value ECOTI's shader reads and is drawn only by ECOTI's thermal cameras),
        ///      and an IR strobe light over the head that blinks in a double flash every 1.5 s and shines
        ///      only with night vision on (EFT's IkLight, scaled by BorkelRNVG). ECOTI and its files are
        ///      untouched.
        ///    Diagnostics: the plan lines gain timing ("; 22 ms in 8 frame(s), worst frame 3.4 ms (scan 2.1,
        ///    routes 17.4, posts 2.5), ready 0.15 s after it began" on an indoor plan's line, a "plan timing:"
        ///    line on any other), "fork cost:" and "fork cost summary:" lines for the Timing switches that are
        ///    on, and a "markers: made for ..." line per bot. The summary tool prints these from v12.
        ///    Pooling: what bots decide pools with v9-v11, since the plans are the same. Two things differ:
        ///    a plan reaches the bots up to about 0.3 s after it was asked for, and an indoor plan's time is
        ///    now "N ms in F frame(s), worst frame W ms": N compares with the "; N ms" of v9-v11 (work), the
        ///    worst frame is what a frame felt. Frame-time figures from before and after v12 are not
        ///    comparable for the bots' own cost: the planner moved and the markers add objects.
        ///    Falsifiers (written before any v12 raid):
        ///    - a plan whose worst frame passes 6 ms in the log, or a timed planner frame of 6 ms or more
        ///      with the planner switch on, sends the step sizes down;
        ///    - bots standing on their old posts for more than half a second after the player stopped, a
        ///      median "ready after" over 0.4 s, or indoor guard places without a post at 14% or more
        ///      (v9-v11: 9-27%, 12% on Reserve) sends the budget up or the slicing back;
        ///    - a "markers: switched off ... after an error" line, or any plan job exception in the log,
        ///      is a bug to fix before anything else;
        ///    - the first raid with the thermal switch on showing no dot over a carry bot through ECOTI means
        ///      the heat-by-property route is wrong (check the "markers: made" line's layer and skin shader
        ///      first); a dot seen with the naked eye or through plain night vision means the per-camera
        ///      gating is wrong; a blink the user cannot tell from an enemy's heat means the pattern or size
        ///      changes;
        ///    - "Timing: carry bot markers" showing 1 ms or more a frame sends the per-camera step back;
        ///    - a long frame (40 ms or more) inside a carry bot's UpdateManual in the recorder, with every
        ///      Timing switch on and no "fork cost:" line at its frame number (give or take one), means the
        ///      fork's cost is in something not timed: add that kind.
        ///    Result (2026-11-18, the user's first raid with v12: Interchange, one carry bot, the user closed the
        ///    game by force about 48 s into it, so it has no raid-end lines; evidence/2026-11-18-build-12-first-raid):
        ///    - Planner: 6 guard plans, all outdoors (no indoor plan ran, so the indoor slicing and the indoor
        ///      no-post rate are untested). Work 0-17 ms (median 2; the 17 ms one was the raid's first plan), 1-8
        ///      frames, worst frame 0.4-4.6 ms (median 2.0; falsifier 6 ms), ready 0.00-0.13 s after it was asked
        ///      for (median 0.01 s; falsifier 0.4 s). No Timing switch was on, so there are no "fork cost:" lines.
        ///    - No "markers: switched off" line and no exception naming MCS or the fork in BepInEx's log, the
        ///      Unity log or the game's errors.log (689 entries, none about MCS or the fork).
        ///    - Markers, from the user's report and two screenshots (a GPNVG-18 with ECOTI, Outline mode): the
        ///      thermal beacon was drawn by ECOTI's thermal view and pulsed hot and cold, and did not show with the
        ///      ECOTI clip-ons off; the IR strobe showed through night vision. So the heat-by-property route works
        ///      (v12 falsifier not fired) and the beacon was not seen through plain night vision. A check with
        ///      night vision off was not reported. Four things were wrong, all changed in v13: the IR light lit
        ///      the scene like a ceiling lamp (the setting was 6 m, and BorkelRNVG, whose IR flashlight multipliers
        ///      are 2 and 1.5 here, made it 12 m and 2.25); its 0.12 s flashes were too long beside a fire alarm's
        ///      quick flash; it was still seen with ECOTI's thermal on the goggles; and the beacon hung above the
        ///      character instead of sitting on it (0.4 m above the head bone; a helmet's top is about 0.2 m above
        ///      it, from the screenshots).
        ///    Second result (2026-11-18, the user's raid with v13, whose planner is v12's: Interchange, 61 minutes, one
        ///    carry bot; evidence/2026-11-18-build-13-first-raid): the first indoor plans in the game. 219 plans with
        ///    timing, 201 indoors and 18 outdoors. Indoors: work median 33 ms (max 53), 13 frames (max 24), worst
        ///    frame median 3.2 ms (max 7.4), ready 0.26 s after it was asked for (max 0.46). All 219: worst frame p99
        ///    4.2 ms; ready median 0.25 s, p99 0.43 s.
        ///    - The first falsifier FIRED once: one plan's worst frame passed 6 ms (7.4 ms at 06:18, an indoor plan
        ///      that took 52 ms in 18 frames), 1 of 219. Not acted on in v14: v14 changes no decision or timing a
        ///      bot has, so the keep-up report from the same raid (sent to the bot behavior session) is not
        ///      confounded. The step sizes come down in a later build if that session finds the planner involved or the
        ///      6 ms frames recur.
        ///    - Not fired: the median "ready after" is 0.25 s (falsifier 0.4 s), though 10 of 219 plans (4.6%) took
        ///      longer than 0.4 s, the longest 0.46 s; indoor guard places without a post were 8 of 201 (4%; falsifier
        ///      14%). Bots standing on their old posts for more than half a second after the player stopped: not
        ///      measured. No plan job exception and no "markers: switched off" line.
        /// 13 (2026-11-18) Test build 13: the carry bot markers, after the user's first test of build 12.
        ///    Measured before it: the Result of v12 above, with ECOTI 3.5.1's code (Coti.Client.dll IL) and
        ///    BorkelRNVG's (IkLightAwakePatch), both read 2026-11-18.
        ///    Changed:
        ///    - The IR strobe is a plain red point light of the fork's own, not EFT's IkLight. BorkelRNVG's
        ///      IkLightAwakePatch multiplies an IkLight's range and brightness by its IR flashlight multipliers
        ///      (2 and 1.5 in the user's com.borkel.nvgmasks.cfg) and again whenever those settings change, so v12's
        ///      light was not the 6 m and 1.5 set. The new light is as set: reach 1 m (setting IrRadius), brightness
        ///      3 (IrBrightness). By Unity's built-in point-light fall-off, roughly 1/(1+25(d/reach)^2), that is
        ///      strong within about a quarter of a metre and gone at 1 m: the helmet and shoulders, not the
        ///      scene (an estimate, not measured). The fork switches the light on only while night vision is on, the
        ///      pattern is in a flash and the rule below allows it. Brightness and reach follow their settings
        ///      within 0.25 s (v12 remade the lights).
        ///    - The double flash is two 0.05 s flashes 0.10 s apart every 1.5 s (v12: 0.12 s flashes 0.16 s
        ///      apart). 0.05 s is three frames at 60 fps and still one at 25. A frame longer than 50 ms can skip a
        ///      flash.
        ///    - The IR strobe stays off while ECOTI's thermal view is up on the goggles (setting IrHideUnderEcoti,
        ///      default on; a thermal view cannot see an IR light). Core/EcotiProbe.cs reads the static bool
        ///      Coti.Client.CotiState.Active by reflection ten times a second; it is true while the goggles' thermal
        ///      is shown. Not used: "a camera named Coti... rendered", because ECOTI's 1x camera also renders for a
        ///      powered thermal riser on the held weapon, aimed or not (CotiCameraDuty.OneX), which would hide the
        ///      strobe all the time for a player who keeps a riser on. A riser's scope view is not covered: its lens
        ///      is heat only. If ECOTI is absent or its state cannot be read the strobe is simply not hidden.
        ///    - The beacon and the IR light sit on the helmet: 0.24 m above the head bone (setting MarkerHeight; v12
        ///      0.4), the beacon 0.15 m wide (BeaconSize; v12 0.22). The helmet top is estimated at 0.2 m above the
        ///      head bone from the screenshots, so a marker may sink a little into the helmet or hang a little over it.
        ///    - Four settings with new defaults were renamed, because a BepInEx entry keeps the value in the file
        ///      and a changed default would not have reached an install that holds v12's: StrobeHeight to
        ///      MarkerHeight, StrobeSize to BeaconSize, StrobeIrIntensity to IrBrightness, StrobeIrRange to
        ///      IrRadius. The old entries stay in the file, unread.
        ///    Diagnostics: "markers: made for ..." says "reach" where v12 said "range"; with the diagnostic log on,
        ///    "markers: ECOTI thermal state: ..." (once a raid: where the state is read from, or why it cannot be)
        ///    and "markers: ECOTI thermal view on/off; the IR strobe is hidden/shown again" at each change. The
        ///    summary tool prints these from v13.
        ///    Pooling: UNRESTRICTED for everything v12 measures. No decision a bot makes changed; the markers and
        ///    their own log lines did (the wording of "made for" aside, no record field was repurposed).
        ///    Falsifiers (written before any v13 raid):
        ///    - the IR flash still lighting the ground or walls a few metres round the bot (the user's eye), or
        ///      not visible at 30 m through night vision, sends IrRadius and IrBrightness back (down, or up);
        ///    - the IR strobe flashing through ECOTI's thermal on the goggles while the setting is on and the log
        ///      has "ECOTI thermal view on" means the hiding is wrong; no "ECOTI thermal state: read from ..."
        ///      line (the log says "not loaded" or "no class ..." instead) means ECOTI's state could not be read,
        ///      and the rule needs a different source;
        ///    - the IR strobe missing with night vision on and no ECOTI thermal on the goggles means the hiding
        ///      fires when it should not (look for "ECOTI thermal view on" with the clip-on switched off);
        ///    - the beacon still hanging clear above the helmet, or sunk so far into it that its outline is
        ///      lost (the user's eye), changes MarkerHeight; a beacon too small to see through ECOTI at 100 m
        ///      changes BeaconSize;
        ///    - a "markers: switched off ... after an error" line is a bug to fix before anything else.
        ///    Result (2026-11-18, the user's first raid with v13: Interchange, 61 minutes, one carry bot, a GPNVG-18
        ///    with ECOTI in Outline mode; evidence/2026-11-18-build-13-first-raid):
        ///    - The probe worked in the game: "markers: ECOTI thermal state: read from Coti.Client.CotiState.Active",
        ///      and the goggles' thermal went on 6 times and off 5 times in the log, the IR strobe hidden and shown
        ///      again with it. No v13 falsifier on the probe or the hiding fired. The user: "The two markers are
        ///      correctly differentiated now."
        ///    - "Timing: carry bot markers" was on: 737,270 calls in 265,553 frames, 2.79 s in all (0.08% of the
        ///      3,693 s raid), mean 0.00 ms a call, worst call and worst frame 0.4 ms (falsifier 1 ms a frame, not fired).
        ///      No "markers: switched off" line and no exception naming MCS or the fork (676 errors.log entries, none
        ///      about MCS or the fork).
        ///    - The IR strobe is "nearly there": the 1 m reach is fine, but it is "too dim to notice unless you're looking
        ///      very hard for it". The first v13 falsifier fired in its "up" direction (brightness 3 was too low). The
        ///      user's config also held MarkerHeight 0.18 by the end of the session (default 0.24), which put the IR
        ///      light, which rode at the same height, inside the helmet (a helmet's top is about 0.2 m above the head
        ///      bone), where a point light lights hardly anything; the cause of the dimness is therefore two things at
        ///      once and cannot be separated from this raid.
        ///    - The thermal beacon "is a bit hard to recognize due to its position above the head and its misalignment
        ///      with the skeleton", after the user had lowered MarkerHeight to 0.18. The v13 falsifier "the beacon still
        ///      hanging clear above the helmet" fired in spirit: the fix is not another height but a marker that is
        ///      visibly part of the body. Their proposal, a tall pole from the body like the flag pole on some shopping
        ///      carts with a barber's pole pattern and a choice of designs in F12, is v14.
        ///    - Also reported, for the bot behavior session (not changed here): the bot struggles to decide to keep up
        ///      with the player on the move (log: 53% of the raid in Mcs:TooFar, 33 Default-TooFar flips at 8-9 m, a
        ///      7 s lag before following from TooClose, standing still in TooFar at 30-146 m).
        /// 14 (2026-11-18) Test build 14: the carry bot markers again, after the user's first test of build 13.
        ///    Measured before it: the Result of v13 above and the second Result of v12 (the first indoor plans in
        ///    the game). Markers only: no decision or timing a bot has changed.
        ///    Changed:
        ///    - The thermal marker is a tall thin pole standing on the helmet (setting PoleLength, default 1 m; the foot is
        ///      MarkerHeight above the head bone, default 0.18, the user's own tuned value), so it is visibly part of the
        ///      body instead of a box floating above it. ECOTI's heat shader draws a renderer hot or not at all and its
        ///      overlay shows every hot pixel at full strength (research B2), so a design is only where the pole is hot at
        ///      each moment. The pole is at most six small hexagonal boxes (C2Sain/ThermalPole.cs), one per hot
        ///      section, switched on only for ECOTI's thermal cameras as before; a section that is not hot is not drawn.
        ///    - Seven designs, chosen in F12 (setting ThermalDesign), computed in Core/PoleDesigns.cs: BarberPole (the
        ///      default: three hot bands, each a sixth of the pole, slide up it, a stripe a cycle), RisingPulse (one band
        ///      of a quarter of the pole climbs in 70% of the cycle, then a dark pause), SeeSaw (lower and upper halves
        ///      take turns), PolePulse (the whole pole hot for half the cycle), Flag (a steady pole with a flag of 0.15
        ///      of its length, three times as wide, blinking at the top), SteadyPole, and Box (build 13's small box,
        ///      for comparison). One cycle is StrobeThermalPeriod seconds (default 1; v13's pulse was 2 s).
        ///    - The pole widens with distance (setting PoleWidenAtRange, default on) so it is never thinner than about
        ///      0.0016 rad from the player, roughly a pixel and a half of ECOTI's picture: 0.16 m at 100 m, at most
        ///      0.5 m. The width at close range is PoleWidth (0.08 m). The 0.0016 and the picture's size (about 1,000
        ///      rows over 50-75 degrees, so a row of 0.0008-0.0011 rad) are estimates from the research, not measured.
        ///    - The IR strobe is white (setting IrColor; White default, Red is v13's), with brightness 12 (v13: 3), and
        ///      its light is ForcePixel, so Unity cannot demote it to a vertex light near other lights. It has its own
        ///      height, IrHeight (default 0.32 m above the head bone, a little over a helmet's top), instead of riding at
        ///      the pole's height. Reach (1 m, the user: "the radius is fine") and the 0.05 s double flash are unchanged.
        ///      The sizes are estimates: at the same distance and reach, brightness 12 lights a surface 4 times as
        ///      strongly as 3 does, and white carries about three times the luminance of red if night vision weighs
        ///      colour channels as a luminance does (not verified for BorkelRNVG), so about 13 times v13's red 3 at
        ///      most; the light also sits 0.14 m higher, clear of the helmet.
        ///    - Defaults that changed reach existing installs by a one-time migration instead of another rename:
        ///      ForkConfig.MigrateDefaults moves IrBrightness from 3 to 12 and StrobeThermalPeriod from 2 to 1 when the
        ///      file still holds the old default, once (hidden setting SettingsVersion, 14). A value anyone changed is
        ///      left alone; a user who had set exactly 3 by choice sees it change once.
        ///    - Removed: the smooth hot-and-cold pulse model (StrobeRules.ThermalCelsius and ThermalHotShare, the cold
        ///      temperature setting). The shader and overlay make heat all or nothing, so v13's pulse was always an
        ///      on-off blink whose duty cycle changed with the air temperature (about 51% at 15 C, 41% at 30 C); the
        ///      designs now say plainly when a section is hot.
        ///    Diagnostics: the "markers: made for ..." line now reads "thermal BarberPole 1 m tall on layer 8, skin
        ///    shader ..., IR strobe (brightness 12, reach 1 m, White, 0.32 m up)" (v13: "thermal beacon on layer 8 ...").
        ///    No record field was repurposed; the summary tool prints the text after the colon unchanged.
        ///    Pooling: UNRESTRICTED for everything v12 and v13 measure. No decision a bot makes changed, and the
        ///    markers' log lines keep their shape.
        ///    Falsifiers (written before any v14 raid):
        ///    - the IR flash still too dim to notice at ordinary follow distances (10-30 m) through night vision
        ///      with the new brightness and white light (the user's eye) means the light is not the way: next is a glow
        ///      sprite on the helmet (a flare that does not depend on lit surfaces), not a bigger reach;
        ///    - the IR flash lighting the ground or walls round the bot like a lamp again sends IrBrightness down;
        ///    - any sign of the white IR light with night vision off (the naked-eye check, never reported for v12 or
        ///      v13) means the gate is wrong: back to red and tighten it;
        ///    - no pole through ECOTI at all while the Box design shows (check the "made for" line first), or a pole
        ///      that flickers or vanishes beyond about 60 m with widening on (MinPoleAngle too small), or one that does
        ///      not look attached to the head (MarkerHeight), each sends that value back;
        ///    - a design that reads like an enemy's heat, or one the user cannot follow, is dropped from the list;
        ///    - "Timing: carry bot markers" showing 1 ms or more a frame sends the per-camera step back (v13: 0.4 ms
        ///      worst); a "markers: switched off ... after an error" line is a bug to fix before anything else.
        ///    Measured: nothing yet (no raid has run v14).
        /// </summary>
        public const int ForkVersion = 14;

        /// <summary>Short label used in logs and the overlay.</summary>
        public const string Label = "C2/SAIN fork v14";
    }
}
