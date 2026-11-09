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
//   indoors        -- posts on the approaches (doorways, hallways, stairwells) where navmesh routes
//                     leave the player's space (fork v9: IndoorApproaches), beside each opening with
//                     a view of it; a second guard on the busiest approaches before the minor ones;
//                     spare bots fill the widest gaps. Since fork v9 a lone bot tries this too.
//                     Falls back to the plans below when the room is open or no approach got a post;
//   one bot        -- one post watching the player's back;
//   otherwise      -- the detail: posts evenly spaced around the player (GuardFormation.EvenYaws).
// The indoor plan's diagnostic line is LastIndoorReport, logged by TeamModes.LogPlan.
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
using System.Text;
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

        /// <summary>
        /// Metres of route walked looking for where it leaves the player's space (plus OutRun); a route
        /// still in view after that counts as staying in view. Until fork v8 every route "left" at a fixed
        /// 6 m (EXIT_DISTANCE), and its direction came from the point 10 m along (BEYOND_DISTANCE).
        /// </summary>
        private const float EXIT_SCAN_MAX = 15f;

        /// <summary>Metres past the approach point used to tell which way the approach leads and whether it climbs.</summary>
        private const float BEYOND_PAST_APPROACH = 3f;

        /// <summary>The player's eye height (m) for the line of sight that keeps a route in the player's space.</summary>
        private const float EYE_HEIGHT = 1.4f;

        /// <summary>Height (m) above a route point that the player's line of sight aims at: above most tables and counters.</summary>
        private const float CHEST_HEIGHT = 1.2f;

        /// <summary>Eye height (m) of a crouched bot, for a post's view of its approach (bots crouch at every post since fork v5).</summary>
        private const float CROUCHED_EYE = 1.0f;

        /// <summary>Largest height difference (m) from the player within the player's space; C2Nav.IsInPlayersSpace uses the same.</summary>
        private const float MAX_HEIGHT_FOR_SPACE = 1.5f;

        /// <summary>Farthest (m) each side a route's width is probed.</summary>
        private const float WIDTH_PROBE = 3f;

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
        private static readonly IndoorPlanSettings _indoor = new();
        private static readonly List<Vector3> _samples = new();
        private static readonly List<bool> _inSpace = new();
        private static readonly List<float> _widths = new();
        private static readonly List<float> _exitWidths = new();

        /// <summary>
        /// One line on the last indoor approach plan (routes, approaches, which got guards and why not,
        /// time taken), or null when the last Plan call did not try approaches. Written by Plan; read by
        /// TeamModes.LogPlan for the diagnostic log.
        /// </summary>
        internal static string LastIndoorReport;

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
            LastIndoorReport = null;
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

            if (indoors && TryApproachPosts(player, lookYaw, botCount, minRange, maxRange, posts))
            {
                return posts;
            }
            posts.Clear();

            if (botCount == 1)
            {
                AddIfFound(posts, BestSpot(player, GuardFormation.SoloYaw(lookYaw), 90f, minRange, maxRange, null, indoors, posts, "solo", lineYaw: lookYaw));
                return posts;
            }

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
        /// Indoors (fork v9): traces navmesh routes from the player to points 12-35 m away (other floors
        /// included) and walks each one (WalkRoute) to where it leaves the player's space, moved back to
        /// the narrowest point just before it (IndoorApproaches.ApproachPoint: the doorway). Those points
        /// group into approaches (GuardFormation.ClusterExits), IndoorApproaches.GuardOrder decides which
        /// approaches get guards (a second guard on the busiest ones before the minor ones), and
        /// ApproachPost finds each guard a spot beside the opening with a view of it. Spare bots take the
        /// widest gaps. Returns false (posts untouched) when there are too few routes, the room is open
        /// (most routes stay in view), or no approach got a post; the caller then uses the even detail or
        /// the solo post. Sets LastIndoorReport either way. Runs up to MAX_ROUTES path queries plus
        /// navmesh and physics casts along each route; the report gives the time it took.
        /// </summary>
        private static bool TryApproachPosts(Vector3 player, float lookYaw, int botCount, float minRange, float maxRange, List<GuardPost> posts)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var report = new StringBuilder("indoor approaches: ");
            var result = TraceApproaches(player, lookYaw, botCount, minRange, maxRange, posts, report);
            report.Append($"; {clock.Elapsed.TotalMilliseconds:0} ms");
            LastIndoorReport = report.ToString();
            return result;
        }

        /// <summary>TryApproachPosts' work; writes what it found and decided to <paramref name="report"/>.</summary>
        private static bool TraceApproaches(Vector3 player, float lookYaw, int botCount, float minRange, float maxRange, List<GuardPost> posts, StringBuilder report)
        {
            if (!NavMesh.SamplePosition(player, out var start, 1.5f, NavMesh.AllAreas))
            {
                report.Append("you are off the navmesh, using the even detail");
                return false;
            }
            var vertices = CommandUtils.GetNavMeshVertices();
            if (vertices == null || vertices.Length == 0)
            {
                report.Append("no navmesh triangulation, using the even detail");
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
            _exitWidths.Clear();
            var path = new NavMeshPath();
            var eye = player + Vector3.up * EYE_HEIGHT;
            var stride = Mathf.Max(1, _destinations.Count / MAX_ROUTES);
            int traced = 0, stayed = 0;
            for (var i = 0; i < _destinations.Count && traced < MAX_ROUTES; i += stride)
            {
                if (!NavMesh.CalculatePath(start.position, _destinations[i], NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                {
                    continue;
                }
                traced++;
                var corners = path.corners;
                WalkRoute(start.position, eye, player.y, corners);
                var exit = IndoorApproaches.FindExit(_inSpace, _indoor);
                if (exit < 0)
                {
                    stayed++;
                    continue;
                }
                _widths.Clear();
                var lookBack = Mathf.FloorToInt(_indoor.DoorLookBack / _indoor.SampleStep + 1e-4f);
                for (var s = 0; s < _samples.Count; s++)
                {
                    _widths.Add(s >= 1 && s <= exit && s >= exit - lookBack ? WidthAt(s) : float.NaN);
                }
                var point = IndoorApproaches.ApproachPoint(_widths, exit, _indoor);
                var at = _samples[point];
                if (!PointAlong(start.position, corners, point * _indoor.SampleStep + BEYOND_PAST_APPROACH, out var beyond))
                {
                    beyond = corners[corners.Length - 1];
                }
                _exits.Add(new V3(at.x, at.y, at.z));
                _beyond.Add(beyond);
                _exitWidths.Add(float.IsNaN(_widths[point]) ? WidthAt(point) : _widths[point]);
            }
            var left = _exits.Count;
            report.Append($"{traced} route(s) traced, {left} left your space, {stayed} stayed in view");
            if (traced < MIN_ROUTES || left == 0)
            {
                report.Append(left == 0 && traced >= MIN_ROUTES ? "; no way out found" : "; too few routes").Append(", using the even detail");
                return false;
            }
            if (IndoorApproaches.IsOpenRoom(traced, stayed, _indoor))
            {
                report.Append("; an open room, using the even detail");
                return false;
            }

            var playerV3 = new V3(player.x, player.y, player.z);
            var approaches = GuardFormation.ClusterExits(_exits, playerV3, _formation);
            // The whole ranked list, not just botCount entries, so a bot whose approach offers no spot
            // tries the next one in rank instead of going straight to a gap.
            var order = IndoorApproaches.GuardOrder(approaches, lookYaw, approaches.Count * 2, _indoor);
            var guards = new int[approaches.Count];
            var outcome = new string[approaches.Count];
            var partnerSides = new int[approaches.Count];
            var coveredYaws = new List<float>();
            foreach (var index in order)
            {
                if (posts.Count >= botCount)
                {
                    break;
                }
                var approach = approaches[index];
                Describe(approach, out var point, out var inward, out _);
                var post = ApproachPost(player, point, inward, maxRange, posts, partnerSides[index], out var why);
                if (post == null)
                {
                    outcome[index] ??= why;
                    continue;
                }
                posts.Add(post);
                coveredYaws.Add(approach.Yaw);
                guards[index]++;
                if (partnerSides[index] == 0)
                {
                    partnerSides[index] = IndoorApproaches.SideOf(new V3(post.Spot.x, post.Spot.y, post.Spot.z),
                        new V3(point.x, point.y, point.z), new V3(inward.x, 0f, inward.z));
                    if (partnerSides[index] == 0)
                    {
                        partnerSides[index] = 1;
                    }
                }
            }

            report.Append($"; {approaches.Count} approach(es): ");
            for (var a = 0; a < approaches.Count; a++)
            {
                var approach = approaches[a];
                Describe(approach, out var point, out _, out var kind);
                var flat = point - player;
                flat.y = 0f;
                var turn = GuardFormation.Normalize(approach.Yaw - lookYaw);
                var bearing = turn > 180f ? $"{360f - turn:0} deg left" : $"{turn:0} deg right";
                var status = guards[a] >= 2 ? "guarded twice" : guards[a] == 1 ? "guarded" : outcome[a] != null ? $"unguarded ({outcome[a]})" : "unguarded (no bot left)";
                report.Append(a == 0 ? "" : ", ").Append($"{kind.ToString().ToLowerInvariant()} {approach.Weight} route(s) {flat.magnitude:0} m {bearing} {status}");
            }
            if (posts.Count == 0)
            {
                report.Append("; no post found, using the even detail");
                return false;
            }

            var yaws = GuardFormation.FillGaps(coveredYaws, botCount, lookYaw);
            for (var i = coveredYaws.Count; i < yaws.Count && posts.Count < botCount; i++)
            {
                AddIfFound(posts, BestSpot(player, yaws[i], 45f, Mathf.Min(minRange, 2f), maxRange, null, true, posts, "gap"));
            }
            return true;

            // An approach's averaged point, the direction from it into the player's room (against the way
            // its routes leave), and what it looks like (IndoorApproaches.Classify).
            void Describe(Approach approach, out Vector3 point, out Vector3 inward, out ApproachKind kind)
            {
                point = new Vector3(approach.Exit.X, approach.Exit.Y, approach.Exit.Z);
                var beyondSum = Vector3.zero;
                var widthSum = 0f;
                foreach (var member in approach.Members)
                {
                    beyondSum += _beyond[member];
                    widthSum += _exitWidths[member];
                }
                var beyondAvg = beyondSum / approach.Members.Count;
                inward = point - beyondAvg;
                inward.y = 0f;
                if (inward.sqrMagnitude < 0.01f)
                {
                    inward = player - point;
                    inward.y = 0f;
                }
                inward = inward.sqrMagnitude > 0.0001f ? inward.normalized : Vector3.forward;
                kind = IndoorApproaches.Classify(widthSum / approach.Members.Count, beyondAvg.y - point.y, _indoor);
            }
        }

        /// <summary>
        /// Samples a route every IndoorPlanSettings.SampleStep m from <paramref name="start"/> into
        /// _samples, with whether each point is in the player's space into _inSpace: within
        /// MAX_HEIGHT_FOR_SPACE m of the player's height, and either a straight navmesh line from the
        /// player or an unobstructed line of sight from the player's eyes at chest height (so a table or
        /// a pillar does not end the room). Stops once the route has stayed out for OutRun m, or after
        /// EXIT_SCAN_MAX m plus OutRun.
        /// </summary>
        private static void WalkRoute(Vector3 start, Vector3 eye, float playerY, Vector3[] corners)
        {
            _samples.Clear();
            _inSpace.Clear();
            var hard = LayersMaskController.HighPolyWithTerrainMask;
            var step = _indoor.SampleStep;
            var needed = Mathf.Max(1, Mathf.CeilToInt(_indoor.OutRun / step - 1e-4f));
            var limit = EXIT_SCAN_MAX + _indoor.OutRun;
            var outCount = 0;
            var previous = start;
            var travelled = 0f;
            var nextAt = 0f;
            _samples.Add(start);
            _inSpace.Add(true);
            nextAt += step;
            foreach (var corner in corners)
            {
                var segment = Vector3.Distance(previous, corner);
                while (segment > 0.001f && nextAt <= travelled + segment && nextAt <= limit)
                {
                    var p = Vector3.Lerp(previous, corner, (nextAt - travelled) / segment);
                    var inside = Mathf.Abs(p.y - playerY) <= MAX_HEIGHT_FOR_SPACE
                        && (!NavMesh.Raycast(start, p, out _, NavMesh.AllAreas) || !Physics.Linecast(eye, p + Vector3.up * CHEST_HEIGHT, hard));
                    _samples.Add(p);
                    _inSpace.Add(inside);
                    outCount = inside ? 0 : outCount + 1;
                    if (outCount >= needed)
                    {
                        return;
                    }
                    nextAt += step;
                }
                travelled += segment;
                previous = corner;
                if (nextAt > limit)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Free width (m) across the route at sample <paramref name="index"/> of _samples: hard geometry
        /// to the left plus to the right at 1 m height, each capped at WIDTH_PROBE m (so open floor
        /// measures 2 x WIDTH_PROBE). Two raycasts.
        /// </summary>
        private static float WidthAt(int index)
        {
            var before = _samples[Mathf.Max(0, index - 1)];
            var after = _samples[Mathf.Min(_samples.Count - 1, index + 1)];
            var along = after - before;
            along.y = 0f;
            if (along.sqrMagnitude < 0.0001f)
            {
                return WIDTH_PROBE * 2f;
            }
            var side = Vector3.Cross(Vector3.up, along.normalized);
            var origin = _samples[index] + Vector3.up;
            var hard = LayersMaskController.HighPolyWithTerrainMask;
            var right = Physics.Raycast(origin, side, out var r, WIDTH_PROBE, hard) ? r.distance : WIDTH_PROBE;
            var leftSide = Physics.Raycast(origin, -side, out var l, WIDTH_PROBE, hard) ? l.distance : WIDTH_PROBE;
            return right + leftSide;
        }

        /// <summary>
        /// A post watching one approach (fork v9): candidates on rings 2-5 m from the approach point, up
        /// to 75 degrees either side of the way into the player's room, each needing to be on the
        /// navmesh on the player's floor, within <paramref name="maxRange"/> m of the player, clear of
        /// the other posts, in the player's space and with a view of the approach at crouched eye
        /// height. IndoorApproaches.ScorePost ranks the survivors (cover, a wall beside, an angle off
        /// the axis, the other side from a first guard). The post watches the approach point. Null when
        /// nothing qualifies; <paramref name="why"/> then says where the candidates fell out.
        /// </summary>
        /// <param name="point">The approach point (the doorway, or where the routes leave).</param>
        /// <param name="inward">Horizontal unit direction from the approach into the player's room.</param>
        /// <param name="partnerSide">IndoorApproaches.SideOf the first guard's post on this approach, or 0.</param>
        private static GuardPost ApproachPost(Vector3 player, Vector3 point, Vector3 inward, float maxRange, List<GuardPost> taken, int partnerSide, out string why)
        {
            var hard = LayersMaskController.HighPolyWithTerrainMask;
            var pointV3 = new V3(point.x, point.y, point.z);
            var inwardV3 = new V3(inward.x, 0f, inward.z);
            var playerV3 = new V3(player.x, player.y, player.z);
            var bestScore = float.NegativeInfinity;
            GuardPost best = null;
            int tries = 0, offMesh = 0, tooFar = 0, crowded = 0, outside = 0, noView = 0, badAngle = 0;

            foreach (var distance in PostRings)
            {
                foreach (var angle in PostAngles)
                {
                    tries++;
                    var raw = point + Quaternion.Euler(0f, angle, 0f) * inward * distance;
                    if (!NavMesh.SamplePosition(raw, out var hit, 0.6f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - player.y) > MAX_HEIGHT_FOR_SPACE)
                    {
                        offMesh++;
                        continue;
                    }
                    var p = hit.position;
                    var fromPlayer = p - player;
                    fromPlayer.y = 0f;
                    if (fromPlayer.magnitude > maxRange)
                    {
                        tooFar++;
                        continue;
                    }
                    var tooClose = false;
                    foreach (var other in taken)
                    {
                        tooClose |= (other.Spot - p).sqrMagnitude < MIN_POST_SPACING * MIN_POST_SPACING;
                    }
                    if (tooClose)
                    {
                        crowded++;
                        continue;
                    }
                    if (!C2Nav.IsInPlayersSpace(player, p))
                    {
                        outside++;
                        continue;
                    }
                    if (Physics.Linecast(p + Vector3.up * CROUCHED_EYE, point + Vector3.up * CROUCHED_EYE, hard))
                    {
                        noView++;
                        continue;
                    }
                    var watch = point - p;
                    watch.y = 0f;
                    watch = watch.sqrMagnitude > 0.01f ? watch.normalized : -inward;
                    var cover = C2Nav.ScoreCover(p, watch, out var covered);
                    var side = Vector3.Cross(Vector3.up, watch);
                    var wall = Physics.Raycast(p + Vector3.up, side, 1f, hard) || Physics.Raycast(p + Vector3.up, -side, 1f, hard);
                    var candidate = new PostCandidate { Spot = new V3(p.x, p.y, p.z), Cover = cover, WallBeside = wall, SightClear = true };
                    var score = IndoorApproaches.ScorePost(candidate, pointV3, inwardV3, playerV3, partnerSide, _indoor) - Random.value * 0.05f;
                    if (float.IsNegativeInfinity(score))
                    {
                        badAngle++;
                        continue;
                    }
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = new GuardPost { Spot = p, Watch = watch, Covered = covered, Kind = "approach", SectorYaw = GuardFormation.YawOf(fromPlayer.x, fromPlayer.z) };
                    }
                }
            }
            why = best != null ? null
                : $"no spot: {tries} tries, {offMesh} off the navmesh or on another floor, {tooFar} too far from you, {crowded} beside another post, "
                    + $"{outside} outside your space, {noView} with no view of it, {badAngle} too close, too far or in your way";
            return best;
        }

        /// <summary>Distances (m) from an approach point at which ApproachPost tries spots.</summary>
        private static readonly float[] PostRings = { 2f, 3f, 4f, 5f };

        /// <summary>Degrees either side of the way into the player's room at which ApproachPost tries spots.</summary>
        private static readonly float[] PostAngles = { 0f, 20f, -20f, 40f, -40f, 60f, -60f, 75f, -75f };

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
