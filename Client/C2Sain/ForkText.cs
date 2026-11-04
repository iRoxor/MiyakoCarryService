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
            ["McsFork/AllowedPersonalities Key"] = "SAIN: allowed carry bot personalities",
            ["McsFork/AllowedPersonalities Description"] = "SAIN rolls a personality for every bot, carry bots included. When it rolls one not ticked here, the bot is re-rolled until it gets a ticked one. Default: Normal, Chad, GigaChad and Wreckless (SnappingTurtle, Rat, Timmy and Coward are banned). Normal is balanced; Chad, GigaChad and Wreckless push progressively harder. Nothing ticked keeps SAIN's roll. Engage orders lend their own personality for their duration.",
            ["McsFork/EngageMax Key"] = "Engage: longest order (s)",
            ["McsFork/EngageMax Description"] = "An Engage Enemy order ends after this long even if the enemy is still known. Default 90. It also ends about 40 s after the enemy was last seen, heard or reported (8 s after it dies), or when you give another order.",
            ["McsFork/EngageFlanking Key"] = "Engage: flanking",
            ["McsFork/EngageFlanking Description"] = "Engage Enemy sends bots around the side of the enemy's last known position before they search it; with two or more bots, one stays back to give covering fire. Default on. Off makes them push straight in, Chad-style.",
            ["McsFork/RecklessStyle Key"] = "Engage: reckless personality",
            ["McsFork/RecklessStyle Description"] = "The SAIN personality Engage Enemy Wrecklessly lends: GigaChad (aggressive, still uses cover) or Wreckless (charges, sprints corners). Default Random picks one per bot each time.",
            ["McsFork/ReportEngages Key"] = "I see the enemy: also engage",
            ["McsFork/ReportEngages Description"] = "Off (default): a contact report puts the squad on guard facing the mark, and bots shoot only if they see the enemy. On: a report that found an enemy also gives every bot Engage Enemy.",
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
            ["McsFork/DebugLog Key"] = "Debug: log mode changes",
            ["McsFork/DebugLog Description"] = "Write squad mode changes and leash events to the BepInEx log. Default off.",

            // Commands.
            ["McsFork/BreakOff Name"] = "Break off from combat",
            ["McsFork/BreakOff TargetName"] = "Command carry service player to stop fighting and run back to you. Once it reaches you, it fights normally again.",
            ["McsFork/TeamBreakOff Name"] = "Team Break off from combat",
            ["McsFork/TeamBreakOff TargetName"] = "All carry service players stop fighting and run back to you. Each one fights normally again once it reaches you.",

            ["McsFork/SpotEnemy Name"] = "I see the enemy",
            ["McsFork/SpotEnemy TargetName"] = "Mark the enemy under your crosshair for the whole squad. They take cover facing it and shoot if they see it; they only advance on Engage Enemy.",
            ["McsFork/Engage Name"] = "Engage Enemy",
            ["McsFork/Engage TargetName"] = "Only while an enemy is known. Command carry service player to attack the enemy's last known position: it flanks, or covers the others, then searches.",
            ["McsFork/TeamEngage Name"] = "Team Engage Enemy",
            ["McsFork/TeamEngage TargetName"] = "Only while an enemy is known. One carry service player gives covering fire while the others flank the enemy's last known position, then they search it.",
            ["McsFork/EngageReckless Name"] = "Engage Enemy Wrecklessly",
            ["McsFork/EngageReckless TargetName"] = "Only while an enemy is known. Command carry service player to rush the enemy's last known position like SAIN's GigaChad or Wreckless.",
            ["McsFork/TeamEngageReckless Name"] = "Team Engage Enemy Wrecklessly",
            ["McsFork/TeamEngageReckless TargetName"] = "Only while an enemy is known. All carry service players rush the enemy's last known position like SAIN's GigaChad or Wreckless.",
            ["McsFork/ClearTargetArea Name"] = "Clear Target Area",
            ["McsFork/ClearTargetArea TargetName"] = "Command carry service player to search the area under your crosshair. Refused while in contact.",
            ["McsFork/TeamClearTargetArea Name"] = "Team Clear Target Area",
            ["McsFork/TeamClearTargetArea TargetName"] = "All carry service players split up and search the area under your crosshair. Refused while in contact.",

            // Replies. Talk keys are the subtitle for a voice trigger; the others are appended keys.
            ["McsFork/CantGetThere"] = "Can't get there.",
            ["McsFork/FallingBack"] = "Falling back to you!",
            ["McsFork/Talk Gogogo"] = "Moving!",
            ["McsFork/Talk Covering"] = "Covering!",
            ["McsFork/Talk LostVisual"] = "Lost them.",
            ["McsFork/NoContact"] = " No contact.",
            ["McsFork/InContact"] = " We're in contact.",
            ["McsFork/Engaging"] = "Moving to engage!",
            ["McsFork/EngagingReckless"] = "Going in hard!",
            ["McsFork/FlankLeft"] = "Flanking left!",
            ["McsFork/FlankRight"] = "Flanking right!",
            ["McsFork/CoveringFire"] = "Covering fire!",
            ["McsFork/LostContact"] = "Lost contact, falling back.",
            ["McsFork/EngageTimeout"] = "Breaking contact, coming back.",
            ["McsFork/ContactMarked"] = "Copy, eyes on.",
            ["McsFork/WatchingSpot"] = "Copy, watching that spot.",
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
