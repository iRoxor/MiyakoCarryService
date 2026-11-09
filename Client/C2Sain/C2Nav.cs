// C2Nav.cs -- the fork's stricter spot picking for following and flanking, and the reach and
// cover tests the guard planner uses.
//
// Called by McsBaseLayer.UpdateLeadNearMoveTarget and McsGuardLayer (follow spots), EngageTactics
// (flank points) and GuardPlanner (reach and cover tests); DescribeCover serves only the diagnostic
// log (GuardPlanner.DescribePost, OpenGroundWatch). Upstream MCS sampled "the nearest
// navmesh point" around the player, which can lie behind a wall, in the next room or on another
// floor, and the bot then pathed there through doors and detours. Here a follow spot, and an
// indoor guard post, counts only if it is "in the player's space": within 1.5 m of the player's
// height and reachable from the player by a straight line on the navmesh. Outdoor guard posts
// need only a short path (IsNearPlayerOnFoot). Since fork v10 it also finds the closest spot a bot
// can reach when it has no path to the player (TryClosestReachable, for McsBaseLayer's follow and
// ForceTeleportCommandAction) and tests whether the player is on the navmesh (OffMeshWatch, the plan
// log). Reads and writes no files; uses Unity's navmesh and physics queries.

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
        /// Outdoors: true when the spot is within 3 m of the player's height and either a straight line
        /// across the navmesh from the player or a complete path no longer than 1.5 times the straight
        /// distance plus 4 m. Looser than IsInPlayersSpace, which small navmesh holes (tree trunks, rocks)
        /// make fail outdoors; indoor posts keep the strict test so they stay out of other rooms.
        /// </summary>
        public static bool IsNearPlayerOnFoot(Vector3 playerPos, Vector3 spot)
        {
            if (Mathf.Abs(spot.y - playerPos.y) > 3f)
            {
                return false;
            }
            if (!NavMesh.SamplePosition(playerPos, out var playerHit, 1.5f, NavMesh.AllAreas))
            {
                return false;
            }
            if (!NavMesh.Raycast(playerHit.position, spot, out _, NavMesh.AllAreas))
            {
                return true;
            }
            return TryCompletePath(playerHit.position, spot, out var corners)
                && PathLength(playerHit.position, corners) <= Vector3.Distance(playerPos, spot) * 1.5f + 4f;
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

        /// <summary>Fork v10: the off-navmesh tunables (Core/OffMesh.cs), shared by the fallback and OffMeshWatch.</summary>
        internal static readonly Core.OffMeshSettings OffMesh = new();

        /// <summary>Fork v10: TryClosestReachable's "why" when the bot already stands as close as it can get.</summary>
        public const string REACH_NO_CLOSER = "already as close as it can get";

        /// <summary>Fork v10: true when a navmesh point lies within OffMeshSettings.OnMeshRadius of the position.</summary>
        public static bool IsOnNavMesh(Vector3 position)
        {
            return NavMesh.SamplePosition(position, out _, OffMesh.OnMeshRadius, NavMesh.AllAreas);
        }

        /// <summary>
        /// Fork v10: the walkable point nearest the position, searching OffMeshSettings.SearchRadii in
        /// turn (smallest first). False when none lies within the largest radius. The point may be on
        /// a navmesh piece no bot can reach; TryClosestReachable deals with that.
        /// </summary>
        /// <param name="position">Where to search from (the player).</param>
        /// <param name="point">The nearest walkable point found.</param>
        public static bool TryNearestWalkable(Vector3 position, out Vector3 point)
        {
            foreach (var radius in OffMesh.SearchRadii)
            {
                if (NavMesh.SamplePosition(position, out var hit, radius, NavMesh.AllAreas))
                {
                    point = hit.position;
                    return true;
                }
            }
            point = default;
            return false;
        }

        /// <summary>
        /// Fork v10: the fallback when a bot has no path to the player (the player is off the navmesh,
        /// or on a piece of it the bot cannot reach). Finds the walkable point nearest the player
        /// (TryNearestWalkable) and the bot's path toward it; a partial path ends at the closest point
        /// the bot can reach. True only when that end is worth walking to (OffMeshRules.WorthMoving:
        /// at least MinGain closer to the player than the bot is). One SamplePosition per search radius
        /// and one CalculatePath per call.
        /// </summary>
        /// <param name="botPos">The bot's position.</param>
        /// <param name="playerPos">The player's position.</param>
        /// <param name="corners">The bot's path toward the spot, when one was found (even if not worth walking).</param>
        /// <param name="end">The path's last corner: the closest spot the bot can reach.</param>
        /// <param name="why">When false: "no walkable ground near you", "no path from the bot" or "already as close as it can get".</param>
        public static bool TryClosestReachable(Vector3 botPos, Vector3 playerPos, out Vector3[] corners, out Vector3 end, out string why)
        {
            corners = null;
            end = default;
            if (!TryNearestWalkable(playerPos, out var anchor))
            {
                why = "no walkable ground near you";
                return false;
            }
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(botPos, anchor, NavMesh.AllAreas, path)
                || path.status == NavMeshPathStatus.PathInvalid || path.corners == null || path.corners.Length == 0)
            {
                why = "no path from the bot";
                return false;
            }
            corners = path.corners;
            end = corners[corners.Length - 1];
            if (!Core.OffMeshRules.WorthMoving(Vector3.Distance(botPos, playerPos), Vector3.Distance(end, playerPos), OffMesh))
            {
                why = REACH_NO_CLOSER;
                return false;
            }
            why = null;
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
        /// Cover score of a spot toward a direction, looking 1.5 m ahead:
        ///   hard cover at waist height (0.7 m)                      +2
        ///   hard cover at eye height (1.6 m) that the bot can lean
        ///     around (clear 0.6 m to either side): a trunk or rock  +1
        ///   hard cover at eye height it cannot see past: a wall     -1
        ///   a bush around or just ahead of the spot                 +1.5 (+0.5 next to hard cover)
        /// <paramref name="covered"/> is true for waist-high cover, lean-around cover or a bush. Used by
        /// GuardPlanner and the flank-point picker.
        /// </summary>
        internal static float ScoreCover(Vector3 p, Vector3 toward, out bool covered)
        {
            var hard = LayersMaskController.HighPolyWithTerrainMask;
            var foliage = LayersMaskController.Foliage;
            var lowCover = Physics.Raycast(p + Vector3.up * 0.7f, toward, 1.5f, hard);
            var eye = p + Vector3.up * 1.6f;
            var eyeBlocked = Physics.Raycast(eye, toward, 1.5f, hard);
            var side = Vector3.Cross(Vector3.up, toward).normalized * 0.6f;
            var leanAround = eyeBlocked && (!Physics.Raycast(eye + side, toward, 1.5f, hard) || !Physics.Raycast(eye - side, toward, 1.5f, hard));
            var bush = Physics.CheckSphere(p + Vector3.up * 0.9f, 0.8f, foliage, QueryTriggerInteraction.Collide)
                || Physics.Raycast(p + Vector3.up * 1.0f, toward, 2f, foliage, QueryTriggerInteraction.Collide);

            var score = 0f;
            if (lowCover)
            {
                score += 2f;
            }
            if (eyeBlocked)
            {
                score += leanAround ? 1f : -1f;
            }
            if (bush)
            {
                score += lowCover ? 0.5f : 1.5f;
            }
            covered = lowCover || leanAround || bush;
            return score;
        }

        /// <summary>
        /// For the diagnostic log only: what ScoreCover's tests find at a spot toward a direction, naming
        /// the object each ray hit, e.g. "waist-high Rock_05 at 1.1 m, lean-around Pine_03 at 0.9 m, bush",
        /// or "nothing". It repeats ScoreCover's rays instead of sharing them, so ScoreCover stays exactly
        /// what scored the posts; keep the two in step if either changes.
        /// </summary>
        /// <param name="p">The spot (on the navmesh).</param>
        /// <param name="toward">The direction the bot watches from it.</param>
        internal static string DescribeCover(Vector3 p, Vector3 toward)
        {
            if (toward.sqrMagnitude < 0.0001f)
            {
                return "no watch direction";
            }
            var hard = LayersMaskController.HighPolyWithTerrainMask;
            var foliage = LayersMaskController.Foliage;
            var text = "";
            void Add(string part) => text = text.Length == 0 ? part : text + ", " + part;

            if (Physics.Raycast(p + Vector3.up * 0.7f, toward, out var low, 1.5f, hard))
            {
                Add($"waist-high {NameOf(low.collider)} at {low.distance:0.0} m");
            }
            var eye = p + Vector3.up * 1.6f;
            if (Physics.Raycast(eye, toward, out var high, 1.5f, hard))
            {
                var side = Vector3.Cross(Vector3.up, toward).normalized * 0.6f;
                var lean = !Physics.Raycast(eye + side, toward, 1.5f, hard) || !Physics.Raycast(eye - side, toward, 1.5f, hard);
                Add($"{(lean ? "lean-around" : "view blocked by")} {NameOf(high.collider)} at {high.distance:0.0} m");
            }
            if (Physics.CheckSphere(p + Vector3.up * 0.9f, 0.8f, foliage, QueryTriggerInteraction.Collide)
                || Physics.Raycast(p + Vector3.up * 1.0f, toward, 2f, foliage, QueryTriggerInteraction.Collide))
            {
                Add("bush");
            }
            return text.Length == 0 ? "nothing" : text;
        }

        /// <summary>A collider's object name for the log, cut to 32 characters.</summary>
        private static string NameOf(Collider collider)
        {
            if (collider == null)
            {
                return "?";
            }
            var name = collider.name;
            return name.Length > 32 ? name.Substring(0, 32) : name;
        }

        /// <summary>Length of a path that starts at <paramref name="from"/> and runs through the corners.</summary>
        internal static float PathLength(Vector3 from, Vector3[] corners)
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
