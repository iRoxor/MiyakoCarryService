// ForkDebug.cs -- the fork's lines in MCS's in-raid debug overlay.
//
// Called by McsBotPlayerData.UpdateBaseInfo, which MCS runs for each carry bot
// while the setting Mcs/DrawDebugInfo is on. Shows the squad mode and zone,
// whether the C2 layer is in charge and why, SAIN's decisions before and after
// the fork's policy, and the chase leash / Break off state. Reads and writes no files.

using System.Text;
using MiyakoCarryService.Client.Datas;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Overlay text builder.</summary>
    internal static class ForkDebug
    {
        private static readonly StringBuilder _builder = new();

        /// <summary>Multi-line overlay text for one bot.</summary>
        /// <param name="data">The bot's MCS data.</param>
        public static string Describe(McsBotPlayerData data)
        {
            var fork = data.Fork;
            var now = Time.time;
            var team = data.McsAILeadPlayer != null ? TeamModes.Get(data.McsAILeadPlayer) : null;

            _builder.Clear();
            _builder.Append(ForkInfo.Label).Append(" | squad: ").Append(team != null ? team.Mode.ToString() : "-");
            if (team != null && team.HasZone)
            {
                _builder.Append(" (zone ").Append(team.ZoneRadius.ToString("0")).Append(" m)");
            }

            _builder.Append('\n').Append("C2: ").Append(fork.C2Active ? "in charge" : "stepped aside").Append(" (").Append(fork.C2Reason).Append(')');
            _builder.Append('\n').Append("SAIN: ").Append(fork.RawDecision);
            if (fork.FinalDecision != fork.RawDecision)
            {
                _builder.Append(" -> ").Append(fork.FinalDecision).Append(" [").Append(fork.VetoReason).Append(']');
            }

            var leash = fork.Leash;
            if (leash.Chasing)
            {
                _builder.Append('\n').Append("chasing ").Append(leash.ChaseElapsed(now).ToString("0.0")).Append(" / ").Append(leash.ChaseLimit.ToString("0.0")).Append(" s");
            }
            if (leash.Returning)
            {
                _builder.Append('\n').Append("chase cut: returning to player");
            }
            var grace = leash.GraceLeft(now);
            if (grace > 0f)
            {
                _builder.Append('\n').Append("no-chase grace ").Append(grace.ToString("0")).Append(" s");
            }
            if (fork.BreakOff.Active)
            {
                _builder.Append('\n').Append("breaking off");
            }
            return _builder.ToString();
        }
    }
}
