// ForkCommands.cs -- handlers for the commands the fork adds, and the hooks it puts on MCS's others.
//
// CommandMgr.Start registers EngageEnemy, EngageEnemyReckless, DefendMe and SpotEnemy
// (ContactReports.Report) with CommandUtils. CommandUtils.Execute calls
// BeforeCommand ahead of every handler, so any other order ends a running Engage
// order. CommandMgr.ClearAreaCommandAction calls RefuseInContact. SubtitlesMgr
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

        /// <summary>Engage Enemy: see EngageTactics. Refused ("Negative, no contact.") without a known enemy.</summary>
        public static void EngageEnemy(McsCommandContext ctx)
        {
            Engage(ctx, EngageKind.Tactical);
        }

        /// <summary>Engage Enemy Wrecklessly: see EngageTactics. Refused without a known enemy.</summary>
        public static void EngageEnemyReckless(McsCommandContext ctx)
        {
            Engage(ctx, EngageKind.Reckless);
        }

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

        /// <summary>Starts an Engage order for the context's bot, or refuses it.</summary>
        private static void Engage(McsCommandContext ctx, EngageKind kind)
        {
            var botOwner = ctx?.McsBotPlayer?.AIData?.BotOwner;
            var data = botOwner?.GetMcsBotPlayerData();
            if (data == null)
            {
                return;
            }
            if (!EngageTactics.HasEngageableContact(botOwner))
            {
                botOwner.TalkMsg(new McsMsg { PhraseTrigger = EPhraseTrigger.Negative, Keys = ["McsFork/NoContact"] });
                return;
            }
            botOwner.WeaponManager?.Stationary?.DropCurWeapon(false, true);
            EngageTactics.Begin(botOwner, data, kind);
        }

        /// <summary>
        /// Called by CommandUtils.Execute before any command handler runs: every order other than the
        /// Engage commands, the contact report and the purely informational ones ends a running Engage
        /// order (restoring the bot's personality).
        /// </summary>
        /// <param name="ctx">The command about to run.</param>
        public static void BeforeCommand(McsCommandContext ctx)
        {
            if (ctx?.CommandType == null || KeepsEngageOrder(ctx.CommandType))
            {
                return;
            }
            var botOwner = ctx.McsBotPlayer?.AIData?.BotOwner;
            var data = botOwner?.GetMcsBotPlayerData();
            if (data != null)
            {
                EngageTactics.Cancel(botOwner, data);
            }
        }

        /// <summary>Commands that leave a running Engage order alone.</summary>
        private static bool KeepsEngageOrder(string commandType)
        {
            return commandType == nameof(ECommandType.EngageEnemy)
                || commandType == nameof(ECommandType.EngageEnemyReckless)
                || commandType == nameof(ECommandType.SpotEnemy)
                || commandType == nameof(ECommandType.ReportAboutEnemy)
                || commandType == nameof(ECommandType.ReportAboutSelf)
                || commandType == nameof(ECommandType.AimingBodyPart)
                || commandType == nameof(ECommandType.OpenInventory)
                || commandType == nameof(ECommandType.ChangeFormation)
                || commandType == nameof(ECommandType.ExcludeOrTakeOver);
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
