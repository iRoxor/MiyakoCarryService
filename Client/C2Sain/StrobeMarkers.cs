// StrobeMarkers.cs -- the two markers that tell carry bots from enemies through ECOTI and night vision (fork v12).
//
// Why: through ECOTI's thermal clip-on a carry bot is a hot body like any enemy; ECOTI reads no side, group or
// team. The fork, which makes the teammates, adds two small markers over each carry bot's head, and each view
// shows only its own (rules and timings: Core/StrobeRules.cs; research and decisions:
// miyako-carry-service/IR-STROBE-PLAN.md and evidence/2026-11-18-coti-heat-research):
//   - the thermal ID beacon: a small solid hexagonal prism with its own heat value. ECOTI draws the world with
//     a replacement heat shader (Coti/HeatOnly) that reads a per-renderer "_CotiHeatTemp" (Kelvin); anything
//     above the air temperature is hot, anything else is black. The beacon's heat pulses between hot and cold
//     (StrobeRules.ThermalCelsius) as if a heated element turned inside it. Its renderer is switched on only
//     in the pre-cull step of ECOTI's thermal cameras (names starting "Coti"), so the naked eye, night vision
//     and every other camera never draw it.
//   - the IR strobe: a small red point light with EFT's own IkLight on it, so the game switches it off unless
//     night vision is on (and BorkelRNVG scales it like the game's other IR lights). Its Light.enabled follows
//     the blink pattern (double flash by default).
// Both are children of nothing: Update (once a frame, from GameLoop.Update) and the first pre-cull of each
// frame put them a little above the head bone's position, so a crouching or prone bot carries them with it.
//
// Owner of the markers (one pair per living carry bot, keyed by profile id), of the subscription to
// Camera.onPreCull and of the camera-name cache; main thread only. The settings are the F12 "Markers: ..." ones
// (ForkConfig), read live; the IR light's brightness and range are applied when a marker is made, because
// IkLight remembers them, so changing one remakes the markers. Clear (BrainMgr.OnRaidEnded) destroys
// everything and unsubscribes. Any exception turns the markers off for the rest of the raid and is logged once;
// they cannot change what a bot does. With "Debug: diagnostic log" on, each marker made is logged. Reads and
// writes no files.

using System.Collections.Generic;
using EFT;
using EFT.CameraControl;
using EFT.Visual;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>The carry bots' thermal beacon and IR strobe; see the file header.</summary>
    internal static class StrobeMarkers
    {
        private static readonly StrobeSettings _settings = new();
        private static readonly Dictionary<string, Marker> _markers = new();
        private static readonly Dictionary<int, bool> _thermalCameras = new();
        private static readonly HashSet<string> _alive = new();
        private static readonly List<string> _gone = new();
        private static readonly int _heatKelvinId = Shader.PropertyToID("_CotiHeatTemp");
        private static readonly int _heatCelsiusId = Shader.PropertyToID("_HeatTemp");
        private static Mesh _prism;
        private static bool _subscribed;
        private static bool _faulted;
        private static int _positionedFrame = -1;
        private static int _madeCount;
        private static float _nextSync;

        /// <summary>Seconds between looks at which carry bots are alive (the list is built fresh each time, so not every frame).</summary>
        private const float SYNC_INTERVAL = 0.25f;

        private static McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        /// <summary>One carry bot's pair of markers.</summary>
        private sealed class Marker
        {
            public Player Bot;
            public string Name;
            public Transform Head;
            public int Index;
            public double Phase;
            public GameObject BeaconObject;
            public MeshRenderer Beacon;
            public MaterialPropertyBlock Block;
            public bool BeaconOn;
            public GameObject IrObject;
            public Light IrLight;
            public float AppliedIntensity;
            public float AppliedRange;
            public bool HasBeacon;
            public bool HasIr;
            public bool IrOn;
        }

        /// <summary>
        /// Once a frame (GameLoop.Update): follows the squad (makes markers for new carry bots, destroys those of
        /// bots that died or left), puts the IR strobe's light in step with its pattern and night vision, and
        /// subscribes to Camera.onPreCull. Times itself under Timing: carry bot markers.
        /// </summary>
        internal static void Update()
        {
            if (_faulted)
            {
                return;
            }
            using var timing = ForkCost.Begin(CostKind.Strobe);
            try
            {
                var wantThermal = ForkConfig.StrobeThermal?.Value == true;
                var wantIr = ForkConfig.StrobeIr?.Value == true;
                if (!wantThermal && !wantIr)
                {
                    if (_markers.Count > 0)
                    {
                        DestroyAll();
                    }
                    return;
                }
                EnsureSubscribed();
                if (Time.time >= _nextSync)
                {
                    _nextSync = Time.time + SYNC_INTERVAL;
                    Sync(wantThermal, wantIr);
                }
                if (_markers.Count == 0)
                {
                    return;
                }
                _settings.ThermalPeriod = ForkConfig.StrobeThermalPeriod?.Value ?? 2f;
                _settings.HotCelsius = ForkConfig.StrobeThermalHeat?.Value ?? 65f;
                var pattern = ForkConfig.StrobeIrPattern?.Value ?? IrPattern.DoubleFlash;
                var nightVision = NightVisionOn();
                var now = Time.time;
                foreach (var marker in _markers.Values)
                {
                    if (marker.IrLight != null)
                    {
                        var lit = StrobeRules.IrLit(pattern, now + marker.Phase, _settings);
                        var on = StrobeRules.IrLightOn(wantIr, lit, nightVision);
                        if (marker.IrOn != on)
                        {
                            marker.IrOn = on;
                            marker.IrLight.enabled = on;
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Fault(e);
            }
        }

        /// <summary>Destroys every marker and unsubscribes; the raid is over. Also lets a faulted marker system try again next raid.</summary>
        internal static void Clear()
        {
            try
            {
                DestroyAll();
                if (_subscribed)
                {
                    Camera.onPreCull -= OnPreCull;
                    _subscribed = false;
                }
            }
            catch (System.Exception e)
            {
                McsLogger.LogError(e);
            }
            _thermalCameras.Clear();
            _positionedFrame = -1;
            _nextSync = 0f;
            _faulted = false;
        }

        /// <summary>Makes sure the pre-cull handler is subscribed.</summary>
        private static void EnsureSubscribed()
        {
            if (!_subscribed)
            {
                Camera.onPreCull += OnPreCull;
                _subscribed = true;
            }
        }

        /// <summary>
        /// Runs before every camera culls: moves the markers to where the bots' heads are (the first time in a
        /// frame) and switches each beacon on only for ECOTI's thermal cameras, with its heat for this moment.
        /// </summary>
        private static void OnPreCull(Camera camera)
        {
            if (_faulted || _markers.Count == 0 || camera == null)
            {
                return;
            }
            using var timing = ForkCost.Begin(CostKind.Strobe);
            try
            {
                var frame = Time.frameCount;
                if (_positionedFrame != frame)
                {
                    _positionedFrame = frame;
                    Position();
                }
                var thermal = IsThermalCamera(camera);
                var show = StrobeRules.BeaconShows(thermal, ForkConfig.StrobeThermal?.Value == true);
                var now = Time.time;
                foreach (var marker in _markers.Values)
                {
                    if (marker.Beacon == null)
                    {
                        continue;
                    }
                    if (show)
                    {
                        var celsius = (float)StrobeRules.ThermalCelsius(now + marker.Phase, _settings);
                        marker.Block.SetFloat(_heatKelvinId, celsius + 273.15f);
                        marker.Block.SetFloat(_heatCelsiusId, celsius);
                        marker.Beacon.SetPropertyBlock(marker.Block);
                    }
                    if (marker.BeaconOn != show)
                    {
                        marker.BeaconOn = show;
                        marker.Beacon.enabled = show;
                    }
                }
            }
            catch (System.Exception e)
            {
                Fault(e);
            }
        }

        /// <summary>Whether a camera is one of ECOTI's thermal cameras; the answer is remembered per camera.</summary>
        private static bool IsThermalCamera(Camera camera)
        {
            var id = camera.GetInstanceID();
            if (!_thermalCameras.TryGetValue(id, out var thermal))
            {
                thermal = StrobeRules.IsThermalCameraName(camera.name);
                _thermalCameras[id] = thermal;
            }
            return thermal;
        }

        /// <summary>Puts both markers of every bot a little above its head bone, whatever way the head tilts.</summary>
        private static void Position()
        {
            var height = ForkConfig.StrobeHeight?.Value ?? 0.4f;
            var size = ForkConfig.StrobeSize?.Value ?? 0.22f;
            foreach (var marker in _markers.Values)
            {
                if (marker.Head == null)
                {
                    continue;
                }
                var at = marker.Head.position + Vector3.up * height;
                if (marker.BeaconObject != null)
                {
                    marker.BeaconObject.transform.position = at;
                    marker.BeaconObject.transform.localScale = new Vector3(size, size * 1.1f, size);
                }
                if (marker.IrObject != null)
                {
                    marker.IrObject.transform.position = at;
                }
            }
        }

        /// <summary>
        /// Makes markers for carry bots that have none, destroys those whose bot is gone, and remakes a bot's markers
        /// when a marker was switched on or off in the settings or the IR light's brightness or range changed.
        /// </summary>
        private static void Sync(bool wantThermal, bool wantIr)
        {
            _alive.Clear();
            var intensity = ForkConfig.StrobeIrIntensity?.Value ?? 1.5f;
            var range = ForkConfig.StrobeIrRange?.Value ?? 6f;
            var index = 0;
            foreach (var bot in McsMgr.GetAllAliveMcsBotPlayer())
            {
                if (bot == null)
                {
                    continue;
                }
                var id = bot.ProfileId;
                _alive.Add(id);
                if (_markers.TryGetValue(id, out var marker))
                {
                    if (marker.HasBeacon != wantThermal || marker.HasIr != wantIr
                        || (marker.HasIr && (marker.AppliedIntensity != intensity || marker.AppliedRange != range)))
                    {
                        Destroy(marker);
                        _markers.Remove(id);
                    }
                    else
                    {
                        index++;
                        continue;
                    }
                }
                var made = Make(bot, index, intensity, range, wantThermal, wantIr);
                if (made != null)
                {
                    _markers[id] = made;
                }
                index++;
            }
            _gone.Clear();
            foreach (var pair in _markers)
            {
                if (!_alive.Contains(pair.Key) || pair.Value.Bot == null)
                {
                    _gone.Add(pair.Key);
                }
            }
            foreach (var id in _gone)
            {
                Destroy(_markers[id]);
                _markers.Remove(id);
            }
        }

        /// <summary>
        /// Makes one bot's markers, or returns null when the bot is not ready yet (no head bone or skin renderer
        /// until it has spawned; tried again on later frames). The beacon copies a skin renderer's layer and
        /// material so ECOTI's camera draws it; its RenderType is set to Opaque, which the heat shader needs.
        /// </summary>
        private static Marker Make(Player bot, int index, float intensity, float range, bool wantThermal, bool wantIr)
        {
            var head = bot.PlayerBones?.Head?.Original;
            Renderer skin = null;
            var skins = bot.PlayerBody?.BodySkins;
            if (skins != null)
            {
                foreach (var part in skins.Values)
                {
                    var renderers = part?.GetRenderers();
                    if (renderers == null)
                    {
                        continue;
                    }
                    foreach (var renderer in renderers)
                    {
                        if (renderer != null && renderer.sharedMaterial != null)
                        {
                            skin = renderer;
                            break;
                        }
                    }
                    if (skin != null)
                    {
                        break;
                    }
                }
            }
            if (head == null || skin == null)
            {
                return null;
            }

            var marker = new Marker { Bot = bot, Name = bot.Profile?.Nickname ?? bot.ProfileId, Head = head, Index = index, Block = new MaterialPropertyBlock() };
            var period = _settings.DoubleFlashPeriod;
            marker.Phase = StrobeRules.PhaseOffset(index, period);
            var layer = skin.gameObject.layer;

            if (wantThermal)
            {
                marker.BeaconObject = new GameObject("McsForkThermalBeacon") { layer = layer };
                marker.BeaconObject.AddComponent<MeshFilter>().sharedMesh = Prism();
                var beaconRenderer = marker.BeaconObject.AddComponent<MeshRenderer>();
                var material = new Material(skin.sharedMaterial) { name = "McsForkThermalBeacon", renderQueue = 2000 };
                material.SetOverrideTag("RenderType", "Opaque");
                beaconRenderer.sharedMaterial = material;
                beaconRenderer.shadowCastingMode = ShadowCastingMode.Off;
                beaconRenderer.receiveShadows = false;
                beaconRenderer.lightProbeUsage = LightProbeUsage.Off;
                beaconRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                beaconRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                beaconRenderer.enabled = false;
                marker.Beacon = beaconRenderer;
                marker.HasBeacon = true;
            }

            if (wantIr)
            {
                // The light is made inactive, given its IkLight and wired to it, then switched on: IkLight reads
                // the light's brightness once, in Awake, and BorkelRNVG scales it there.
                marker.IrObject = new GameObject("McsForkIrStrobe") { layer = layer };
                marker.IrObject.SetActive(false);
                var light = marker.IrObject.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = Color.red;
                light.intensity = intensity;
                light.range = range;
                light.shadows = LightShadows.None;
                light.enabled = false;
                var ik = marker.IrObject.AddComponent<IkLight>();
                ik.Light = light;
                marker.IrObject.SetActive(true);
                marker.IrLight = light;
                marker.HasIr = true;
                marker.AppliedIntensity = intensity;
                marker.AppliedRange = range;
            }

            _madeCount++;
            if (ForkConfig.DebugLog?.Value == true)
            {
                McsLogger.LogInfo($"[{ForkLog.Tag}] markers: made for {marker.Name} (number {_madeCount}): "
                    + (wantThermal ? $"thermal beacon on layer {layer}, skin shader {skin.sharedMaterial.shader?.name}" : "no thermal beacon")
                    + (wantIr ? $", IR strobe (brightness {intensity}, range {range} m)" : ", no IR strobe"));
            }
            return marker;
        }

        /// <summary>A hexagonal prism, 1 wide and 1 tall, centred on its middle: the beacon's shape (scaled by the size setting).</summary>
        private static Mesh Prism()
        {
            if (_prism != null)
            {
                return _prism;
            }
            var vertices = new Vector3[14];
            for (var i = 0; i < 6; i++)
            {
                var angle = i * Mathf.PI / 3f;
                var x = Mathf.Cos(angle) * 0.5f;
                var z = Mathf.Sin(angle) * 0.5f;
                vertices[i] = new Vector3(x, -0.5f, z);
                vertices[6 + i] = new Vector3(x, 0.5f, z);
            }
            vertices[12] = new Vector3(0f, -0.5f, 0f);
            vertices[13] = new Vector3(0f, 0.5f, 0f);
            var triangles = new List<int>();
            for (var i = 0; i < 6; i++)
            {
                var next = (i + 1) % 6;
                triangles.AddRange(new[] { i, 6 + i, 6 + next, i, 6 + next, next });
                triangles.AddRange(new[] { 12, next, i });
                triangles.AddRange(new[] { 13, 6 + i, 6 + next });
            }
            _prism = new Mesh { name = "McsForkPrism" };
            _prism.vertices = vertices;
            _prism.triangles = triangles.ToArray();
            _prism.RecalculateNormals();
            _prism.RecalculateBounds();
            return _prism;
        }

        /// <summary>True while night vision is on for the player's own view or scope.</summary>
        private static bool NightVisionOn()
        {
            var manager = CameraManager.Instance;
            if (manager == null)
            {
                return false;
            }
            return (manager.NightVision != null && manager.NightVision.On)
                || (manager.OpticCameraManager?.NightVision != null && manager.OpticCameraManager.NightVision.On);
        }

        private static void DestroyAll()
        {
            foreach (var marker in _markers.Values)
            {
                Destroy(marker);
            }
            _markers.Clear();
        }

        private static void Destroy(Marker marker)
        {
            if (marker.BeaconObject != null)
            {
                Object.Destroy(marker.BeaconObject);
            }
            if (marker.IrObject != null)
            {
                Object.Destroy(marker.IrObject);
            }
        }

        /// <summary>An exception turns the markers off for the rest of the raid and is logged once.</summary>
        private static void Fault(System.Exception e)
        {
            _faulted = true;
            McsLogger.LogError(e);
            try
            {
                DestroyAll();
            }
            catch
            {
                // Nothing more to do: the markers are off.
            }
            McsLogger.LogInfo($"[{ForkLog.Tag}] markers: switched off for this raid after an error");
        }
    }
}
