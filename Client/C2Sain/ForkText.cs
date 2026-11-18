// ForkText.cs -- English text for everything the C2/SAIN fork adds or renames.
//
// Upstream MCS keeps its strings in the server mod's locale files
// (Server/Assets/database/locales/global/*.json). The fork leaves the server
// untouched, so its own strings live here. StringExtensions.McsLocalized checks
// this table first, which covers setting names and descriptions, command
// descriptions and subtitle keys. Menu entry names are passed already resolved
// (ForkText.Get) because the game's action panel localizes those itself.
// Reads and writes no files.

using System.Collections.Generic;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Fork string table (English only; every language sees English).</summary>
    internal static class ForkText
    {
        private static readonly Dictionary<string, string> _text = new()
        {
            // Settings section and entries (drawn by MCS's settings drawer through McsLocalized).
            ["McsFork/Section"] = "D. C2 / SAIN brains (fork)",
            ["McsFork/IdleRadius Key"] = "Guard: idle radius (m)",
            ["McsFork/IdleRadius Description"] = "How far you may drift and still count as standing still. Default 3. When you stay inside it for the idle time, the bots switch from following to guarding.",
            ["McsFork/IdleSeconds Key"] = "Guard: idle time (s)",
            ["McsFork/IdleSeconds Description"] = "Seconds you must stay inside the idle radius before the bots start guarding. Default 3. Lower reacts sooner but switches modes more often.",
            ["McsFork/GuardRadius Key"] = "Guard: zone radius (m)",
            ["McsFork/GuardRadius Description"] = "Once guarding starts, the zone grows to this radius around where you stopped. Leaving it sends the bots back to following. Default 15.",
            ["McsFork/GuardPostMin Key"] = "Guard: nearest post (m)",
            ["McsFork/GuardPostMin Description"] = "Outdoors, guard posts are at least this far from you. Default 3.",
            ["McsFork/GuardPostMax Key"] = "Guard: farthest post (m)",
            ["McsFork/GuardPostMax Description"] = "Outdoors, guard posts are at most this far from you; a bot takes the best cover (tree, rock, wall, bush) it can find out to this distance. Default 12. Higher spreads the bots wider and finds more cover, but they cover each other less. Indoors posts stay within 8 m. Keep it below the zone radius.",
            ["McsFork/CombatEndDelay Key"] = "Combat: end delay (s)",
            ["McsFork/CombatEndDelay Description"] = "Seconds without any engagement before the squad leaves combat. Default 10. Higher keeps SAIN in charge longer after the last shot.",
            ["McsFork/ChaseMin Key"] = "Leash: shortest chase (s)",
            ["McsFork/ChaseMin Description"] = "Each chase after an enemy lasts a random time between the shortest and longest chase. Default 3.",
            ["McsFork/ChaseMax Key"] = "Leash: longest chase (s)",
            ["McsFork/ChaseMax Description"] = "Upper end of the random chase time. Default 5. Higher lets bots pursue further from you.",
            ["McsFork/ReturnRadius Key"] = "Leash: back-with-you radius (m)",
            ["McsFork/ReturnRadius Description"] = "After a chase is cut off, the bot must come back this close to you before it may chase again. Default 6.",
            ["McsFork/FollowDistance Key"] = "Follow: distance (m)",
            ["McsFork/FollowDistance Description"] = "While following, a bot walks back toward you once it is this far away. Default 8. Lower keeps them tighter but moving more.",
            ["McsFork/BreakOffArrive Key"] = "Break off: arrival radius (m)",
            ["McsFork/BreakOffArrive Description"] = "Break off from combat ends once the bot is this close to you, and SAIN takes over again. Default 3.",
            ["McsFork/BreakOffTimeout Key"] = "Break off: give up after (s)",
            ["McsFork/BreakOffTimeout Description"] = "Break off from combat ends after this long even if the bot has not reached you. Default 20.",
            ["McsFork/BreakOffGrace Key"] = "Break off: no-chase time (s)",
            ["McsFork/BreakOffGrace Description"] = "After Break off brings a bot back, it may not chase for this long, so it does not run straight back out. Default 10.",
            ["McsFork/Stance Key"] = "Stance: squad stance",
            ["McsFork/Stance Description"] = "How the carry bots fight. Default Defensive. Choosing a stance from the Team ▸ Combat ▸ Stance menu changes this setting, so the squad keeps it next raid; a stance given to one bot from its own menu lasts only for that raid.\n- Passive: hold fire and let enemies pass until a bot or you is shot at (or you fire, see below); then fight as Defensive until the fight ends.\n- Defensive: stay close to you, prefer cover, fight at range, chase only briefly (the Leash settings). No grenades.\n- Aggressive: push and flank with covering fire, chase up to the Aggressive leash, use grenades.\n- Hyper-aggressive: charge the enemy with no leash, use grenades. Expect them to leave you behind.",
            ["McsFork/PassiveEngagesWhenYouShoot Key"] = "Stance: Passive fights when you shoot",
            ["McsFork/PassiveEngagesWhenYouShoot Description"] = "In the Passive stance, whether you firing at an enemy releases the squad to fight, as well as a bot or you being shot at. Default on. Off keeps them quiet even after you open fire, until someone is shot at.",
            ["McsFork/AggressiveLeash Key"] = "Stance: Aggressive leash (m)",
            ["McsFork/AggressiveLeash Description"] = "In the Aggressive stance, bots chase freely within this distance of you; past it they break off and come back to within half of it. Default 100. Higher lets them pursue further and leaves you more alone; lower keeps them closer but lets enemies slip away. Passive and Defensive use the Leash settings above; Hyper-aggressive has no limit.",
            ["McsFork/EngageFlanking Key"] = "Stance: Aggressive flanking",
            ["McsFork/EngageFlanking Description"] = "In the Aggressive stance, bots go around the side of the enemy's last known position before they search it; with two or more Aggressive bots, one stays back to give covering fire. Default on. Off makes them push straight in.",
            ["McsFork/RecklessStyle Key"] = "Stance: Hyper-aggressive personality",
            ["McsFork/RecklessStyle Description"] = "The SAIN personality the Hyper-aggressive stance lends: GigaChad (aggressive, still uses cover) or Wreckless (charges, sprints corners). Default Random picks one per bot each time it enters the stance.",
            ["McsFork/FoliageSight Key"] = "Bushes: see-out radius (m)",
            ["McsFork/FoliageSight Description"] = "Carry bots see and shoot through bushes and grass this close to their own eyes, so a bot hiding in a bush is not blind. Enemies still cannot see through it. Default 2; 0 turns it off. Larger values start to let them shoot through bushes in front of them.",
            ["McsFork/EscortSprint Key"] = "Escort: sprint burst (s)",
            ["McsFork/EscortSprint Description"] = "An escorting bot sprints in bursts of this length, then walks and looks around. Default 3. Higher gets there sooner but is harder to keep up with.",
            ["McsFork/EscortWalkMin Key"] = "Escort: shortest walk (s)",
            ["McsFork/EscortWalkMin Description"] = "Shortest walk between sprint bursts. Default 3.",
            ["McsFork/EscortWalkMax Key"] = "Escort: longest walk (s)",
            ["McsFork/EscortWalkMax Description"] = "Longest walk between sprint bursts. Default 5.",
            ["McsFork/EscortWait Key"] = "Escort: wait for you beyond (m)",
            ["McsFork/EscortWait Description"] = "When you fall this far behind an escorting bot, it stops and watches until you are within 8 m. Default 20.",
            ["McsFork/OutOfCombatStims Key"] = "Stims outside combat",
            ["McsFork/OutOfCombatStims Description"] = "Upstream MCS made carry bots inject any stim they carry, every 30 s, even out of combat (an SJ6 makes an escort hard to keep up with). Default off: SAIN still uses stims in a fight.",
            ["McsFork/GapCrossing Key"] = "Movement: vault and drop shortcuts",
            ["McsFork/GapCrossing Description"] = "Upstream MCS's shortcuts: vaulting through windows and over walls, dropping off ledges, and warping onto the navmesh. Default off. This is what made bots clip into walls and unreachable spots.",
            ["McsFork/McsFastDoors Key"] = "Movement: MCS fast doors",
            ["McsFork/McsFastDoors Description"] = "Upstream MCS's own door handling, which opens doors instantly and ignores the game's rule on which doors may be opened. Default off: the game's and SAIN's door handling are used instead.",
            ["McsFork/HearFootsteps Key"] = "Hearing: AI footsteps",
            ["McsFork/HearFootsteps Description"] = "Let carry bots hear AI enemies walking and sprinting. SAIN makes footstep sounds only for human players, so without this no carry bot hears an AI enemy move. SAIN still decides each time whether a step is heard: walls, distance, headphones. Only carry bots hear the steps; other bots are unchanged. Default on.",
            ["McsFork/GunshotRange Key"] = "Hearing: gunshot range (m)",
            ["McsFork/GunshotRange Description"] = "Carry bots ignore gunshots farther away than this, so a distant fight does not pull them off you. Bullets passing or hitting near them still count as being shot at. Default 25. 0 = hear gunshots as far as SAIN allows.",
            ["McsFork/FollowFootstepRange Key"] = "Hearing: footsteps while following (m)",
            ["McsFork/FollowFootstepRange Description"] = "While the bots follow you, they react only to footsteps within this distance. A heard enemy makes the squad stop, take cover facing the sound, and say \"I hear something.\" Default 30. Higher alerts on farther footsteps but stops the squad more often; 0 = no limit.",
            ["McsFork/SayIHearSomething Key"] = "Hearing: say \"I hear something.\"",
            ["McsFork/SayIHearSomething Description"] = "A carry bot that hears an enemy it has not seen says so, at most once every 20 s per squad, and not during a fight. Default on.",
            ["McsFork/IHearSomething"] = "I hear something.",
            ["McsFork/PlannerBudget Key"] = "Guard: planning time per frame (ms)",
            ["McsFork/PlannerBudget Description"] = "Working out where each bot should stand can take 20-30 ms of computing in rooms and buildings, and the game cannot draw a frame meanwhile. The bots now do it a few milliseconds per frame instead, and keep their current spots until the new ones are ready, a fraction of a second later. Default 3. Higher finishes sooner but holds each frame longer; lower is gentler on the frame rate but the new spots arrive later. 30 does it all in one frame, as before.",
            ["McsFork/StrobeThermal Key"] = "Markers: thermal ID beacon",
            ["McsFork/StrobeThermal Description"] = "Puts a small marker over each carry bot's head that ECOTI's thermal view draws hot and cold in a slow pulse, as if a heated element were turning inside it, so your bots stand out from enemies through the clip-on. Enemies have none. Only ECOTI's thermal camera draws it: the plain view and night vision never show it. Default on. Off removes it.",
            ["McsFork/StrobeIr Key"] = "Markers: night vision IR strobe",
            ["McsFork/StrobeIr Description"] = "Puts a small red IR light over each carry bot's head that blinks in the pattern below and shines only while night vision is on, so your bots stand out from enemies through night vision, and under ECOTI's thermal image in its Outline mode. Default on. BorkelRNVG, if you use it, scales its brightness and range like the game's other IR lights.",
            ["McsFork/StrobeIrPattern Key"] = "Markers: IR strobe pattern",
            ["McsFork/StrobeIrPattern Description"] = "How the IR strobe blinks. Double flash (default): two short flashes, then a pause, every 1.5 s. Slow blink: 1 s on, 1 s off. Fast blink: four times a second. Steady: always lit.",
            ["McsFork/StrobeThermalPeriod Key"] = "Markers: thermal pulse period (s)",
            ["McsFork/StrobeThermalPeriod Description"] = "Seconds between the thermal beacon's hot peaks. Default 2. Shorter pulses faster and is easier to notice but looks like a flicker; longer is calmer. Keep it unlike the IR strobe's pattern so the two are not confused.",
            ["McsFork/StrobeThermalHeat Key"] = "Markers: thermal beacon heat (C)",
            ["McsFork/StrobeThermalHeat Description"] = "How hot the thermal beacon gets at the top of its pulse, in degrees Celsius. Default 65. ECOTI draws anything 20 degrees or more above the air at full brightness, so values above about 55 change nothing; lower dims the pulse.",
            ["McsFork/StrobeHeight Key"] = "Markers: height above the head (m)",
            ["McsFork/StrobeHeight Description"] = "How far above each bot's head bone the markers float. Default 0.4, which clears a helmet. Higher separates the marker from the head in the thermal image; lower looks more natural but can merge with the head. Applies at once.",
            ["McsFork/StrobeSize Key"] = "Markers: thermal beacon size (m)",
            ["McsFork/StrobeSize Description"] = "Width of the thermal beacon. Default 0.22. ECOTI's picture is about a thousand rows tall, so a marker this size is only a pixel or two at 200 m: bigger is easier to see far away, smaller is less obvious up close. Applies at once.",
            ["McsFork/StrobeIrIntensity Key"] = "Markers: IR strobe brightness",
            ["McsFork/StrobeIrIntensity Description"] = "Brightness of the IR light. Default 1.5. BorkelRNVG, if installed, multiplies it again. Higher is brighter on the helmet and nearby ground through night vision and easier to see from far, but lights more of the surroundings. Changing it remakes the lights.",
            ["McsFork/StrobeIrRange Key"] = "Markers: IR strobe range (m)",
            ["McsFork/StrobeIrRange Description"] = "How far the IR light reaches. Default 6. BorkelRNVG, if installed, multiplies it again. Higher lights more around the bot; lower keeps the glow on the bot itself. Changing it remakes the lights.",
            ["McsFork/CostPlanner Key"] = "Timing: guard planning",
            ["McsFork/CostPlanner Description"] = "For finding what costs frame time; not needed in normal play. Measures how long the guard planner's work takes in each frame (it works in small slices) and writes the frames where it was heavy to the log, with a summary when the raid ends. Default off. Does nothing unless \"Debug: diagnostic log\" is on. Each Timing setting is separate, so you can measure any combination.",
            ["McsFork/CostDefend Key"] = "Timing: Defend Me plans and shifts",
            ["McsFork/CostDefend Description"] = "Measures how long Defend Me takes to pick spots: a whole plan, or one bot's shift to a new spot, each done in a single frame. Default off. Does nothing unless \"Debug: diagnostic log\" is on.",
            ["McsFork/CostFlank Key"] = "Timing: flank spot search",
            ["McsFork/CostFlank Description"] = "Measures the search for a flank point that the Aggressive stances run before a bot goes around an enemy. Default off. Does nothing unless \"Debug: diagnostic log\" is on.",
            ["McsFork/CostReach Key"] = "Timing: closest-reachable fallback",
            ["McsFork/CostReach Description"] = "Measures the search for the closest spot a bot can walk to when it has no path to you. Default off. Does nothing unless \"Debug: diagnostic log\" is on.",
            ["McsFork/CostFollowSpot Key"] = "Timing: follow spot picks",
            ["McsFork/CostFollowSpot Description"] = "Measures the search for a spot near you for a bot to stand on (up to 8 path checks each time). Default off. Does nothing unless \"Debug: diagnostic log\" is on.",
            ["McsFork/CostFoliageSight Key"] = "Timing: bush sight patch",
            ["McsFork/CostFoliageSight Description"] = "Measures the fork's patch that lets bots see out of a bush. SAIN runs it about 30 times a second for every bot and enemy; each call is tiny, so this shows the steady cost, and it puts a timer around every call while it is on. Default off. Does nothing unless \"Debug: diagnostic log\" is on.",
            ["McsFork/CostDecisions Key"] = "Timing: decision filter",
            ["McsFork/CostDecisions Description"] = "Measures the fork's filter on SAIN's decisions for carry bots (stances, chase leash, holding fire, grenades). Time spent in the flank search inside it is counted under the flank search, not here. Default off. Does nothing unless \"Debug: diagnostic log\" is on.",
            ["McsFork/CostStrobe Key"] = "Timing: carry bot markers",
            ["McsFork/CostStrobe Description"] = "Measures the upkeep of the thermal beacon and IR strobe markers, including the small step they run before each camera draws (several times a frame). Default off. Does nothing unless \"Debug: diagnostic log\" is on.",
            ["McsFork/CostThreshold Key"] = "Timing: log frames over (ms)",
            ["McsFork/CostThreshold Description"] = "A frame is written to the log when the timed fork work in it adds up to this many milliseconds or more. Default 2. Lower logs more frames and makes a longer log; the summary at raid end counts every timed frame whatever this is.",
            ["McsFork/DebugLog Key"] = "Debug: diagnostic log",
            ["McsFork/DebugLog Description"] = "Write what the carry bots decide to BepInEx\\LogOutput.log, each line stamped with the raid clock (minutes:seconds since the raid started, the same clock Raid Review uses): every change of each bot's brain layer and SAIN's decision, squad mode changes, cut chases, contact reports, stance changes, Passive holding and releasing fire, held grenade throws, every guard and Defend plan with each bot's spot, its cover and how long the plan took, bots stopping short of their spot, any bot that has stood still in the open for 5 s or kept looking the same way for 8 s, each enemy a bot heard but could not see, deaths, and each item a bot goes to loot by itself. Also the frames where the fork's own work was heavy, for whichever \"Timing:\" settings are on, with a summary when the raid ends. Several hundred lines a raid. Turn it on when you want to report a problem. Default off.",

            // Commands.
            ["McsFork/BreakOff Name"] = "Break off from combat",
            ["McsFork/BreakOff TargetName"] = "Command carry service player to stop fighting and run back to you. Once it reaches you, it fights normally again.",
            ["McsFork/TeamBreakOff Name"] = "Team Break off from combat",
            ["McsFork/TeamBreakOff TargetName"] = "All carry service players stop fighting and run back to you. Each one fights normally again once it reaches you.",

            ["McsFork/SpotEnemy Name"] = "I see the enemy",
            ["McsFork/SpotEnemy TargetName"] = "Mark the enemy under your crosshair for the whole squad. They take cover facing it and shoot if they see it; how far they push depends on their stance.",
            ["McsFork/ClearTargetArea Name"] = "Clear Target Area",
            ["McsFork/ClearTargetArea TargetName"] = "Command carry service player to search the area under your crosshair. Refused while in contact.",
            ["McsFork/TeamClearTargetArea Name"] = "Team Clear Target Area",
            ["McsFork/TeamClearTargetArea TargetName"] = "All carry service players split up and search the area under your crosshair. Refused while in contact.",
            ["McsFork/TeamStance Name"] = "Team Stance",
            ["McsFork/TeamStance TargetName"] = "How the whole squad fights. The choice is kept for the next raid.",
            ["McsFork/Stance Name"] = "Stance",
            ["McsFork/Stance TargetName"] = "How this carry service player fights, for this raid only.",
            ["McsFork/StancePassive Name"] = "Passive",
            ["McsFork/StanceDefensive Name"] = "Defensive",
            ["McsFork/StanceAggressive Name"] = "Aggressive",
            ["McsFork/StanceHyperAggressive Name"] = "Hyper-aggressive",
            ["McsFork/TeamStancePassive TargetName"] = "Hold fire and let enemies pass until one of you is shot at.",
            ["McsFork/TeamStanceDefensive TargetName"] = "Stay close, prefer cover, fight at range. No grenades. The default.",
            ["McsFork/TeamStanceAggressive TargetName"] = "Push and flank with covering fire, chase up to the Aggressive leash, use grenades.",
            ["McsFork/TeamStanceHyperAggressive TargetName"] = "Charge the enemy with no leash and use grenades. They will leave you behind.",
            ["McsFork/StancePassive TargetName"] = "This carry service player holds fire and lets enemies pass until one of you is shot at.",
            ["McsFork/StanceDefensive TargetName"] = "This carry service player stays close, prefers cover and fights at range. No grenades.",
            ["McsFork/StanceAggressive TargetName"] = "This carry service player pushes and flanks, chases up to the Aggressive leash, and uses grenades.",
            ["McsFork/StanceHyperAggressive TargetName"] = "This carry service player charges the enemy with no leash and uses grenades.",
            ["McsFork/DefendMe Name"] = "Defend Me",
            ["McsFork/DefendMe TargetName"] = "All carry service players stay 3-5 m from you, each covering its own side from cover where there is any, and shift position now and then. In a fight they fight, then come back. Ends when you move 5 m away or order Regroup.",

            // Command menu groups (CommandMgr.BuildTeamMenu and BuildMemberMenu).
            ["McsFork/MenuCombat Name"] = "Combat",
            ["McsFork/TeamMenuCombat TargetName"] = "Squad combat orders: set how they fight, or break off and come back to you",
            ["McsFork/MemberMenuCombat TargetName"] = "Combat orders: set how it fights, or break off and come back to you",
            ["McsFork/MenuPositions Name"] = "Positions",
            ["McsFork/TeamMenuPositions TargetName"] = "Where the squad stays: defend you up close, hold, regroup, go to a spot, or teleport to you",
            ["McsFork/MemberMenuPositions TargetName"] = "Where this carry service player stays: hold, regroup, go to a spot, or teleport to you",
            ["McsFork/MenuTasks Name"] = "Tasks",
            ["McsFork/TeamMenuTasks TargetName"] = "Squad jobs: clear an area, escort you somewhere, or drop the loot they picked up for you",
            ["McsFork/MemberMenuTasks TargetName"] = "Jobs: clear an area, escort you somewhere, proxy actions, or drop the loot it picked up for you",
            ["McsFork/MenuReports Name"] = "Reports",
            ["McsFork/TeamMenuReports TargetName"] = "Ask the squad for enemy positions or their status",
            ["McsFork/MemberMenuReports TargetName"] = "Ask for enemy positions or its status",
            ["McsFork/MenuSettings Name"] = "Settings",
            ["McsFork/TeamMenuSettings TargetName"] = "Squad aiming and formation",
            ["McsFork/MenuGear Name"] = "Gear and aiming",
            ["McsFork/MemberMenuGear TargetName"] = "Open its inventory or change where it aims",

            // Replies. Talk keys are the subtitle for a voice trigger; the others are appended keys.
            ["McsFork/CantGetThere"] = "Can't get there.",
            ["McsFork/FallingBack"] = "Falling back to you!",
            ["McsFork/Talk Gogogo"] = "Moving!",
            ["McsFork/Talk Covering"] = "Covering!",
            ["McsFork/Talk LostVisual"] = "Lost them.",
            ["McsFork/InContact"] = " We're in contact.",
            ["McsFork/FlankLeft"] = "Flanking left!",
            ["McsFork/FlankRight"] = "Flanking right!",
            ["McsFork/CoveringFire"] = "Covering fire!",
            ["McsFork/StancePassiveAck"] = "Copy, holding fire unless engaged.",
            ["McsFork/StanceDefensiveAck"] = "Copy, staying defensive.",
            ["McsFork/StanceAggressiveAck"] = "Copy, going aggressive.",
            ["McsFork/StanceHyperAggressiveAck"] = "Copy, all-out attack!",
            ["McsFork/ContactMarked"] = "Copy, eyes on.",
            ["McsFork/WatchingSpot"] = "Copy, watching that spot.",
            ["McsFork/DefendingYou"] = "Copy, staying on you.",
        };

        /// <summary>Looks up a fork string.</summary>
        /// <param name="id">The key; null is allowed and never matches.</param>
        /// <param name="text">The English text when found.</param>
        /// <returns>True when the key belongs to the fork.</returns>
        public static bool TryGet(string id, out string text)
        {
            if (id == null)
            {
                text = null;
                return false;
            }
            return _text.TryGetValue(id, out text);
        }

        /// <summary>Returns the English text for a key, or the key itself when unknown.</summary>
        public static string Get(string id)
        {
            return TryGet(id, out var text) ? text : id;
        }
    }
}
