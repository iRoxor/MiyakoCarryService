// GuardFormation.cs -- the geometry of a guard detail that needs no Unity: where the posts point,
// which bot takes which post, and how navmesh routes leaving the player's surroundings group into
// approaches (doorways, hallways, stairwells).
//
// Used by GuardPlanner (C2Sain/GuardPlanner.cs), which TeamModes calls when a squad's guard plan is
// (re)made. Unity-free so the logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can
// check it with scripted points. Reads and writes no files.
//
// Yaws are degrees clockwise from +Z seen from above, in [0, 360), the same convention as Unity's
// Quaternion.Euler(0, yaw, 0) * Vector3.forward.

using System;
using System.Collections.Generic;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Tunables for <see cref="GuardFormation"/>; the plugin uses the defaults.</summary>
    public sealed class GuardFormationSettings
    {
        /// <summary>Metres within which route exits count as one approach.</summary>
        public float ExitClusterRadius = 2.5f;
    }

    /// <summary>One way into the player's surroundings: where routes leave, and how many do.</summary>
    public sealed class Approach
    {
        /// <summary>Centre of the exits grouped into this approach.</summary>
        public V3 Exit;

        /// <summary>Number of sampled routes that leave through it; higher means a busier way in.</summary>
        public int Weight;

        /// <summary>Yaw from the player to <see cref="Exit"/>.</summary>
        public float Yaw;

        /// <summary>Indices (into the exits passed to ClusterExits) of the routes that leave through it.</summary>
        public readonly List<int> Members = new();
    }

    /// <summary>Guard-detail geometry; see the file header.</summary>
    public static class GuardFormation
    {
        /// <summary>
        /// Post offsets (degrees) around the direction of a contact report, by the bot's place in the
        /// squad: the squad fans out across the front facing the mark (unchanged from fork v2).
        /// </summary>
        public static readonly float[] FanOffsets = { -35f, 35f, -70f, 70f, 0f, -105f, 105f };

        /// <summary>
        /// Yaws of <paramref name="count"/> evenly spaced posts around the player, offset half a spacing
        /// from where the player looks so no post stands in the player's line of fire: two bots take the
        /// flanks, three a triangle with one behind, four the diagonals.
        /// </summary>
        public static float[] EvenYaws(int count, float lookYaw)
        {
            if (count <= 0)
            {
                return Array.Empty<float>();
            }
            var yaws = new float[count];
            var step = 360f / count;
            for (var i = 0; i < count; i++)
            {
                yaws[i] = Normalize(lookYaw + step * 0.5f + step * i);
            }
            return yaws;
        }

        /// <summary>Yaw a lone guard watches: the player's back.</summary>
        public static float SoloYaw(float lookYaw)
        {
            return Normalize(lookYaw + 180f);
        }

        /// <summary>Yaws of <paramref name="count"/> posts fanned out facing a contact report.</summary>
        public static float[] FanYaws(int count, float threatYaw)
        {
            var yaws = new float[Math.Max(0, count)];
            for (var i = 0; i < yaws.Length; i++)
            {
                yaws[i] = Normalize(threatYaw + FanOffsets[i % FanOffsets.Length]);
            }
            return yaws;
        }

        /// <summary>Yaw of the horizontal direction (dx, dz).</summary>
        public static float YawOf(float dx, float dz)
        {
            return Normalize((float)(Math.Atan2(dx, dz) * 180.0 / Math.PI));
        }

        /// <summary>Unsigned angle between two yaws, 0-180 degrees.</summary>
        public static float AngleBetween(float a, float b)
        {
            var d = Math.Abs(Normalize(a) - Normalize(b));
            return d > 180f ? 360f - d : d;
        }

        /// <summary>A yaw wrapped into [0, 360).</summary>
        public static float Normalize(float yaw)
        {
            var r = yaw % 360f;
            return r < 0f ? r + 360f : r;
        }

        /// <summary>
        /// Groups route exits into approaches: each exit joins the first group whose first exit lies
        /// within the cluster radius (horizontally), or starts a new group. Returns the groups busiest
        /// first (ties keep input order), each with its centre, weight, yaw from the player and members.
        /// </summary>
        /// <param name="exits">Where each sampled route leaves the player's surroundings.</param>
        /// <param name="player">The player's position, for the yaws.</param>
        /// <param name="settings">Supplies ExitClusterRadius.</param>
        public static List<Approach> ClusterExits(IReadOnlyList<V3> exits, V3 player, GuardFormationSettings settings)
        {
            var seeds = new List<V3>();
            var groups = new List<Approach>();
            var radiusSq = settings.ExitClusterRadius * settings.ExitClusterRadius;
            for (var i = 0; i < exits.Count; i++)
            {
                var e = exits[i];
                var joined = false;
                for (var g = 0; g < groups.Count; g++)
                {
                    var dx = e.X - seeds[g].X;
                    var dz = e.Z - seeds[g].Z;
                    if (dx * dx + dz * dz <= radiusSq)
                    {
                        groups[g].Members.Add(i);
                        joined = true;
                        break;
                    }
                }
                if (!joined)
                {
                    seeds.Add(e);
                    var approach = new Approach();
                    approach.Members.Add(i);
                    groups.Add(approach);
                }
            }

            foreach (var group in groups)
            {
                float x = 0f, y = 0f, z = 0f;
                foreach (var m in group.Members)
                {
                    x += exits[m].X;
                    y += exits[m].Y;
                    z += exits[m].Z;
                }
                var n = group.Members.Count;
                group.Exit = new V3(x / n, y / n, z / n);
                group.Weight = n;
                group.Yaw = YawOf(group.Exit.X - player.X, group.Exit.Z - player.Z);
            }

            // Stable sort: busiest first, input order among equals.
            var order = new List<int>();
            for (var i = 0; i < groups.Count; i++)
            {
                order.Add(i);
            }
            order.Sort((a, b) => groups[a].Weight != groups[b].Weight ? groups[b].Weight.CompareTo(groups[a].Weight) : a.CompareTo(b));
            var sorted = new List<Approach>(groups.Count);
            foreach (var i in order)
            {
                sorted.Add(groups[i]);
            }
            return sorted;
        }

        /// <summary>
        /// Adds yaws in the middle of the widest angular gaps between the given yaws until there are
        /// <paramref name="count"/>, for posts left over once every approach has a guard. With no yaws
        /// to start from, the first new one is the player's back.
        /// </summary>
        public static List<float> FillGaps(IReadOnlyList<float> taken, int count, float lookYaw)
        {
            var yaws = new List<float>();
            foreach (var y in taken)
            {
                yaws.Add(Normalize(y));
            }
            if (yaws.Count == 0 && count > 0)
            {
                yaws.Add(SoloYaw(lookYaw));
            }
            while (yaws.Count < count)
            {
                var sorted = new List<float>(yaws);
                sorted.Sort();
                var bestGap = -1f;
                var bestMid = 0f;
                for (var i = 0; i < sorted.Count; i++)
                {
                    var from = sorted[i];
                    var to = i + 1 < sorted.Count ? sorted[i + 1] : sorted[0] + 360f;
                    var gap = to - from;
                    if (gap > bestGap)
                    {
                        bestGap = gap;
                        bestMid = Normalize(from + gap * 0.5f);
                    }
                }
                yaws.Add(bestMid);
            }
            return yaws;
        }

        /// <summary>
        /// Assigns bots to posts so the total straight-line walk is smallest: exact for up to seven bots,
        /// greedy (nearest pair first) beyond. Returns each bot's post index, or -1 for bots left over
        /// when there are fewer posts than bots.
        /// </summary>
        public static int[] Assign(IReadOnlyList<V3> bots, IReadOnlyList<V3> posts)
        {
            var result = new int[bots.Count];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = -1;
            }
            if (bots.Count == 0 || posts.Count == 0)
            {
                return result;
            }
            if (bots.Count <= 7 && posts.Count <= 7)
            {
                var best = float.MaxValue;
                var current = new int[bots.Count];
                var used = new bool[posts.Count];
                Search(0, 0f, posts.Count);
                return result;

                // Every bot takes a free post, except that a bot may go without one while more bots
                // remain than free posts, so the bots left over are the ones it costs least to leave out.
                void Search(int bot, float cost, int freePosts)
                {
                    if (cost >= best)
                    {
                        return;
                    }
                    if (bot == bots.Count)
                    {
                        best = cost;
                        Array.Copy(current, result, current.Length);
                        return;
                    }
                    for (var p = 0; p < posts.Count; p++)
                    {
                        if (used[p])
                        {
                            continue;
                        }
                        used[p] = true;
                        current[bot] = p;
                        Search(bot + 1, cost + V3.Distance(bots[bot], posts[p]), freePosts - 1);
                        used[p] = false;
                    }
                    if (bots.Count - bot > freePosts)
                    {
                        current[bot] = -1;
                        Search(bot + 1, cost, freePosts);
                    }
                }
            }

            var takenPost = new bool[posts.Count];
            var placed = new bool[bots.Count];
            for (var round = 0; round < Math.Min(bots.Count, posts.Count); round++)
            {
                var bestDistance = float.MaxValue;
                int bestBot = -1, bestPost = -1;
                for (var b = 0; b < bots.Count; b++)
                {
                    if (placed[b])
                    {
                        continue;
                    }
                    for (var p = 0; p < posts.Count; p++)
                    {
                        if (takenPost[p])
                        {
                            continue;
                        }
                        var d = V3.Distance(bots[b], posts[p]);
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            bestBot = b;
                            bestPost = p;
                        }
                    }
                }
                placed[bestBot] = true;
                takenPost[bestPost] = true;
                result[bestBot] = bestPost;
            }
            return result;
        }
    }
}
