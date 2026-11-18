// StrobeMarkers.cs -- the two markers that tell carry bots from enemies through ECOTI and night vision (fork v12;
// v13 sat them on the helmet, made the IR light small and quick, and keeps it off while ECOTI's thermal is on;
// v14 made the thermal marker a tall pole with a choice of designs, and the IR strobe brighter and white).
//
// Why: through ECOTI's thermal clip-on a carry bot is a hot body like any enemy; ECOTI reads no side, group or
// team. The fork, which makes the teammates, adds two markers to each carry bot, and each view shows only its
// own (rules and timings: Core/StrobeRules.cs and Core/PoleDesigns.cs; research and decisions:
// miyako-carry-service/IR-STROBE-PLAN.md and evidence/2026-11-18-coti-heat-research):
//   - the thermal marker: a tall thin pole standing on the helmet whose hot sections move in a pattern (the F12
//     "thermal design": barber pole by default), drawn as a few small boxes (ThermalPole). ECOTI draws the world
//     with a replacement heat shader (Coti/HeatOnly) that reads a per-renderer "_CotiHeatTemp" (Kelvin); anything
//     above the air temperature is hot, anything else is black, so a section that is not hot is simply not drawn.
//     The boxes are switched on only in the pre-cull step of ECOTI's thermal cameras (names starting "Coti"), so
//     the naked eye, night vision and every other camera never draw them. One design, Box, is build 13's small box
//     on the helmet.
//   - the IR strobe: a small white point light of the fork's own (red in build 13). Build 12 used EFT's IkLight on
//     it, but BorkelRNVG multiplies an IkLight's range and brightness for IR flashlights (2 and 1.5 on the user's
//     install), so the light reached about 12 m and lit the scene like a lamp. The fork switches the light
//     itself, with its own brightness, reach, colour and height: on only while night vision is on, the blink
//     pattern is in a flash (a quick double flash by default) and ECOTI's thermal view is not up on the goggles
//     (EcotiProbe: a thermal view cannot see an IR light).
// Both are children of nothing: Update (once a frame, from GameLoop.Update) and the first pre-cull of each
// frame put them at the head bone's position (the pole's foot and the box's centre MarkerHeight above it, the
// light IrHeight above it), so a crouching or prone bot carries them with it.
//
// Owner of the markers (one pair per living carry bot, keyed by profile id), of the subscription to
// Camera.onPreCull, of the camera-name cache and of the EcotiProbe; main thread only. The settings are the F12
// "Markers: ..." ones (ForkConfig), read live; Sync puts the IR light's brightness, reach and colour on the existing
// lights within a quarter of a second of a change. Clear (BrainMgr.OnRaidEnded) destroys everything and
// unsubscribes. Any exception turns the markers off for the rest of the raid and is logged once; they cannot
// change what a bot does. With "Debug: diagnostic log" on, each marker made and each change of ECOTI's thermal
// view is logged. Reads and writes no files.

using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using MiyakoCarryService.Client.C2Sain.Core;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>The carry bots' thermal marker and IR strobe; see the file header.</summary>
    internal static class StrobeMarkers
    {
        private static readonly StrobeSettings _settings = new();
        private static readonly Dictionary<string, Marker> _markers = new();
        private static readonly Dictionary<int, bool> _thermalCameras = new();
        private static readonly HashSet<string> _alive = new();
        private static readonly List<string> _gone = new();
        private static readonly EcotiProbe _ecoti = new();
        private static Mesh _prism;
        private static bool _subscribed;
        private static bool _faulted;
        private static bool _thermalViewOn;
        private static bool _ecotiLogged;
        private static int _positionedFrame = -1;
        private static int _madeCount;
        private static float _nextSync;
        private static float _nextEcotiPoll;

        /// <summary>Seconds between looks at which carry bots are alive (the list is built fresh each time, so not every frame).</summary>
        private const float SYNC_INTERVAL = 0.25f;

        /// <summary>Seconds between reads of ECOTI's thermal state: a read by reflection boxes a value, so not every frame.</summary>
        private const float ECOTI_POLL_INTERVAL = 0.1f;

        /// <summary>Metres above the player's feet that count as the eye, for the pole's width at range (a standing eye is about 1.6 m up).</summary>
        private const float EYE_HEIGHT = 1.6f;

        private static McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        /// <summary>One carry bot's pair of markers.</summary>
        private sealed class Marker
        {
            public Player Bot;
            public string Name;
            public Transform Head;
            public int Index;
            public double Phase;
            public ThermalPole Pole;
            public GameObject IrObject;
            public Light IrLight;
            public bool HasPole;
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
                _settings.ThermalPeriod = ForkConfig.StrobeThermalPeriod?.Value ?? 1f;
                _settings.HideIrUnderThermal = ForkConfig.IrHideUnderEcoti?.Value != false;
                var pattern = ForkConfig.StrobeIrPattern?.Value ?? IrPattern.DoubleFlash;
                var nightVision = NightVisionOn();
                var thermalView = wantIr && _settings.HideIrUnderThermal && EcotiThermalOn();
                var now = Time.time;
                foreach (var marker in _markers.Values)
                {
                    if (marker.IrLight != null)
                    {
                        var lit = StrobeRules.IrLit(pattern, now + marker.Phase, _settings);
                        var on = StrobeRules.IrLightOn(wantIr, lit, nightVision, thermalView, _settings);
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
            _nextEcotiPoll = 0f;
            _thermalViewOn = false;
            _ecotiLogged = false;
            _ecoti.Reset();
            _faulted = false;
        }

        /// <summary>
        /// Whether ECOTI's thermal view is up on the player's goggles, read through <see cref="EcotiProbe"/> a few times
        /// a second. With "Debug: diagnostic log" on it logs where the state is read from (once a raid) and each change.
        /// </summary>
        private static bool EcotiThermalOn()
        {
            var now = Time.unscaledTime;
            if (now < _nextEcotiPoll)
            {
                return _thermalViewOn;
            }
            _nextEcotiPoll = now + ECOTI_POLL_INTERVAL;
            var on = _ecoti.GoggleThermalOn();
            var log = ForkConfig.DebugLog?.Value == true;
            if (!_ecotiLogged && log)
            {
                _ecotiLogged = true;
                McsLogger.LogInfo($"[{ForkLog.Tag}] markers: ECOTI thermal state: {_ecoti.Status}");
            }
            if (on != _thermalViewOn)
            {
                _thermalViewOn = on;
                if (log)
                {
                    McsLogger.LogInfo($"[{ForkLog.Tag}] markers: ECOTI thermal view {(on ? "on" : "off")}; the IR strobe is {(on ? "hidden" : "shown again")} (night vision {(NightVisionOn() ? "on" : "off")})");
                }
            }
            return _thermalViewOn;
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
        /// Runs before every camera culls: moves the markers to where the bots' heads are and lays out the thermal
        /// pole's hot sections for this moment (the first time in a frame), and switches each pole on only for
        /// ECOTI's thermal cameras.
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
                foreach (var marker in _markers.Values)
                {
                    marker.Pole?.Show(show);
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

        /// <summary>
        /// Puts both markers of every bot where its head is, whatever way the head tilts, and works out the pole's hot
        /// sections for this moment. The pole's foot (the Box design's centre) is MarkerHeight above the head bone
        /// (default 0.18 m: a helmet's top is about 0.2 m above the bone, estimated from the user's build 12
        /// screenshots, so the foot sinks a little into the helmet and the pole stands on it); the IR light is
        /// IrHeight above it (default 0.32 m, a little over the helmet, so it shines down on the helmet and shoulders;
        /// build 13 had it at the pole's height and the user lowered that to 0.18 m, inside the helmet, where a point
        /// light lights hardly anything).
        /// </summary>
        private static void Position()
        {
            var design = ForkConfig.ThermalDesign?.Value ?? ThermalDesign.BarberPole;
            var height = ForkConfig.MarkerHeight?.Value ?? 0.18f;
            var irHeight = ForkConfig.IrHeight?.Value ?? 0.32f;
            var poleLength = ForkConfig.PoleLength?.Value ?? 1f;
            var poleWidth = ForkConfig.PoleWidth?.Value ?? 0.08f;
            var widen = ForkConfig.PoleWidenAtRange?.Value != false;
            var boxSize = ForkConfig.BeaconSize?.Value ?? 0.15f;
            var hasEye = TryGetEye(out var eye);
            var now = Time.time;
            foreach (var marker in _markers.Values)
            {
                if (marker.Head == null)
                {
                    continue;
                }
                var head = marker.Head.position;
                if (marker.Pole != null)
                {
                    PoleDesigns.Fill(design, now + marker.Phase, _settings, marker.Pole.Spans);
                    if (design == ThermalDesign.Box)
                    {
                        var length = boxSize * 1.1f;
                        marker.Pole.Layout(head + Vector3.up * (height - length * 0.5f), length, boxSize);
                    }
                    else
                    {
                        var distance = hasEye ? Vector3.Distance(eye, head) : 0f;
                        var width = (float)PoleGeometry.Width(poleWidth, distance, widen, _settings);
                        marker.Pole.Layout(head + Vector3.up * height, poleLength, width);
                    }
                }
                if (marker.IrObject != null)
                {
                    marker.IrObject.transform.position = head + Vector3.up * irHeight;
                }
            }
        }

        /// <summary>The local player's eye position (for the pole's width at range); false when there is no local player.</summary>
        private static bool TryGetEye(out Vector3 eye)
        {
            var player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
            if (player == null)
            {
                eye = Vector3.zero;
                return false;
            }
            eye = player.Position + Vector3.up * EYE_HEIGHT;
            return true;
        }

        /// <summary>The IR light's colour from its setting.</summary>
        private static Color IrColorValue()
        {
            IrColors.Rgb(ForkConfig.IrLightColor?.Value ?? IrColor.White, out var r, out var g, out var b);
            return new Color(r, g, b);
        }

        /// <summary>
        /// Makes markers for carry bots that have none, destroys those whose bot is gone, remakes a bot's markers when a
        /// marker was switched on or off in the settings, and puts the IR light's brightness, reach and colour and the
        /// pole's heat on the markers that exist.
        /// </summary>
        private static void Sync(bool wantThermal, bool wantIr)
        {
            _alive.Clear();
            var intensity = ForkConfig.IrBrightness?.Value ?? 12f;
            var range = ForkConfig.IrRadius?.Value ?? 1f;
            var color = IrColorValue();
            var heat = ForkConfig.StrobeThermalHeat?.Value ?? 65f;
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
                    if (marker.HasPole != wantThermal || marker.HasIr != wantIr)
                    {
                        Destroy(marker);
                        _markers.Remove(id);
                    }
                    else
                    {
                        marker.Pole?.SetHeat(heat);
                        if (marker.IrLight != null)
                        {
                            if (marker.IrLight.intensity != intensity)
                            {
                                marker.IrLight.intensity = intensity;
                            }
                            if (marker.IrLight.range != range)
                            {
                                marker.IrLight.range = range;
                            }
                            if (marker.IrLight.color != color)
                            {
                                marker.IrLight.color = color;
                            }
                        }
                        index++;
                        continue;
                    }
                }
                var made = Make(bot, index, intensity, range, color, heat, wantThermal, wantIr);
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
        /// until it has spawned; tried again on later frames). The pole's boxes copy a skin renderer's layer and
        /// material so ECOTI's camera draws them; their RenderType is set to Opaque, which the heat shader needs.
        /// </summary>
        private static Marker Make(Player bot, int index, float intensity, float range, Color color, float heat, bool wantThermal, bool wantIr)
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

            var marker = new Marker { Bot = bot, Name = bot.Profile?.Nickname ?? bot.ProfileId, Head = head, Index = index };
            var period = _settings.DoubleFlashPeriod;
            marker.Phase = StrobeRules.PhaseOffset(index, period);
            var layer = skin.gameObject.layer;

            if (wantThermal)
            {
                marker.Pole = new ThermalPole("McsForkThermalPole", layer, skin.sharedMaterial, Prism());
                marker.Pole.SetHeat(heat);
                marker.HasPole = true;
            }

            if (wantIr)
            {
                // A plain light, off until Update lights it for a flash. Build 12 gave it EFT's IkLight, which
                // BorkelRNVG scales (range x2, brightness x1.5 on the user's install); this one is as set. ForcePixel
                // keeps Unity from demoting it to a vertex light when other lights are near the bot.
                marker.IrObject = new GameObject("McsForkIrStrobe") { layer = layer };
                var light = marker.IrObject.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = color;
                light.intensity = intensity;
                light.range = range;
                light.shadows = LightShadows.None;
                light.renderMode = LightRenderMode.ForcePixel;
                light.enabled = false;
                marker.IrLight = light;
                marker.HasIr = true;
            }

            _madeCount++;
            if (ForkConfig.DebugLog?.Value == true)
            {
                var design = ForkConfig.ThermalDesign?.Value ?? ThermalDesign.BarberPole;
                var poleLength = design == ThermalDesign.Box ? (ForkConfig.BeaconSize?.Value ?? 0.15f) * 1.1f : ForkConfig.PoleLength?.Value ?? 1f;
                McsLogger.LogInfo($"[{ForkLog.Tag}] markers: made for {marker.Name} (number {_madeCount}): "
                    + (wantThermal ? $"thermal {design} {poleLength:0.##} m tall on layer {layer}, skin shader {skin.sharedMaterial.shader?.name}" : "no thermal marker")
                    + (wantIr ? $", IR strobe (brightness {intensity}, reach {range} m, {(ForkConfig.IrLightColor?.Value ?? IrColor.White)}, {ForkConfig.IrHeight?.Value ?? 0.32f:0.##} m up)" : ", no IR strobe"));
            }
            return marker;
        }

        /// <summary>A hexagonal prism, 1 wide and 1 tall, centred on its middle: the shape of every box of the thermal marker (scaled per section).</summary>
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
            marker.Pole?.Destroy();
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
