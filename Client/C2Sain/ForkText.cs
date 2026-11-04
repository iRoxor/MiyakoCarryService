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

            // Replies.
            ["McsFork/CantGetThere"] = "Can't get there.",
            ["McsFork/FallingBack"] = "Falling back to you!",
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
