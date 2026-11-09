// ForkCommands.cs -- handlers for the commands the fork adds, and the hooks it puts on MCS's others.
//
// CommandMgr.Start registers DefendMe, SpotEnemy (ContactReports.Report) and, since fork v11, the
// eight stance commands with CommandUtils. The Team stance commands write the squad stance to the
// "Stance" setting (BepInEx saves it, so it carries over to the next raid) and clear every bot's own
// stance; a bot's own stance command sets BotForkState.StanceOverride for this raid only (carry bots
// expire and are rebuilt, so nothing is saved against one). A stance survives every other order.
// Before v11 the Engage Enemy commands lived here, and every other order ended them through a
// pre-command hook. CommandMgr.ClearAreaCommandAction calls RefuseInContact. SubtitlesMgr
// uses ReplaceWithKeys to show the fork's reply text for voice lines it shares
// with upstream. GameLoop calls EnsureVoiceTriggers when it sets a carry bot up.
// Writes the bots' MCS and fork state; reads and writes no files.

using EFT;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Enums;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Models;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Utils;
using System.Text;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Fork command handlers and hooks. Main thread only.</summary>
    internal static class ForkCommands
    {
        /// <summary>Voice lines the fork's replies use, with the talk priority given to any the game lacks.</summary>
        private static readonly (EPhraseTrigger trigger, float priority)[] VoiceTriggers =
        {
            (EPhraseTrigger.Gogogo, 143f),
            (EPhraseTrigger.Covering, 144f),
            (EPhraseTrigger.LostVisual, 145f),
            (EPhraseTrigger.NoisePhrase, 146f),
        };

        /// <summary>Team Stance ▸ Passive: the whole squad holds fire until shot at (Core/Stance.cs).</summary>
        public static void TeamStancePassive(McsCommandContext ctx) => SetStance(ctx, CarryStance.Passive, true);

        /// <summary>Team Stance ▸ Defensive: the whole squad stays close and prefers cover.</summary>
        public static void TeamStanceDefensive(McsCommandContext ctx) => SetStance(ctx, CarryStance.Defensive, true);

        /// <summary>Team Stance ▸ Aggressive: the whole squad pushes and flanks.</summary>
        public static void TeamStanceAggressive(McsCommandContext ctx) => SetStance(ctx, CarryStance.Aggressive, true);

        /// <summary>Team Stance ▸ Hyper-aggressive: the whole squad charges.</summary>
        public static void TeamStanceHyperAggressive(McsCommandContext ctx) => SetStance(ctx, CarryStance.HyperAggressive, true);

        /// <summary>Stance ▸ Passive, for one bot this raid.</summary>
        public static void StancePassive(McsCommandContext ctx) => SetStance(ctx, CarryStance.Passive, false);

        /// <summary>Stance ▸ Defensive, for one bot this raid.</summary>
        public static void StanceDefensive(McsCommandContext ctx) => SetStance(ctx, CarryStance.Defensive, false);

        /// <summary>Stance ▸ Aggressive, for one bot this raid.</summary>
        public static void StanceAggressive(McsCommandContext ctx) => SetStance(ctx, CarryStance.Aggressive, false);

        /// <summary>Stance ▸ Hyper-aggressive, for one bot this raid.</summary>
        public static void StanceHyperAggressive(McsCommandContext ctx) => SetStance(ctx, CarryStance.HyperAggressive, false);

        /// <summary>
        /// Defend Me: ends the bot's current task as Regroup does, places the squad's Defend zone at the
        /// player (TeamModes.StartDefend: spots 3-5 m out, shifting now and then, until the player moves
        /// 5 m away or orders Regroup), and answers "Copy, staying on you.". Runs once per bot that
        /// received the order. In combat SAIN still fights; the spots resume when the fight ends.
        /// </summary>
        public static void DefendMe(McsCommandContext ctx)
        {
            var botOwner = ctx?.McsBotPlayer?.AIData?.BotOwner;
            var data = botOwner?.GetMcsBotPlayerData();
            var player = data?.McsAILeadPlayer?.McsLeadPlayer;
            if (data == null || player == null)
            {
                return;
            }
            botOwner.WeaponManager?.Stationary?.DropCurWeapon(false, true);
            data.SetIntent([Intents.ShouldKeepFormation, Intents.ShouldFollowMe]);
            data.MoveResetRequested = true;
            data.IsLooting = false;
            data.TargetPos = null;
            data.ProxyTargetId = null;
            TeamModes.StartDefend(data.McsAILeadPlayer, player.Position);
            botOwner.TalkMsg(new McsMsg { PhraseTrigger = EPhraseTrigger.Roger, Keys = ["McsFork/DefendingYou"] });
        }

        /// <summary>
        /// Sets a stance and answers with the stance's acknowledgement. A team order runs once per bot
        /// (CommandUtils dispatches it to each): it writes the squad stance to the "Stance" setting,
        /// which BepInEx saves, and clears this bot's own stance so the squad's applies to it. A bot's
        /// own order sets only that bot's stance, for this raid. EngageTactics.EnsureStance applies the
        /// change on the bot's next SAIN decision.
        /// </summary>
        /// <param name="ctx">The command; its bot receives the stance.</param>
        /// <param name="stance">The stance chosen.</param>
        /// <param name="team">True for the Team menu's commands.</param>
        private static void SetStance(McsCommandContext ctx, CarryStance stance, bool team)
        {
            var botOwner = ctx?.McsBotPlayer?.AIData?.BotOwner;
            var data = botOwner?.GetMcsBotPlayerData();
            if (data == null)
            {
                return;
            }
            if (team)
            {
                if (ForkConfig.Stance != null && ForkConfig.Stance.Value != stance)
                {
                    ForkConfig.Stance.Value = stance;
                }
                data.Fork.StanceOverride = null;
            }
            else
            {
                data.Fork.StanceOverride = stance;
            }
            if (ForkConfig.DebugLog?.Value == true)
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] stance order: {botOwner.Profile?.Nickname} -> {stance}{(team ? " (squad, saved)" : " (this bot, this raid)")}");
            }
            botOwner.TalkMsg(new McsMsg { PhraseTrigger = EPhraseTrigger.Roger, Keys = [$"McsFork/Stance{stance}Ack"] });
        }

        /// <summary>
        /// Clear Target Area is refused while the squad is in contact: answers "Negative. We're in
        /// contact." and returns true when the command must stop.
        /// </summary>
        public static bool RefuseInContact(BotOwner botOwner, McsBotPlayerData data)
        {
            if (data?.McsAILeadPlayer == null || TeamModes.Get(data.McsAILeadPlayer)?.Mode != TeamMode.Combat)
            {
                return false;
            }
            botOwner.TalkMsg(new McsMsg { PhraseTrigger = EPhraseTrigger.Negative, Keys = ["McsFork/InContact"] });
            return true;
        }

        /// <summary>
        /// Subtitle handler for voice lines the fork replies with: when the reply carries keys, their
        /// text replaces the line's usual subtitle; without keys (upstream's own uses) the usual
        /// subtitle stays. Matches SubtitlesMgr's handler signature.
        /// </summary>
        public static string ReplaceWithKeys(string content, McsMsg msg, Player mcsLeadPlayer, Player mcsBotPlayer)
        {
            if (msg?.Keys == null || msg.Keys.Length == 0)
            {
                return content;
            }
            var sb = new StringBuilder();
            foreach (var key in msg.Keys)
            {
                sb.Append(key.McsLocalized());
            }
            return sb.ToString();
        }

        /// <summary>
        /// Makes sure the bot can say the fork's voice lines: the game's bot talk only plays triggers
        /// listed in its priority table, the way GameLoop adds Ready, Going and DontKnow upstream.
        /// </summary>
        public static void EnsureVoiceTriggers(BotOwner botOwner)
        {
            var priority = botOwner?.BotTalk?._priority;
            if (priority == null)
            {
                return;
            }
            foreach (var (trigger, value) in VoiceTriggers)
            {
                if (!priority.ContainsKey(trigger))
                {
                    priority.Add(trigger, value);
                }
            }
        }
    }
}
