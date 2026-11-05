// GuardPlanner.cs -- where each carry bot stands while the squad guards the player.
//
// Called by TeamModes.GetGuardPost when a squad's guard plan is made or remade (no plan yet, the
// player moved, a contact report arrived or expired, or a bot died), and by TeamModes.MakePlan for
// the diagnostic log's line per post (DescribePost). Uses Unity's navmesh and
// physics queries and MCS's cached navmesh triangulation (CommandUtils.GetNavMeshVertices). Reads
// and writes no files.
//
// Plans, in order:
//   contact report -- posts fanned out facing the mark (GuardFormation.FanYaws), as in fork v2;
//   one bot        -- one post watching the player's back;
//   indoors        -- one post per approach (doorway, hallway, stairwell), busiest first, on the
//                     player's side of it with a view down it; spare bots fill the widest gaps.
//                     Falls back to the detail when no approach is found;
//   otherwise      -- the detail: posts evenly spaced around the player (GuardFormation.EvenYaws).
// Defend Me has its own plan (PlanDefend): one spot per bot DEFEND_MIN-DEFEND_MAX m from the player,
// each in its own sector, and a bot shifting position takes another spot in the same sector
// (ShiftDefend). TeamModes calls both.
//
// A post is placed by BestSpot, within a sector either side of the post's direction. Candidates
// come from two sources: points just behind solid objects in the sector, found the way SAIN's combat
// cover finder finds them (any collider at least 0.75 m tall and 0.25 m wide: tree trunks, rocks,
// walls' ends, vehicles, bushes), and random points in the sector. Each is scored by
// C2Nav.ScoreCover. Fork v2 only fired one ray from random points, so a tree trunk counted only when
// that ray happened to hit it.
//
// Detail and contact-report posts try wider sectors (SectorPost, GuardFormationSettings) when the
// narrower one's best spot has no cover, and take the narrowest covered spot. Until fork v4 a wider
// sector was tried only when the narrower one had no spot at all, so in open ground a bare spot
// beat cover just outside the first sector. Since fork v6 every outdoor post and Defend Me spot also
// keeps out of the player's line of fire: no spot within LineOfFireClearance degrees of where the
// player looks, or of the reported enemy for a post facing a contact report.

using System.Collections.Generic;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Utils;
using UnityEngine;
using UnityEngine.AI;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>One bot's guard post.</summary>
    internal sealed class GuardPost
    {
        /// <summary>Where the bot stands (on the navmesh).</summary>
        public Vector3 Spot;

        /// <summary>Horizontal unit direction the bot watches from the spot.</summary>
        public Vector3 Watch;

        /// <summary>
        /// True when the spot has cover or a bush toward <see cref="Watch"/>; the bot then reports
        /// C2Sain:WatchFromCover there, otherwise C2Sain:Watch. Since fork v5 bots crouch at every post.
        /// </summary>
        public bool Covered;

        /// <summary>Which plan made it, for the overlay: detail, approach, gap, solo, fan or defend.</summary>
        public string Kind;

        /// <summary>Yaw of the sector the spot was searched in; a Defend Me shift searches the same sector again.</summary>
        public float SectorYaw;
    }

    /// <summary>Guard plans; see the file header. Main thread only.</summary>
    internal static class GuardPlanner
    {
        /// <summary>Smallest object height (m) that counts as cover; SAIN's default CoverMinHeight.</summary>
        private const float OBJECT_MIN_HEIGHT = 0.75f;

        /// <summary>Smallest object width (m) that counts as cover; SAIN's cover finder uses the same.</summary>
        private const float OBJECT_MIN_WIDTH = 0.25f;

        /// <summary>Largest object footprint (m) treated as one object; buildings and terrain are left to the sampling.</summary>
        private const float OBJECT_MAX_WIDTH = 8f;

        /// <summary>Metres along a route at which it counts as leaving the player's surroundings indoors.</summary>
        private const float EXIT_DISTANCE = 6f;

        /// <summary>Metres along a route used to tell which way an approach leads.</summary>
        private const float BEYOND_DISTANCE = 10f;

        /// <summary>Route destinations: this far away horizontally (m), and at most this far up or down.</summary>
        private const float DESTINATION_MIN = 12f;
        private const float DESTINATION_MAX = 35f;
        private const float DESTINATION_MAX_HEIGHT = 8f;

        /// <summary>Most routes traced per indoor plan (each is one navmesh path query).</summary>
        private const int MAX_ROUTES = 40;

        /// <summary>Fewest routes needed before approaches are trusted.</summary>
        private const int MIN_ROUTES = 3;

        /// <summary>Most cover objects tried per post, so a plan in dense woods stays within a frame.</summary>
        private const int MAX_OBJECTS_PER_POST = 30;

        /// <summary>Closest two posts may be (m).</summary>
        private const float MIN_POST_SPACING = 2.5f;

        /// <summary>Indoor posts stay within this many metres of the player.</summary>
        private const float INDOOR_MAX_RANGE = 8f;

        /// <summary>Defend Me spots are this many metres from the player (the user's design: 3-5 m).</summary>
        private const float DEFEND_MIN = 3f;
        private const float DEFEND_MAX = 5f;

        /// <summary>A Defend Me shift moves the bot at least this far (m), so it visibly changes angle.</summary>
        private const float SHIFT_MIN_MOVE = 1.5f;

        /// <summary>
        /// Longest walk (m of navmesh path) a Defend Me shift may take. The design asks for moves of
        /// 3 s or less; 6 m is about 3 s at the pace GuardMoveLogic walks, not measured in game.
        /// </summary>
        private const float SHIFT_MAX_PATH = 6f;

        private static readonly Collider[] _colliders = new Collider[512];
        private static readonly List<Collider> _objects = new();
        private static readonly HashSet<(int, int, int)> _cells = new();
        private static readonly List<Vector3> _destinations = new();
        private static readonly List<V3> _exits = new();
        private static readonly List<Vector3> _beyond = new();
        private static readonly List<GuardPost> _sectorPosts = new();
        private static readonly List<bool?> _sectorResults = new();
        private static readonly GuardFormationSettings _formation = new();

        /// <summary>
        /// Plans posts for a squad of <paramref name="botCount"/> bots. Returns up to that many posts;
        /// fewer when the surroundings offer no usable spot for some.
        /// </summary>
        /// <param name="player">The player's position.</param>
        /// <param name="look">The player's view direction.</param>
        /// <param name="botCount">Living carry bots in the squad.</param>
        /// <param name="threat">A contact report to face, or null.</param>
        /// <param name="indoors">True inside the game's indoor zones (the player's AIData.EnvironmentId is not 0).</param>
        /// <param name="minRange">Nearest a post may be to the player (m), from the settings.</param>
        /// <param name="maxRange">Farthest a post may be from the player (m), from the settings.</param>
        public static List<GuardPost> Plan(Vector3 player, Vector3 look, int botCount, Vector3? threat, bool indoors, float minRange, float maxRange)
        {
            var posts = new List<GuardPost>();
            if (botCount <= 0)
            {
                return posts;
            }
            var lookYaw = GuardFormation.YawOf(look.x, look.z);
            if (indoors)
            {
                maxRange = Mathf.Min(maxRange, INDOOR_MAX_RANGE);
                minRange = Mathf.Min(minRange, maxRange - 1f);
            }
            CollectObjects(player, maxRange + 2f);

            if (threat.HasValue)
            {
                var toThreat = threat.Value - player;
                var threatYaw = GuardFormation.YawOf(toThreat.x, toThreat.z);
                var yaws = botCount == 1 ? new[] { threatYaw } : GuardFormation.FanYaws(botCount, threatYaw);
                foreach (var yaw in yaws)
                {
                    AddIfFound(posts, SectorPost(player, yaw, _formation.FanWindows, minRange, maxRange, threat, indoors, posts, "fan", threatYaw));
                }
                return posts;
            }

            if (botCount == 1)
            {
                AddIfFound(posts, BestSpot(player, GuardFormation.SoloYaw(lookYaw), 90f, minRange, maxRange, null, indoors, posts, "solo", lineYaw: lookYaw));
                return posts;
            }

            if (indoors && TryApproachPosts(player, lookYaw, botCount, minRange, maxRange, posts))
            {
                return posts;
            }
            posts.Clear();

            foreach (var yaw in GuardFormation.EvenYaws(botCount, lookYaw))
            {
                AddIfFound(posts, SectorPost(player, yaw, _formation.DetailWindows, minRange, maxRange, null, indoors, posts, "detail", lookYaw));
            }
            return posts;
        }

        /// <summary>
        /// Defend Me: one spot per bot DEFEND_MIN-DEFEND_MAX m from the player, each bot in its own
        /// sector (GuardFormation.EvenYaws: two bots take the flanks, a lone bot the player's back),
        /// behind cover where the sector has any, and facing a contact report when there is one.
        /// Returns up to <paramref name="botCount"/> spots; fewer when a sector offers none.
        /// </summary>
        /// <param name="player">The player's position.</param>
        /// <param name="look">The player's view direction.</param>
        /// <param name="botCount">Living carry bots in the squad.</param>
        /// <param name="threat">A contact report to face, or null.</param>
        /// <param name="indoors">True inside the game's indoor zones; spots then need a straight navmesh line to the player.</param>
        public static List<GuardPost> PlanDefend(Vector3 player, Vector3 look, int botCount, Vector3? threat, bool indoors)
        {
            var posts = new List<GuardPost>();
            if (botCount <= 0)
            {
                return posts;
            }
            CollectObjects(player, DEFEND_MAX + 2f);
            var window = DefendWindow(botCount);
            var lookYaw = GuardFormation.YawOf(look.x, look.z);
            foreach (var yaw in GuardFormation.EvenYaws(botCount, lookYaw))
            {
                AddIfFound(posts, BestSpot(player, yaw, window, DEFEND_MIN, DEFEND_MAX, threat, indoors, posts, "defend", lineYaw: lookYaw));
            }
            return posts;
        }

        /// <summary>
        /// Defend Me: a new spot for a bot shifting position. It is in the same sector as the current
        /// spot, at least SHIFT_MIN_MOVE m from it, at most SHIFT_MAX_PATH m of walking from the bot,
        /// and clear of the other bots' spots. Null when the sector has no such spot; the bot then
        /// stays where it is. Runs a navmesh path query per candidate that passes the cheaper tests.
        /// </summary>
        /// <param name="look">The player's view direction now, kept clear (GuardFormationSettings.LineOfFireClearance).</param>
        /// <param name="current">The bot's current Defend Me spot.</param>
        /// <param name="botPos">The bot's position.</param>
        /// <param name="others">The other bots' spots.</param>
        public static GuardPost ShiftDefend(Vector3 player, Vector3 look, GuardPost current, Vector3 botPos, int botCount, Vector3? threat, bool indoors, List<GuardPost> others)
        {
            CollectObjects(player, DEFEND_MAX + 2f);
            return BestSpot(player, current.SectorYaw, DefendWindow(botCount), DEFEND_MIN, DEFEND_MAX, threat, indoors, others, "defend",
                p => (p - current.Spot).sqrMagnitude >= SHIFT_MIN_MOVE * SHIFT_MIN_MOVE
                    && C2Nav.TryCompletePath(botPos, p, out var corners)
                    && C2Nav.PathLength(botPos, corners) <= SHIFT_MAX_PATH,
                GuardFormation.YawOf(look.x, look.z));
        }

        /// <summary>Half-width (degrees) of each Defend Me sector: the whole circle for one bot, an even share otherwise.</summary>
        private static float DefendWindow(int botCount)
        {
            return botCount <= 1 ? 180f : 180f / botCount;
        }

        /// <summary>
        /// The post for one direction. Runs BestSpot in each sector of <paramref name="windows"/> (degrees
        /// either side of <paramref name="yaw"/>), narrowest first, and stops at the first sector whose
        /// best spot has cover. GuardFormation.ChooseWindow then picks that spot, or the narrowest bare
        /// one when no sector had cover. With GuardFormationSettings.WidenForCover off it stops at the
        /// first spot of either kind, fork v3-v4's rule for detail posts (v3-v4 searched contact-report
        /// posts in the 50-degree sector only). Null when no sector had a spot.
        /// </summary>
        /// <param name="windows">Sector half-widths to try, narrowest first.</param>
        /// <param name="kind">The post kind for the overlay and the log ("detail", "fan").</param>
        /// <param name="lineYaw">The line of fire to keep clear: where the player looks, or toward the reported enemy.</param>
        private static GuardPost SectorPost(Vector3 player, float yaw, float[] windows, float minRange, float maxRange,
            Vector3? threat, bool indoors, List<GuardPost> taken, string kind, float lineYaw)
        {
            _sectorPosts.Clear();
            _sectorResults.Clear();
            foreach (var window in windows)
            {
                var post = BestSpot(player, yaw, window, minRange, maxRange, threat, indoors, taken, kind, lineYaw: lineYaw);
                _sectorPosts.Add(post);
                _sectorResults.Add(post?.Covered);
                if (post != null && (post.Covered || !_formation.WidenForCover))
                {
                    break;
                }
            }
            var index = GuardFormation.ChooseWindow(_sectorResults, _formation);
            return index >= 0 ? _sectorPosts[index] : null;
        }

        /// <summary>
        /// The best post in a sector: centred on <paramref name="yaw"/>, <paramref name="window"/> degrees
        /// either side, between the two ranges from the player, at least MIN_POST_SPACING m from posts
        /// already taken, and reachable from the player (indoors on a straight navmesh line, outdoors by a
        /// short path). With <paramref name="lineYaw"/>, a spot within LineOfFireClearance degrees of that
        /// bearing is refused. Null when no candidate qualifies.
        /// </summary>
        /// <param name="accept">An extra test a candidate must pass, run after the others; null accepts all.</param>
        /// <param name="lineYaw">The player's line of fire to keep clear, or null (indoor approach and gap posts).</param>
        private static GuardPost BestSpot(Vector3 player, float yaw, float window, float minRange, float maxRange,
            Vector3? threat, bool indoors, List<GuardPost> taken, string kind, System.Func<Vector3, bool> accept = null, float? lineYaw = null)
        {
            var sector = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            var guarded = threat ?? player + sector * 40f;
            var bestScore = float.MinValue;
            GuardPost best = null;
            var objectsTried = 0;

            foreach (var collider in _objects)
            {
                if (collider == null || objectsTried >= MAX_OBJECTS_PER_POST)
                {
                    continue;
                }
                var bounds = collider.bounds;
                var center = bounds.center;
                var fromPlayer = center - player;
                fromPlayer.y = 0f;
                var distance = fromPlayer.magnitude;
                if (distance < minRange - 1f || distance > maxRange + 1f
                    || GuardFormation.AngleBetween(GuardFormation.YawOf(fromPlayer.x, fromPlayer.z), yaw) > window)
                {
                    continue;
                }
                var away = center - guarded;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f)
                {
                    continue;
                }
                objectsTried++;
                var halfWidth = Mathf.Sqrt(bounds.size.x * bounds.size.x + bounds.size.z * bounds.size.z) * 0.5f;
                var baseY = Mathf.Clamp(bounds.min.y, player.y - 3f, player.y + 1f);
                Consider(new Vector3(center.x, baseY, center.z) + away.normalized * Mathf.Min(halfWidth + 0.6f, 3f));
            }

            for (var i = 0; i < 16; i++)
            {
                var direction = Quaternion.Euler(0f, yaw + Random.Range(-window, window), 0f) * Vector3.forward;
                Consider(player + direction * Random.Range(minRange, maxRange));
            }
            return best;

            void Consider(Vector3 raw)
            {
                if (!NavMesh.SamplePosition(raw, out var hit, 2f, NavMesh.AllAreas))
                {
                    return;
                }
                var p = hit.position;
                var fromPlayer = p - player;
                fromPlayer.y = 0f;
                var distance = fromPlayer.magnitude;
                if (distance < Mathf.Max(1.5f, minRange - 1f) || distance > maxRange + 1.5f)
                {
                    return;
                }
                if (lineYaw.HasValue && !GuardFormation.ClearOfLineOfFire(GuardFormation.YawOf(fromPlayer.x, fromPlayer.z), lineYaw.Value, _formation))
                {
                    return;
                }
                foreach (var other in taken)
                {
                    if ((other.Spot - p).sqrMagnitude < MIN_POST_SPACING * MIN_POST_SPACING)
                    {
                        return;
                    }
                }
                if (!(indoors ? C2Nav.IsInPlayersSpace(player, p) : C2Nav.IsNearPlayerOnFoot(player, p)))
                {
                    return;
                }
                if (accept != null && !accept(p))
                {
                    return;
                }
                var watch = sector;
                if (threat.HasValue)
                {
                    watch = threat.Value - p;
                    watch.y = 0f;
                    watch = watch.sqrMagnitude > 0.01f ? watch.normalized : sector;
                }
                var score = C2Nav.ScoreCover(p, watch, out var covered);
                score -= GuardFormation.AngleBetween(GuardFormation.YawOf(fromPlayer.x, fromPlayer.z), yaw) / Mathf.Max(window, 1f) * 0.75f;
                score -= Mathf.Abs(distance - (minRange + maxRange) * 0.5f) / Mathf.Max(maxRange, 1f) * 0.5f;
                score -= Random.value * 0.05f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new GuardPost { Spot = p, Watch = watch, Covered = covered, Kind = kind, SectorYaw = yaw };
                }
            }
        }

        /// <summary>
        /// Indoors: traces navmesh routes from the player to points 12-35 m away (other floors included),
        /// groups the points where they leave the player's surroundings into approaches, and posts one bot
        /// per approach, busiest first; spare bots take the widest gaps. Returns false (posts untouched)
        /// when too few routes or no usable approach post were found.
        /// </summary>
        private static bool TryApproachPosts(Vector3 player, float lookYaw, int botCount, float minRange, float maxRange, List<GuardPost> posts)
        {
            if (!NavMesh.SamplePosition(player, out var start, 1.5f, NavMesh.AllAreas))
            {
                return false;
            }
            var vertices = CommandUtils.GetNavMeshVertices();
            if (vertices == null || vertices.Length == 0)
            {
                return false;
            }

            _cells.Clear();
            _destinations.Clear();
            foreach (var v in vertices)
            {
                var dx = v.x - player.x;
                var dz = v.z - player.z;
                var flatSq = dx * dx + dz * dz;
                if (flatSq < DESTINATION_MIN * DESTINATION_MIN || flatSq > DESTINATION_MAX * DESTINATION_MAX
                    || Mathf.Abs(v.y - player.y) > DESTINATION_MAX_HEIGHT)
                {
                    continue;
                }
                if (_cells.Add((Mathf.FloorToInt(v.x / 5f), Mathf.FloorToInt(v.y / 3f), Mathf.FloorToInt(v.z / 5f))))
                {
                    _destinations.Add(v);
                }
            }

            _exits.Clear();
            _beyond.Clear();
            var path = new NavMeshPath();
            var stride = Mathf.Max(1, _destinations.Count / MAX_ROUTES);
            for (var i = 0; i < _destinations.Count && _exits.Count < MAX_ROUTES; i += stride)
            {
                if (!NavMesh.CalculatePath(start.position, _destinations[i], NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                {
                    continue;
                }
                var corners = path.corners;
                if (!PointAlong(start.position, corners, EXIT_DISTANCE, out var exit) || !PointAlong(start.position, corners, BEYOND_DISTANCE, out var beyond))
                {
                    continue;
                }
                _exits.Add(new V3(exit.x, exit.y, exit.z));
                _beyond.Add(beyond);
            }
            if (_exits.Count < MIN_ROUTES)
            {
                return false;
            }

            var approaches = GuardFormation.ClusterExits(_exits, new V3(player.x, player.y, player.z), _formation);
            var coveredYaws = new List<float>();
            foreach (var approach in approaches)
            {
                if (posts.Count >= botCount)
                {
                    break;
                }
                var beyondSum = Vector3.zero;
                foreach (var member in approach.Members)
                {
                    beyondSum += _beyond[member];
                }
                var post = ApproachPost(player, new Vector3(approach.Exit.X, approach.Exit.Y, approach.Exit.Z), beyondSum / approach.Members.Count, maxRange, posts);
                if (post != null)
                {
                    posts.Add(post);
                    coveredYaws.Add(approach.Yaw);
                }
            }
            if (posts.Count == 0)
            {
                return false;
            }

            var yaws = GuardFormation.FillGaps(coveredYaws, botCount, lookYaw);
            for (var i = coveredYaws.Count; i < yaws.Count && posts.Count < botCount; i++)
            {
                AddIfFound(posts, BestSpot(player, yaws[i], 45f, Mathf.Min(minRange, 2f), maxRange, null, true, posts, "gap"));
            }
            return true;
        }

        /// <summary>
        /// A post covering one approach: on the player's side of where the routes leave, standing back
        /// from the opening, in the player's space, with a clear view of the opening. Prefers cover and a
        /// wall beside the post (a doorframe or corner). Null when nothing qualifies.
        /// </summary>
        private static GuardPost ApproachPost(Vector3 player, Vector3 exit, Vector3 beyond, float maxRange, List<GuardPost> taken)
        {
            var toExit = exit - player;
            toExit.y = 0f;
            var length = toExit.magnitude;
            if (length < 0.5f)
            {
                return null;
            }
            var forward = toExit / length;
            var right = Vector3.Cross(Vector3.up, forward);
            var standBack = Mathf.Clamp(length - 1.5f, 1.5f, Mathf.Min(5f, maxRange));
            var hard = LayersMaskController.HighPolyWithTerrainMask;
            var bestScore = float.MinValue;
            GuardPost best = null;

            foreach (var along in new[] { standBack, standBack - 1f, standBack + 1f })
            {
                foreach (var sideways in new[] { 0f, 0.8f, -0.8f, 1.5f, -1.5f })
                {
                    if (along < 1f || !NavMesh.SamplePosition(player + forward * along + right * sideways, out var hit, 0.8f, NavMesh.AllAreas))
                    {
                        continue;
                    }
                    var p = hit.position;
                    var tooClose = false;
                    foreach (var other in taken)
                    {
                        tooClose |= (other.Spot - p).sqrMagnitude < MIN_POST_SPACING * MIN_POST_SPACING;
                    }
                    if (tooClose || !C2Nav.IsInPlayersSpace(player, p) || Physics.Linecast(p + Vector3.up * 1.5f, exit + Vector3.up * 1.2f, hard))
                    {
                        continue;
                    }
                    var watch = beyond - p;
                    watch.y = 0f;
                    watch = watch.sqrMagnitude > 0.25f ? watch.normalized : forward;
                    var score = C2Nav.ScoreCover(p, watch, out var covered);
                    var side = Vector3.Cross(Vector3.up, watch);
                    if (Physics.Raycast(p + Vector3.up * 1f, side, 1f, hard) || Physics.Raycast(p + Vector3.up * 1f, -side, 1f, hard))
                    {
                        score += 1f;
                    }
                    score -= Mathf.Abs(sideways) * 0.1f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = new GuardPost { Spot = p, Watch = watch, Covered = covered, Kind = "approach" };
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Collects colliders near the player that can serve as cover, using SAIN's cover-finder rules
        /// (layers Terrain, HighPolyCollider and Foliage; at least 0.75 m tall and 0.25 m wide), minus
        /// anything wider than OBJECT_MAX_WIDTH and anything floating above the player's head (tree
        /// canopies) or well below their feet.
        /// </summary>
        private static void CollectObjects(Vector3 player, float radius)
        {
            _objects.Clear();
            var count = Physics.OverlapSphereNonAlloc(player + Vector3.up, radius, _colliders,
                LayersMaskController.HighPolyWithTerrainNoGrassMask, QueryTriggerInteraction.Collide);
            for (var i = 0; i < count; i++)
            {
                var collider = _colliders[i];
                _colliders[i] = null;
                if (collider == null)
                {
                    continue;
                }
                var bounds = collider.bounds;
                var size = bounds.size;
                if (size.y < OBJECT_MIN_HEIGHT || (size.x < OBJECT_MIN_WIDTH && size.z < OBJECT_MIN_WIDTH)
                    || size.x > OBJECT_MAX_WIDTH || size.z > OBJECT_MAX_WIDTH)
                {
                    continue;
                }
                if (bounds.min.y > player.y + 1f || bounds.max.y < player.y - 1f)
                {
                    continue;
                }
                _objects.Add(collider);
            }
        }

        /// <summary>The point <paramref name="distance"/> m along a path (start, then corners); false when the path is shorter.</summary>
        private static bool PointAlong(Vector3 start, Vector3[] corners, float distance, out Vector3 point)
        {
            var previous = start;
            var travelled = 0f;
            foreach (var corner in corners)
            {
                var segment = Vector3.Distance(previous, corner);
                if (travelled + segment >= distance && segment > 0.001f)
                {
                    point = Vector3.Lerp(previous, corner, (distance - travelled) / segment);
                    return true;
                }
                travelled += segment;
                previous = corner;
            }
            point = default;
            return false;
        }

        /// <summary>
        /// For the diagnostic log only: one bot's post as planned, e.g. "detail post 9 m from you, 45 deg
        /// right of your view, with cover (bush); walk 11 m". Flags a post the bot has no complete navmesh
        /// path to, since the bot then stops short of it. Runs one path query.
        /// </summary>
        /// <param name="player">The player's position when the plan was made.</param>
        /// <param name="look">The player's view direction then.</param>
        /// <param name="botPos">The bot's position.</param>
        /// <param name="post">The bot's post, or null when the plan left it without one.</param>
        internal static string DescribePost(Vector3 player, Vector3 look, Vector3 botPos, GuardPost post)
        {
            if (post == null)
            {
                return "no post (follows you instead)";
            }
            var toPost = post.Spot - player;
            toPost.y = 0f;
            look.y = 0f;
            var angle = look.sqrMagnitude > 0.0001f && toPost.sqrMagnitude > 0.0001f ? Vector3.SignedAngle(look, toPost, Vector3.up) : 0f;
            var path = new NavMeshPath();
            string walk;
            if (!NavMesh.CalculatePath(botPos, post.Spot, NavMesh.AllAreas, path))
            {
                walk = "NO PATH from the bot";
            }
            else if (path.status != NavMeshPathStatus.PathComplete)
            {
                walk = "NO COMPLETE PATH from the bot (it will stop short)";
            }
            else
            {
                walk = $"walk {C2Nav.PathLength(botPos, path.corners):0} m";
            }
            return $"{post.Kind} post {toPost.magnitude:0} m from you, {Mathf.Abs(angle):0} deg {(angle >= 0f ? "right" : "left")} of your view, "
                + $"{(post.Covered ? "with" : "without")} cover ({C2Nav.DescribeCover(post.Spot, post.Watch)}); {walk}";
        }

        /// <summary>Adds a post when one was found.</summary>
        private static void AddIfFound(List<GuardPost> posts, GuardPost post)
        {
            if (post != null)
            {
                posts.Add(post);
            }
        }
    }
}
