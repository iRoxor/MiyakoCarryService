// SainFoliageSightPatch.cs -- lets carry bots see and shoot out of the bush they are hiding in.
//
// Registered by MiyakoCarryServicePlugin (the fork's patch list). A Harmony prefix
// on SAIN 4.5.1's EnemyPartDataClass.SetLineOfSight, which SAIN's VisionRaycastJob
// calls on the main thread with the result of each eye-to-body-part and
// muzzle-to-body-part raycast, about 30 times a second for every bot and enemy.
// Reads the fork's see-out radius setting; reads and writes no files.
//
// Why: all three of SAIN's checks treat bushes as solid -- line of sight uses
// HighPolyWithTerrainNoGrassMask (Terrain, HighPolyCollider, Foliage), vision uses
// the game's AI mask (Foliage, Grass) and shooting uses HighPolyWithTerrainMaskAI
// (all four) -- so a carry bot crouched in a bush could neither see nor shoot.
// Rule: when the first thing a ray hits is foliage or grass within the see-out
// radius of the ray's start, and the ray starts at a carry bot's eyes or muzzle,
// the ray is cast again from just past that foliage (repeatedly, while the next hit
// is still foliage inside the radius). The hit SAIN records is the first real
// obstruction after that, or none. Rays from every other bot are untouched, so
// enemies still cannot see the carry bot through the same bush.
//
// The ray's start is not passed to SetLineOfSight; it is recovered from the hit:
// the ray runs from its start through the hit point toward castPoint, and the hit
// lies raycastHit.distance from the start.

using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Utils;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SPT.Reflection.Patching;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain.Patches
{
    /// <summary>See the file header.</summary>
    public sealed class SainFoliageSightPatch : ModulePatch
    {
        /// <summary>Most foliage hits skipped on one ray before the remaining foliage counts as blocking.</summary>
        private const int MAX_SKIPS = 4;

        /// <summary>Horizontal metres between a ray's start and a carry bot that still count as "from that bot".</summary>
        private const float OWNER_RADIUS = 0.75f;

        /// <summary>Positions of the living carry bots, refreshed at most once a frame when a candidate ray needs them.</summary>
        private static readonly List<Vector3> _carryBotPositions = new();
        private static int _positionsFrame = -1;
        private static bool _loggedError;

        private static McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        /// <summary>Targets SAIN's per-body-part line-of-sight setter.</summary>
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(EnemyPartDataClass), nameof(EnemyPartDataClass.SetLineOfSight));
        }

        /// <summary>
        /// Replaces a near-foliage hit on a carry bot's ray with what the ray hits beyond the foliage.
        /// Never skips SAIN's method; on any error the hit is left unchanged.
        /// </summary>
        [PatchPrefix]
        public static void Prefix(Vector3 castPoint, ref RaycastHit raycastHit, ERaycastCheck type)
        {
            try
            {
                var radius = ForkConfig.FoliageSightRadius?.Value ?? 0f;
                var distance = raycastHit.distance;
                if (radius <= 0f || distance <= 0f || distance > radius)
                {
                    return;
                }
                var collider = raycastHit.collider;
                if (collider == null || !IsFoliage(collider.gameObject.layer))
                {
                    return;
                }

                var toTarget = castPoint - raycastHit.point;
                var remaining = toTarget.magnitude;
                if (remaining < 0.01f)
                {
                    return;
                }
                var direction = toTarget / remaining;
                var origin = raycastHit.point - direction * distance;
                if (!StartsAtCarryBot(origin))
                {
                    return;
                }

                var mask = MaskFor(type);
                var start = raycastHit.point + direction * 0.05f;
                var travelled = distance + 0.05f;
                for (var i = 0; i < MAX_SKIPS; i++)
                {
                    var length = Vector3.Distance(start, castPoint) + 0.05f;
                    if (!Physics.Raycast(start, direction, out var hit, length, mask))
                    {
                        raycastHit = default;
                        return;
                    }
                    if (IsFoliage(hit.collider.gameObject.layer) && travelled + hit.distance <= radius)
                    {
                        start = hit.point + direction * 0.05f;
                        travelled += hit.distance + 0.05f;
                        continue;
                    }
                    hit.distance += travelled;
                    raycastHit = hit;
                    return;
                }
            }
            catch (System.Exception e)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    McsLogger.LogError(e);
                }
            }
        }

        /// <summary>True for the game's Foliage and Grass layers.</summary>
        private static bool IsFoliage(int layer)
        {
            var bit = 1 << layer;
            return (bit & (LayersMaskController.Foliage | LayersMaskController.Grass)) != 0;
        }

        /// <summary>The layer mask SAIN's job used for this kind of check (VisionRaycastJob, SAIN 4.5.1).</summary>
        private static int MaskFor(ERaycastCheck type)
        {
            return type switch
            {
                ERaycastCheck.Vision => LayersMaskController.AI,
                ERaycastCheck.Shoot => LayersMaskController.HighPolyWithTerrainMaskAI,
                _ => LayersMaskController.HighPolyWithTerrainNoGrassMask,
            };
        }

        /// <summary>
        /// True when the point is at a living carry bot's eyes or muzzle: within OWNER_RADIUS of it
        /// horizontally and 0.3-2.0 m above its feet.
        /// </summary>
        private static bool StartsAtCarryBot(Vector3 origin)
        {
            if (_positionsFrame != Time.frameCount)
            {
                _positionsFrame = Time.frameCount;
                _carryBotPositions.Clear();
                foreach (var player in McsMgr.GetAllAliveMcsBotPlayer())
                {
                    if (player != null)
                    {
                        _carryBotPositions.Add(player.Position);
                    }
                }
            }
            for (var i = 0; i < _carryBotPositions.Count; i++)
            {
                var feet = _carryBotPositions[i];
                var dx = origin.x - feet.x;
                var dz = origin.z - feet.z;
                var dy = origin.y - feet.y;
                if (dx * dx + dz * dz <= OWNER_RADIUS * OWNER_RADIUS && dy >= 0.3f && dy <= 2.0f)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
