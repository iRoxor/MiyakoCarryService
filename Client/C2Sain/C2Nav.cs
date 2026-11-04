// C2Nav.cs -- the fork's stricter spot picking for following and guarding.
//
// Called by McsBaseLayer.UpdateLeadNearMoveTarget (follow spots) and
// McsGuardLayer (guard spots). Upstream MCS sampled "the nearest navmesh point"
// around the player, which can lie behind a wall, in the next room or on
// another floor, and the bot then pathed there through doors and detours. Here
// a spot counts only if it is "in the player's space": within 1.5 m of the
// player's height and reachable from the player by a straight line on the
// navmesh. Reads and writes no files; uses Unity's navmesh and physics queries.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Spot selection helpers. Main-thread only (Unity APIs).</summary>
    internal static class C2Nav
    {
        /// <summary>Largest height difference (m) between the player and an accepted spot.</summary>
        private const float MAX_HEIGHT_DIFF = 1.5f;

        /// <summary>
        /// True when the spot shares the player's floor and open space: small height difference and
        /// an unobstructed straight line across the navmesh from the player to it.
        /// </summary>
        public static bool IsInPlayersSpace(Vector3 playerPos, Vector3 spot)
        {
            if (Mathf.Abs(spot.y - playerPos.y) > MAX_HEIGHT_DIFF)
            {
                return false;
            }
            if (!NavMesh.SamplePosition(playerPos, out var playerHit, 1.5f, NavMesh.AllAreas))
            {
                return false;
            }
            return !NavMesh.Raycast(playerHit.position, spot, out _, NavMesh.AllAreas);
        }

        /// <summary>A complete navmesh path between two points, or false.</summary>
        public static bool TryCompletePath(Vector3 from, Vector3 to, out Vector3[] corners)
        {
            corners = null;
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
            {
                return false;
            }
            corners = path.corners;
            return corners != null && corners.Length > 0;
        }

        /// <summary>
        /// Picks a follow spot 1.5-4 m from the player, on the bot's side, in the player's space and
        /// reachable by the bot. Up to eight random tries.
        /// </summary>
        /// <param name="playerPos">The player's position.</param>
        /// <param name="botPos">The bot's position.</param>
        /// <param name="spot">The chosen spot.</param>
        /// <param name="corners">The bot's path to it.</param>
        public static bool TryPickFollowSpot(Vector3 playerPos, Vector3 botPos, out Vector3 spot, out Vector3[] corners)
        {
            var toBot = botPos - playerPos;
            toBot.y = 0f;
            var baseYaw = toBot.sqrMagnitude > 0.01f ? Mathf.Atan2(toBot.x, toBot.z) * Mathf.Rad2Deg : Random.Range(0f, 360f);

            for (var attempt = 0; attempt < 8; attempt++)
            {
                var yaw = baseYaw + Random.Range(-90f, 90f);
                var candidate = playerPos + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * Random.Range(1.5f, 4f);
                if (!NavMesh.SamplePosition(candidate, out var hit, 1f, NavMesh.AllAreas))
                {
                    continue;
                }
                if (!IsInPlayersSpace(playerPos, hit.position) || !TryCompletePath(botPos, hit.position, out corners))
                {
                    continue;
                }
                spot = hit.position;
                return true;
            }

            spot = default;
            corners = null;
            return false;
        }

        /// <summary>
        /// Picks a guard spot 2.5-6 m from the player inside the bot's sector, preferring low cover
        /// on the outward side and avoiding other guards' spots.
        /// </summary>
        /// <param name="playerPos">The player's position.</param>
        /// <param name="botPos">The bot's position (the spot must be reachable from it).</param>
        /// <param name="sectorYaw">Centre of this bot's sector, degrees around the player.</param>
        /// <param name="otherSpots">Spots other guards already hold.</param>
        /// <param name="spot">The chosen spot.</param>
        /// <param name="outward">Horizontal direction from the player to the spot; the bot watches it.</param>
        public static bool TryPickGuardSpot(Vector3 playerPos, Vector3 botPos, float sectorYaw, IReadOnlyList<Vector3> otherSpots, out Vector3 spot, out Vector3 outward)
        {
            var mask = LayersMaskController.HighPolyWithTerrainMask;
            var bestScore = float.MinValue;
            var best = default(Vector3);
            var bestDir = default(Vector3);
            var found = false;

            for (var attempt = 0; attempt < 12; attempt++)
            {
                var dir = Quaternion.Euler(0f, sectorYaw + Random.Range(-50f, 50f), 0f) * Vector3.forward;
                var candidate = playerPos + dir * Random.Range(2.5f, 6f);
                if (!NavMesh.SamplePosition(candidate, out var hit, 1f, NavMesh.AllAreas))
                {
                    continue;
                }
                var p = hit.position;
                if (!IsInPlayersSpace(playerPos, p))
                {
                    continue;
                }

                var score = 0f;
                if (Physics.Raycast(p + Vector3.up * 0.7f, dir, 1.5f, mask))
                {
                    score += 2f;
                }
                if (Physics.Raycast(p + Vector3.up * 1.6f, dir, 1.5f, mask))
                {
                    score -= 1f;
                }
                for (var i = 0; i < otherSpots.Count; i++)
                {
                    if ((otherSpots[i] - p).sqrMagnitude < 2f * 2f)
                    {
                        score -= 3f;
                    }
                }
                score -= Random.value * 0.1f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                    bestDir = dir;
                    found = true;
                }
            }

            if (!found || !TryCompletePath(botPos, best, out _))
            {
                spot = default;
                outward = default;
                return false;
            }

            spot = best;
            outward = bestDir;
            return true;
        }
    }
}
