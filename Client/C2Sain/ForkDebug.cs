// ForkDebug.cs -- the fork's lines in MCS's in-raid debug overlay.
//
// Called by McsBotPlayerData.UpdateBaseInfo, which MCS runs for each carry bot
// while the setting Mcs/DrawDebugInfo is on. Shows the squad mode and zone,
// whether the C2 layer is in charge and why, SAIN's decisions before and after
// the fork's policy, the bot's stance (fork v11: whose stance it is, and whether a
// Passive squad is holding fire), its SAIN personality, the aggressive tactics (role,
// flank leg) and the chase leash / Break off state. Reads and writes no files.

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
            if (!fork.C2Active && fork.HasGuardSpot && fork.GuardPostKind.Length > 0 && data.LeadPlayer != null)
            {
                _builder.Append('\n').Append("post: ").Append(fork.GuardPostKind).Append(", ")
                    .Append(Vector3.Distance(fork.GuardSpot, data.LeadPlayer.Position).ToString("0")).Append(" m from you")
                    .Append(fork.GuardSpotCovered ? ", in cover" : "");
            }
            var stance = EngageTactics.CurrentStance(fork);
            _builder.Append('\n').Append("stance: ").Append(stance.ToString()).Append(fork.StanceOverride.HasValue ? " (this bot)" : " (squad)");
            if (stance == Core.CarryStance.Passive && data.McsAILeadPlayer != null)
            {
                _builder.Append(TeamModes.IsPassiveReleased(data.McsAILeadPlayer) ? ", released" : ", holding fire");
            }
            var personality = SainBridge.GetPersonality(SainBridge.GetSain(data.BotOwner));
            if (personality != null)
            {
                _builder.Append('\n').Append("personality: ").Append(personality.Value.ToString());
                if (fork.HasSainRoll && fork.SainRolledPersonality != personality.Value)
                {
                    _builder.Append(" (SAIN rolled ").Append(fork.SainRolledPersonality.ToString()).Append(')');
                }
            }
            var engage = fork.Engage;
            if (engage.Active)
            {
                _builder.Append('\n').Append("tactics: ").Append(engage.Kind.ToString()).Append(' ').Append((now - engage.StartTime).ToString("0")).Append(" s");
                if (fork.EngageRole.Length > 0)
                {
                    _builder.Append(", ").Append(fork.EngageRole);
                }
                if (engage.FlankActive)
                {
                    var point = new Vector3(engage.FlankPoint.X, engage.FlankPoint.Y, engage.FlankPoint.Z);
                    _builder.Append(", flank point ").Append(Vector3.Distance(data.BotOwner.Position, point).ToString("0")).Append(" m");
                }
            }
            return _builder.ToString();
        }
    }
}
