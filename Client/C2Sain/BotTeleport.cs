// BotTeleport.cs -- the one teleport routine every teleport of a carry bot uses (fork v17).
//
// Callers: the four MCS 30 s auto-teleports in McsBaseLayer (EndGoToPoint, EndFollowWalk, EndEscortToPointByWay, EndGoToProtect;
// full style), CommandMgr.ForceTeleportCommandAction (the shared mover reset only), and the reach watch (C2Sain/ReachWatch.cs: a
// stranded bot snapped back onto the navmesh or teleported to the player, and the catch-up teleport). Each caller keeps its own
// extra steps (Force Teleport sets its intents, says its line and clears the bot's targets; the 30 s teleport picks a follow spot).
// Before v17 each of the five wrote the mover reset out by hand and only Force Teleport avoided landing a bot off the navmesh when
// the player stood off it; MCS's 30 s teleport put it at the player's feet whatever was under them. Writes the bot's mover fields
// (_lastGoodCastPoint and the others named in ResetMover), moves the bot, writes the BepInEx log (a "teleport:" line when the
// player is off the navmesh, with the diagnostic log on). Reads and writes no files. Main thread only.

using EFT;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>The shared teleport routine; see the file header.</summary>
    internal static class BotTeleport
    {
        /// <summary>
        /// Where a bot teleported to the player should land: the player's own position, or, when the player stands off the navmesh,
        /// the walkable point nearest them (C2Nav.TryNearestWalkable, up to 32 m), because a bot dropped off the navmesh can never
        /// move again. Says so in the log (the line the summary tool counts) when it chooses the walkable point.
        /// </summary>
        /// <param name="playerPosition">The player's position.</param>
        /// <param name="botName">The bot's nickname, for the log line.</param>
        public static Vector3 LandingNear(Vector3 playerPosition, string botName)
        {
            if (!C2Nav.IsOnNavMesh(playerPosition) && C2Nav.TryNearestWalkable(playerPosition, out var walkable))
            {
                if (ForkConfig.DebugLog?.Value == true)
                {
                    McsLogger.LogInfo($"[{ForkLog.Tag}] teleport: you are off the navmesh, so {botName} lands on the nearest walkable ground, "
                        + $"{Vector3.Distance(walkable, playerPosition):0} m from you");
                }
                return walkable;
            }
            return playerPosition;
        }

        /// <summary>
        /// Teleports the bot to <paramref name="destination"/> and resets its mover there: tells the follow code (the walk after it
        /// starts at once), stops the bot's move, allows the teleport, moves it, then <see cref="ResetMover"/>.
        /// </summary>
        /// <param name="botOwner">The carry bot.</param>
        /// <param name="data">Its MCS data.</param>
        /// <param name="destination">Where to put it (a walkable point: use <see cref="LandingNear"/> for the player's side).</param>
        /// <param name="now">Time.time.</param>
        /// <param name="full">True for MCS's 30 s teleport's extra mover steps (also reset the cast-point time and the linked time,
        /// recalculate the way and pause the mover); false for Force Teleport's, which the reach watch also uses.</param>
        /// <returns>Where the bot stands after the move.</returns>
        public static Vector3 Move(BotOwner botOwner, McsBotPlayerData data, Vector3 destination, float now, bool full)
        {
            data.Fork.Follow.OnTeleport(now);
            botOwner.StopMove();
            botOwner.Mover.AllowTeleport();
            botOwner.GetPlayer.Teleport(destination, true);
            var landed = botOwner.GetPlayer.Position;
            ResetMover(botOwner, landed, now, full);
            return landed;
        }

        /// <summary>
        /// The mover reset every teleport shares: the mover's last good cast point, linked-from point, link position and way position
        /// are set to where the bot stands, the player is put on the navmesh there, and the stuck timer restarts. With
        /// <paramref name="full"/> the 30 s teleport's extra steps run too, in the order MCS wrote them.
        /// </summary>
        /// <param name="botOwner">The carry bot.</param>
        /// <param name="landed">Where the bot stands now.</param>
        /// <param name="now">Time.time.</param>
        /// <param name="full">See <see cref="Move"/>.</param>
        public static void ResetMover(BotOwner botOwner, Vector3 landed, float now, bool full)
        {
            var mover = botOwner.Mover;
            mover._lastGoodCastPoint = mover._prevSuccessLinkedFrom = mover._prevLinkPos = mover.PositionOnWayInner = landed;
            if (full)
            {
                mover._lastGoodCastPointTime = now;
                mover._prevPosLinkedTime = 0f;
            }
            mover.SetPlayerToNavMesh(landed);
            if (full)
            {
                mover.RecalcWay();
                mover.Pause = true;
            }
            mover._lastTimePosChanged = now;
        }
    }
}
