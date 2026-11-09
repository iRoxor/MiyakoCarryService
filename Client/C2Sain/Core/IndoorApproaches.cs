// IndoorApproaches.cs -- the Unity-free half of the indoor guard planner (fork v9): where a navmesh
// route leaves the player's room, which narrow point it leaves through (a doorway, hallway or
// stairwell), which approaches the bots guard and in what order, and how good a spot is for
// watching one.
//
// Used by GuardPlanner.TryApproachPosts (C2Sain/GuardPlanner.cs), which walks each route with navmesh
// and physics queries and hands the results here. Unity-free so the logic harness
// (.for-claude-to-reference/harnesses/mcs_fork_logic) can check it with scripted samples. Reads and
// writes no files.
//
// Why it exists: up to fork v8 an approach was the point 6 m along each route. Indoors that point
// usually lies past the doorway, in the next room or down the hallway, so routes through one door
// that then turned different ways counted as different approaches. Posts were then searched on the
// straight line from the player toward that point, through walls. In the build 6-8 raid logs
// (2026-11-05 to 11-09: evidence\2026-11-05-build-6-raid, 2026-11-08-build-7-raids and
// 2026-11-09-build-8-lighthouse-2) indoor guard plans left a bot without a post 157 times
// (71 + 67 + 19), the lone bot got no post in 41 of 65 indoor plans (build 7), and 105 of 173
// approach posts were bare (builds 7-8).
//
// Yaws follow GuardFormation: degrees clockwise from +Z seen from above, in [0, 360).

using System;
using System.Collections.Generic;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>What an approach looks like where it leaves the player's room.</summary>
    public enum ApproachKind
    {
        /// <summary>A narrow opening: a door or a gap about one person wide.</summary>
        Doorway,

        /// <summary>A corridor-wide opening.</summary>
        Hallway,

        /// <summary>The route climbs or drops a floor just past it.</summary>
        Stairs,

        /// <summary>A wide opening or a corner the route turns.</summary>
        Opening,
    }

    /// <summary>Tunables for <see cref="IndoorApproaches"/>; the plugin uses the defaults.</summary>
    public sealed class IndoorPlanSettings
    {
        /// <summary>Metres between the points sampled along a route.</summary>
        public float SampleStep = 0.75f;

        /// <summary>
        /// Metres a route must stay out of the player's space before it counts as having left. Shorter
        /// dips (a route skirting a table or a pillar) are ignored. A route that ends out of the
        /// player's space has left even when the dip is shorter.
        /// </summary>
        public float OutRun = 2f;

        /// <summary>Metres back from where a route leaves that are searched for its narrowest point.</summary>
        public float DoorLookBack = 4f;

        /// <summary>Openings this wide (m) or narrower are doorways; the approach moves to the narrowest one.</summary>
        public float DoorwayMaxWidth = 1.8f;

        /// <summary>Openings this wide (m) or narrower, and wider than a doorway, are hallways.</summary>
        public float HallwayMaxWidth = 3f;

        /// <summary>A route rising or dropping this far (m) just past its approach makes it a stairwell.</summary>
        public float StairsHeight = 1.2f;

        /// <summary>
        /// Share of traced routes that must stay in view for the room to count as open; the planner then
        /// uses the even detail instead of approaches (a hall, a warehouse floor).
        /// </summary>
        public float OpenShare = 0.6f;

        /// <summary>Approaches within this many degrees of where the player looks are the player's own to watch.</summary>
        public float ViewHalfAngle = 45f;

        /// <summary>Weight multiplier for an approach the player is already looking at.</summary>
        public float ViewDiscount = 0.5f;

        /// <summary>
        /// Share of the routes that left the room an approach needs before a bot is given to it ahead of
        /// a second bot on a busier one. The busiest approach always qualifies.
        /// </summary>
        public float MinApproachShare = 0.15f;

        /// <summary>True: spare bots guard the busiest approaches a second time, from the other side.</summary>
        public bool DoubleUp = true;

        /// <summary>Nearest (m) a post may be to the approach it watches.</summary>
        public float PostMinDistance = 1.5f;

        /// <summary>Distance (m) from the approach that scores best.</summary>
        public float PostBestDistance = 3.5f;

        /// <summary>Farthest (m) a post may be from the approach it watches.</summary>
        public float PostMaxDistance = 6f;

        /// <summary>Metres a post must stand back from the opening, on the player's side of it.</summary>
        public float StandBack = 0.75f;

        /// <summary>
        /// Degrees off the approach's axis that score best: a bot watching a doorway from beside it, not
        /// square in front, sees whoever comes through before they see it.
        /// </summary>
        public float BestOffsetMin = 20f;
        public float BestOffsetMax = 55f;

        /// <summary>A post's view of its approach must pass at least this far (m) from the player.</summary>
        public float PlayerClearance = 0.6f;
    }

    /// <summary>What GuardPlanner measured at one candidate post, for <see cref="IndoorApproaches.ScorePost"/>.</summary>
    public struct PostCandidate
    {
        /// <summary>The spot (on the navmesh).</summary>
        public V3 Spot;

        /// <summary>C2Nav.ScoreCover toward the approach: -1 (a wall in the face) to 3.5.</summary>
        public float Cover;

        /// <summary>A wall within 1 m to either side (a doorframe or a corner to stand against).</summary>
        public bool WallBeside;

        /// <summary>Nothing solid between the spot and the approach at crouched eye height.</summary>
        public bool SightClear;
    }

    /// <summary>Indoor approach geometry; see the file header.</summary>
    public static class IndoorApproaches
    {
        /// <summary>
        /// Where a route leaves the player's space. <paramref name="inSpace"/> holds one flag per
        /// sample, <see cref="IndoorPlanSettings.SampleStep"/> apart, sample 0 at the player. Returns the
        /// index of the last sample in the player's space before the route stays out of it for
        /// <see cref="IndoorPlanSettings.OutRun"/> m (or until the route ends), or -1 when it never does.
        /// </summary>
        public static int FindExit(IReadOnlyList<bool> inSpace, IndoorPlanSettings settings)
        {
            var needed = Math.Max(1, (int)Math.Ceiling(settings.OutRun / Math.Max(0.01f, settings.SampleStep) - 1e-4));
            var lastIn = -1;
            var outCount = 0;
            for (var i = 0; i < inSpace.Count; i++)
            {
                if (inSpace[i])
                {
                    lastIn = i;
                    outCount = 0;
                    continue;
                }
                outCount++;
                if (lastIn >= 0 && outCount >= needed)
                {
                    return lastIn;
                }
            }
            return lastIn >= 0 && outCount > 0 ? lastIn : -1;
        }

        /// <summary>
        /// The approach point of a route that leaves at <paramref name="exit"/>: the narrowest sample
        /// from the exit back <see cref="IndoorPlanSettings.DoorLookBack"/> m (never sample 0, the
        /// player), when it is a doorway's width or narrower; the exit itself otherwise. Ties go to the
        /// sample nearer the exit. NaN widths (not measured) are skipped.
        /// </summary>
        public static int ApproachPoint(IReadOnlyList<float> widths, int exit, IndoorPlanSettings settings)
        {
            if (exit <= 0)
            {
                return exit;
            }
            var back = (int)Math.Floor(settings.DoorLookBack / Math.Max(0.01f, settings.SampleStep) + 1e-4);
            var best = -1;
            var bestWidth = float.MaxValue;
            for (var i = exit; i >= Math.Max(1, exit - back); i--)
            {
                var w = i < widths.Count ? widths[i] : float.NaN;
                if (float.IsNaN(w) || w >= bestWidth)
                {
                    continue;
                }
                bestWidth = w;
                best = i;
            }
            return best >= 0 && bestWidth <= settings.DoorwayMaxWidth ? best : exit;
        }

        /// <summary>What an approach looks like, from its width (m) and how far the route climbs or drops just past it (m).</summary>
        public static ApproachKind Classify(float width, float heightChange, IndoorPlanSettings settings)
        {
            if (Math.Abs(heightChange) >= settings.StairsHeight)
            {
                return ApproachKind.Stairs;
            }
            if (width <= settings.DoorwayMaxWidth)
            {
                return ApproachKind.Doorway;
            }
            return width <= settings.HallwayMaxWidth ? ApproachKind.Hallway : ApproachKind.Opening;
        }

        /// <summary>True when so many routes stay in view that the room is open ground (see <see cref="IndoorPlanSettings.OpenShare"/>).</summary>
        public static bool IsOpenRoom(int routes, int stayedInView, IndoorPlanSettings settings)
        {
            return routes > 0 && stayedInView >= settings.OpenShare * routes;
        }

        /// <summary>
        /// Which approach each guard takes, in order: an index into <paramref name="approaches"/> per
        /// post, at most <paramref name="botCount"/> of them. Approaches are ranked by weight, halved
        /// (ViewDiscount) for one the player already looks at; ties keep input order. Every approach
        /// with at least MinApproachShare of the routes (and always the top one) gets a guard first;
        /// with DoubleUp, the spare bots then take the ranked approaches again, then the minor ones.
        /// An index appearing twice means a second guard on that approach.
        /// </summary>
        public static List<int> GuardOrder(IReadOnlyList<Approach> approaches, float lookYaw, int botCount, IndoorPlanSettings settings)
        {
            var result = new List<int>();
            if (approaches.Count == 0 || botCount <= 0)
            {
                return result;
            }
            var total = 0;
            foreach (var a in approaches)
            {
                total += a.Weight;
            }
            var rank = new List<int>();
            for (var i = 0; i < approaches.Count; i++)
            {
                rank.Add(i);
            }
            float Ranked(int i) => approaches[i].Weight
                * (GuardFormation.AngleBetween(approaches[i].Yaw, lookYaw) <= settings.ViewHalfAngle ? settings.ViewDiscount : 1f);
            rank.Sort((a, b) => Ranked(a) != Ranked(b) ? Ranked(b).CompareTo(Ranked(a)) : a.CompareTo(b));

            var major = new List<int>();
            var minor = new List<int>();
            foreach (var i in rank)
            {
                (major.Count == 0 || approaches[i].Weight >= settings.MinApproachShare * total ? major : minor).Add(i);
            }
            foreach (var i in major)
            {
                if (result.Count < botCount)
                {
                    result.Add(i);
                }
            }
            if (settings.DoubleUp)
            {
                foreach (var i in major)
                {
                    if (result.Count < botCount)
                    {
                        result.Add(i);
                    }
                }
            }
            foreach (var i in minor)
            {
                if (result.Count < botCount)
                {
                    result.Add(i);
                }
            }
            return result;
        }

        /// <summary>
        /// Which side of an approach's axis a spot lies on, seen from above: +1 one side, -1 the other,
        /// 0 within 0.25 m of the axis. <paramref name="inward"/> is the horizontal unit direction from
        /// the approach into the player's room.
        /// </summary>
        public static int SideOf(V3 spot, V3 approach, V3 inward)
        {
            var cross = inward.Z * (spot.X - approach.X) - inward.X * (spot.Z - approach.Z);
            return cross > 0.25f ? 1 : cross < -0.25f ? -1 : 0;
        }

        /// <summary>
        /// Score of a spot for watching an approach; higher is better, negative infinity rejects it.
        /// Rejected: no clear sight of the approach, closer than PostMinDistance or farther than
        /// PostMaxDistance from it, less than StandBack m on the player's side of the opening, or a
        /// sight line that passes within PlayerClearance m of the player. Scored: the cover score, +1
        /// for a wall beside the spot, up to +0.75 for standing BestOffsetMin-BestOffsetMax degrees off
        /// the axis, -0.25 per metre from PostBestDistance, and for a second guard on the approach +1.5
        /// on the side opposite the first guard's (<paramref name="partnerSide"/>, 0 for none) or -1 on
        /// the same side.
        /// </summary>
        /// <param name="c">What was measured at the spot.</param>
        /// <param name="approach">The approach point.</param>
        /// <param name="inward">Horizontal unit direction from the approach into the player's room.</param>
        /// <param name="player">The player's position.</param>
        /// <param name="partnerSide">SideOf the first guard's post on this approach, or 0.</param>
        public static float ScorePost(PostCandidate c, V3 approach, V3 inward, V3 player, int partnerSide, IndoorPlanSettings settings)
        {
            if (!c.SightClear)
            {
                return float.NegativeInfinity;
            }
            var dx = c.Spot.X - approach.X;
            var dz = c.Spot.Z - approach.Z;
            var distance = (float)Math.Sqrt(dx * dx + dz * dz);
            if (distance < settings.PostMinDistance || distance > settings.PostMaxDistance)
            {
                return float.NegativeInfinity;
            }
            var depth = dx * inward.X + dz * inward.Z;
            if (depth < settings.StandBack)
            {
                return float.NegativeInfinity;
            }
            if (Blocks(player, c.Spot, approach, settings.PlayerClearance))
            {
                return float.NegativeInfinity;
            }

            var offset = (float)(Math.Acos(Math.Max(-1.0, Math.Min(1.0, depth / distance))) * 180.0 / Math.PI);
            float offsetBonus;
            if (offset < settings.BestOffsetMin)
            {
                offsetBonus = 0.75f * offset / Math.Max(1f, settings.BestOffsetMin);
            }
            else if (offset <= settings.BestOffsetMax)
            {
                offsetBonus = 0.75f;
            }
            else
            {
                offsetBonus = 0.75f * Math.Max(0f, (90f - offset) / Math.Max(1f, 90f - settings.BestOffsetMax));
            }

            var score = c.Cover + (c.WallBeside ? 1f : 0f) + offsetBonus - Math.Abs(distance - settings.PostBestDistance) * 0.25f;
            if (partnerSide != 0)
            {
                var side = SideOf(c.Spot, approach, inward);
                score += side == -partnerSide ? 1.5f : side == partnerSide ? -1f : 0f;
            }
            return score;
        }

        /// <summary>True when the player stands between a spot and the approach it watches, within <paramref name="clearance"/> m of the sight line (horizontal).</summary>
        public static bool Blocks(V3 player, V3 spot, V3 approach, float clearance)
        {
            var abx = approach.X - spot.X;
            var abz = approach.Z - spot.Z;
            var lengthSq = abx * abx + abz * abz;
            if (lengthSq < 1e-6f)
            {
                return false;
            }
            var t = ((player.X - spot.X) * abx + (player.Z - spot.Z) * abz) / lengthSq;
            if (t <= 0.05f || t >= 0.95f)
            {
                return false;
            }
            var px = spot.X + abx * t - player.X;
            var pz = spot.Z + abz * t - player.Z;
            return px * px + pz * pz < clearance * clearance;
        }
    }
}
