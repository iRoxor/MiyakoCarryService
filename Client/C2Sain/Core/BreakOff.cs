// BreakOff.cs -- state of the "Break off from combat" command for one carry bot.
//
// The command (CommandMgr.FollowMeCommandAction in this fork) starts it; the C2
// layer gate (C2Sain/C2Gate.cs) keeps the Command-and-Control brain in charge
// while it is active, so the bot runs back to the player instead of fighting.
// It ends when the bot reaches the player or a timeout passes, and the gate then
// starts the chase leash's grace period. Unity-free for the logic harness.
// Reads and writes no files.

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Per-bot Break off state. Main-thread only.</summary>
    public sealed class BreakOff
    {
        /// <summary>True while the bot is running back to the player.</summary>
        public bool Active { get; private set; }

        /// <summary>Game time the current Break off started.</summary>
        public float StartedAt { get; private set; }

        /// <summary>Starts (or restarts) a Break off.</summary>
        /// <param name="now">Game time in seconds.</param>
        public void Begin(float now)
        {
            Active = true;
            StartedAt = now;
        }

        /// <summary>Ends a Break off without the grace period, e.g. when another command replaces it.</summary>
        public void Cancel()
        {
            Active = false;
        }

        /// <summary>
        /// Checks whether the Break off has finished.
        /// </summary>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="distanceToPlayer">Metres between the bot and the player.</param>
        /// <param name="arriveRadius">Distance that counts as having reached the player.</param>
        /// <param name="timeout">Seconds after which the Break off gives up.</param>
        /// <returns>True only on the call where it ends, so the caller can start the grace period once.</returns>
        public bool Update(float now, float distanceToPlayer, float arriveRadius, float timeout)
        {
            if (!Active)
            {
                return false;
            }

            if (distanceToPlayer <= arriveRadius || now - StartedAt >= timeout)
            {
                Active = false;
                return true;
            }
            return false;
        }
    }
}
