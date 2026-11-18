// ThermalPole.cs -- one carry bot's thermal marker as Unity objects (fork v14): a small fixed set of hexagonal boxes,
// one per hot section of the pole (Core/PoleDesigns.cs), that ECOTI's thermal cameras draw hot.
//
// Made and owned by C2Sain/StrobeMarkers.cs (one per living carry bot, main thread only), which fills Spans each
// frame with the design's hot sections, calls Layout to put the boxes where the bot's head is, and calls Show in the
// pre-cull step of every camera (shown only for ECOTI's thermal cameras). ECOTI's heat shader draws a renderer hot
// when its per-renderer "_CotiHeatTemp" (Kelvin) is more than 1 degree above the air, and anything colder black;
// the boxes carry that value in a property block set once (SetHeat) and are simply left out (disabled) for a
// section that is not hot, because a cold box would draw nothing but black. Each box uses a copy of a skin
// renderer's material with its RenderType set to Opaque, which the heat shader needs to draw it at all (fork research
// 2026-11-18, miyako-carry-service/evidence/2026-11-18-coti-heat-research).
// Reads and writes no files. Destroy removes every Unity object it made.

using MiyakoCarryService.Client.C2Sain.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>One bot's thermal marker: <see cref="PoleSpans.MAX"/> boxes that follow the design's hot sections.</summary>
    internal sealed class ThermalPole
    {
        private static readonly int _heatKelvinId = Shader.PropertyToID("_CotiHeatTemp");
        private static readonly int _heatCelsiusId = Shader.PropertyToID("_HeatTemp");

        private readonly GameObject _root;
        private readonly Material _material;
        private readonly Transform[] _boxes = new Transform[PoleSpans.MAX];
        private readonly MeshRenderer[] _renderers = new MeshRenderer[PoleSpans.MAX];
        private readonly bool[] _active = new bool[PoleSpans.MAX];
        private readonly bool[] _enabled = new bool[PoleSpans.MAX];
        private readonly MaterialPropertyBlock _block = new();
        private float _heat = float.NaN;

        /// <summary>The hot sections for this frame; the owner fills it (PoleDesigns.Fill) before calling <see cref="Layout"/>.</summary>
        public readonly PoleSpans Spans = new();

        /// <summary>
        /// Builds the boxes, all switched off. <paramref name="layer"/> is a layer ECOTI's thermal camera draws (a skin
        /// renderer's own), <paramref name="source"/> a material an ECOTI-drawn renderer uses (it is copied), and
        /// <paramref name="mesh"/> a 1 m wide, 1 m tall mesh centred on its middle (scaled per section).
        /// </summary>
        public ThermalPole(string name, int layer, Material source, Mesh mesh)
        {
            _material = new Material(source) { name = name, renderQueue = 2000 };
            _material.SetOverrideTag("RenderType", "Opaque");
            _root = new GameObject(name) { layer = layer };
            for (var i = 0; i < PoleSpans.MAX; i++)
            {
                var box = new GameObject(name + " " + i) { layer = layer };
                box.transform.SetParent(_root.transform, false);
                box.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = box.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = _material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                renderer.enabled = false;
                _boxes[i] = box.transform;
                _renderers[i] = renderer;
            }
        }

        /// <summary>
        /// Gives every box the heat ECOTI will draw (Celsius; it writes the Kelvin value the shader reads and EFT's own
        /// Celsius fallback). Does nothing when the value is unchanged.
        /// </summary>
        public void SetHeat(float celsius)
        {
            if (celsius == _heat)
            {
                return;
            }
            _heat = celsius;
            _block.SetFloat(_heatKelvinId, celsius + 273.15f);
            _block.SetFloat(_heatCelsiusId, celsius);
            for (var i = 0; i < PoleSpans.MAX; i++)
            {
                _renderers[i].SetPropertyBlock(_block);
            }
        }

        /// <summary>
        /// Puts the boxes where <see cref="Spans"/> says: the pole stands vertically from <paramref name="foot"/> for
        /// <paramref name="length"/> metres, each hot section is a box of <paramref name="width"/> metres across times
        /// its width scale. Boxes without a section are marked not wanted. Does not switch any renderer (see <see cref="Show"/>).
        /// </summary>
        public void Layout(Vector3 foot, float length, float width)
        {
            for (var i = 0; i < PoleSpans.MAX; i++)
            {
                if (i >= Spans.Count)
                {
                    _active[i] = false;
                    continue;
                }
                var lo = Spans.Lo[i];
                var hi = Spans.Hi[i];
                var across = width * (float)Spans.WidthScale[i];
                _boxes[i].position = foot + Vector3.up * ((float)(lo + hi) * 0.5f * length);
                _boxes[i].localScale = new Vector3(across, (float)(hi - lo) * length, across);
                _active[i] = true;
            }
        }

        /// <summary>
        /// Switches the boxes for the camera about to draw: with <paramref name="shown"/> each box that has a hot
        /// section is drawn, otherwise none is. Only touches a renderer whose state changes.
        /// </summary>
        public void Show(bool shown)
        {
            for (var i = 0; i < PoleSpans.MAX; i++)
            {
                var want = shown && _active[i];
                if (_enabled[i] != want)
                {
                    _enabled[i] = want;
                    _renderers[i].enabled = want;
                }
            }
        }

        /// <summary>Destroys the boxes and the material copy.</summary>
        public void Destroy()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
            }
            if (_material != null)
            {
                Object.Destroy(_material);
            }
        }
    }
}
