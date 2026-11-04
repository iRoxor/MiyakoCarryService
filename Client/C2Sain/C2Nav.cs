// C2Nav.cs -- the fork's stricter spot picking for following, guarding and flanking.
//
// Called by McsBaseLayer.UpdateLeadNearMoveTarget (follow spots), McsGuardLayer
// (guard spots) and EngageTactics (flank points). Upstream MCS sampled "the
// nearest navmesh point" around the player, which can lie behind a wall, in the
// next room or on another floor, and the bot then pathed there through doors and
// detours. Here a follow or guard spot counts only if it is "in the player's
// space": within 1.5 m of the player's height and reachable from the player by a
// straight line on the navmesh. Guard and flank spots are scored for hard cover
// and bushes. Reads and writes no files; uses Unity's navmesh and physics queries.

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
        /// Picks a guard spot 2.5-8 m from the player inside the bot's sector. Scoring, toward the
        /// direction the bot will watch (the reported threat when there is one, else outward):
        /// low hard cover +2, a bush the bot can sit in or behind +1.5 (+0.5 next to hard cover),
        /// a wall at eye height -1 (cannot see over it), another guard within 2 m -3. Bushes are
        /// concealment rather than cover; carry bots can see out of them (SainFoliageSightPatch).
        /// </summary>
        /// <param name="playerPos">The player's position.</param>
        /// <param name="botPos">The bot's position (the spot must be reachable from it).</param>
        /// <param name="sectorYaw">Centre of this bot's sector, degrees around the player.</param>
        /// <param name="otherSpots">Spots other guards already hold.</param>
        /// <param name="threat">A reported threat to face, or null to watch outward.</param>
        /// <param name="spot">The chosen spot.</param>
        /// <param name="watch">Horizontal direction the bot should watch from the spot.</param>
        /// <param name="covered">True when the spot has hard cover or a bush toward <paramref name="watch"/>.</param>
        public static bool TryPickGuardSpot(Vector3 playerPos, Vector3 botPos, float sectorYaw, IReadOnlyList<Vector3> otherSpots,
            Vector3? threat, out Vector3 spot, out Vector3 watch, out bool covered)
        {
            var bestScore = float.MinValue;
            var best = default(Vector3);
            var bestWatch = default(Vector3);
            var bestCovered = false;
            var found = false;

            for (var attempt = 0; attempt < 16; attempt++)
            {
                var dir = Quaternion.Euler(0f, sectorYaw + Random.Range(-50f, 50f), 0f) * Vector3.forward;
                var candidate = playerPos + dir * Random.Range(2.5f, 8f);
                if (!NavMesh.SamplePosition(candidate, out var hit, 1f, NavMesh.AllAreas))
                {
                    continue;
                }
                var p = hit.position;
                if (!IsInPlayersSpace(playerPos, p))
                {
                    continue;
                }

                var look = dir;
                if (threat.HasValue)
                {
                    look = threat.Value - p;
                    look.y = 0f;
                    look = look.sqrMagnitude > 0.01f ? look.normalized : dir;
                }

                var score = ScoreCover(p, look, out var isCovered);
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
                    bestWatch = look;
                    bestCovered = isCovered;
                    found = true;
                }
            }

            if (!found || !TryCompletePath(botPos, best, out _))
            {
                spot = default;
                watch = default;
                covered = false;
                return false;
            }

            spot = best;
            watch = bestWatch;
            covered = bestCovered;
            return true;
        }

        /// <summary>
        /// Picks the end of a flank leg: a navmesh point off to one side of the enemy's last known
        /// position, 40-85 degrees around from the line between the enemy and the bot, at 0.7 times the
        /// bot's distance (10-40 m) from the enemy. A candidate needs a complete path from the bot that is
        /// at most 2.2 times the straight distance (or that plus 40 m) and never passes within 8 m (or
        /// 0.4 times the radius) of the enemy. Prefers cover toward the enemy, then the shorter path.
        /// </summary>
        /// <param name="botPos">The bot's position.</param>
        /// <param name="enemyPos">The enemy's last known position.</param>
        /// <param name="preferredSide">+1 or -1 to try only that side, 0 to try both.</param>
        /// <param name="spot">The chosen point.</param>
        /// <param name="side">The side it lies on (+1 the bot's right as it faces the enemy, -1 its left).</param>
        /// <returns>False when the enemy is closer than 12 m or farther than 150 m, or no candidate qualifies.</returns>
        public static bool TryPickFlankSpot(Vector3 botPos, Vector3 enemyPos, int preferredSide, out Vector3 spot, out int side)
        {
            spot = default;
            side = 0;
            var toBot = botPos - enemyPos;
            toBot.y = 0f;
            var distance = toBot.magnitude;
            if (distance < 12f || distance > 150f)
            {
                return false;
            }
            var baseDir = toBot / distance;
            var radius = Mathf.Clamp(distance * 0.7f, 10f, 40f);
            var maxPath = Mathf.Max(2.2f * distance, distance + 40f);
            var keepAway = Mathf.Max(8f, 0.4f * radius);
            var bestScore = float.MinValue;
            var sideCount = preferredSide != 0 ? 1 : 2;

            for (var s = 0; s < sideCount; s++)
            {
                var trySide = preferredSide != 0 ? preferredSide : (s == 0 ? 1 : -1);
                foreach (var angle in FlankAngles)
                {
                    // Seen from above, a negative yaw turns the enemy-to-bot line anticlockwise, which
                    // is the bot's right-hand side as it faces the enemy.
                    var dir = Quaternion.Euler(0f, -trySide * angle, 0f) * baseDir;
                    if (!NavMesh.SamplePosition(enemyPos + dir * radius, out var hit, 4f, NavMesh.AllAreas))
                    {
                        continue;
                    }
                    var p = hit.position;
                    if (Mathf.Abs(p.y - enemyPos.y) > 6f || !TryCompletePath(botPos, p, out var corners))
                    {
                        continue;
                    }
                    var length = PathLength(botPos, corners);
                    if (length > maxPath || MinDistanceToPath(botPos, corners, enemyPos) < keepAway)
                    {
                        continue;
                    }
                    var toEnemy = enemyPos - p;
                    toEnemy.y = 0f;
                    var score = ScoreCover(p, toEnemy.normalized, out _) - length * 0.01f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        spot = p;
                        side = trySide;
                    }
                }
            }
            return side != 0;
        }

        /// <summary>Angles (degrees off the enemy-to-bot line) tried for a flank point, best first.</summary>
        private static readonly float[] FlankAngles = { 70f, 55f, 85f, 40f };

        /// <summary>
        /// Cover score of a spot toward a direction; see TryPickGuardSpot for the weights.
        /// <paramref name="covered"/> is true when there is hard cover or a bush.
        /// </summary>
        private static float ScoreCover(Vector3 p, Vector3 toward, out bool covered)
        {
            var hard = LayersMaskController.HighPolyWithTerrainMask;
            var foliage = LayersMaskController.Foliage;
            var lowCover = Physics.Raycast(p + Vector3.up * 0.7f, toward, 1.5f, hard);
            var eyeWall = Physics.Raycast(p + Vector3.up * 1.6f, toward, 1.5f, hard);
            var bush = Physics.CheckSphere(p + Vector3.up * 0.9f, 0.8f, foliage, QueryTriggerInteraction.Collide)
                || Physics.Raycast(p + Vector3.up * 1.0f, toward, 2f, foliage, QueryTriggerInteraction.Collide);

            var score = 0f;
            if (lowCover)
            {
                score += 2f;
            }
            if (bush)
            {
                score += lowCover ? 0.5f : 1.5f;
            }
            if (eyeWall)
            {
                score -= 1f;
            }
            covered = lowCover || bush;
            return score;
        }

        /// <summary>Length of a path that starts at <paramref name="from"/> and runs through the corners.</summary>
        private static float PathLength(Vector3 from, Vector3[] corners)
        {
            var length = 0f;
            var previous = from;
            for (var i = 0; i < corners.Length; i++)
            {
                length += Vector3.Distance(previous, corners[i]);
                previous = corners[i];
            }
            return length;
        }

        /// <summary>Closest horizontal distance between a point and a path (start plus corners).</summary>
        private static float MinDistanceToPath(Vector3 from, Vector3[] corners, Vector3 point)
        {
            var best = float.MaxValue;
            var previous = from;
            for (var i = 0; i < corners.Length; i++)
            {
                best = Mathf.Min(best, DistanceToSegment(point, previous, corners[i]));
                previous = corners[i];
            }
            return best;
        }

        /// <summary>Horizontal distance from a point to the segment a-b.</summary>
        private static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            var ab = new Vector2(b.x - a.x, b.z - a.z);
            var ap = new Vector2(point.x - a.x, point.z - a.z);
            var lengthSq = ab.sqrMagnitude;
            var t = lengthSq > 1e-6f ? Mathf.Clamp01(Vector2.Dot(ap, ab) / lengthSq) : 0f;
            return (ap - ab * t).magnitude;
        }
    }
}
