// PoleDesigns.cs -- the thermal marker's shapes (fork v14): where along a tall pole the hot sections are at a moment
// in time, the pole's width at a distance, and the IR light's colours.
//
// Used by C2Sain/StrobeMarkers.cs, which draws one small hot box per section through C2Sain/ThermalPole.cs and shows
// them only to ECOTI's thermal cameras. Unity-free so the logic harness
// (.for-claude-to-reference/harnesses/mcs_fork_logic) can pin every design. Reads and writes no files.
//
// Why: build 12 and 13's thermal marker was one small box on the helmet whose heat pulsed. After build 13's raid the
// user (2026-11-18, miyako-carry-service/evidence/2026-11-18-build-13-first-raid) found it hard to recognise: it sat
// above the head and looked apart from the skeleton. They proposed a tall antenna pole rising from the body, like the
// flag pole on some shopping carts, with a barber's pole style pattern on it, and a choice of designs in F12.
//
// What a design can do: ECOTI's heat shader draws a renderer hot (more than 1 degree above the air) or, for anything
// colder, black, and its overlay shows every hot pixel at full strength (research:
// miyako-carry-service/evidence/2026-11-18-coti-heat-research, section B2). So a design is only WHERE the pole is hot
// at each moment; a cold section is simply not drawn. In ECOTI's Outline mode each hot section shows as an outlined block.
//
// A "span" is one hot section: its start and end as fractions of the pole's length (0 = the foot, on the helmet; 1 =
// the tip) and how much wider than the pole it is drawn (1 = the pole's width).

using System;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>The look of the thermal marker; the F12 "Markers: thermal design" setting.</summary>
    public enum ThermalDesign
    {
        /// <summary>Hot bands slide up the pole, like the stripes of a barber's pole (the default).</summary>
        BarberPole = 0,

        /// <summary>One hot band climbs from the foot to the tip, then the pole is dark for a moment, and it repeats.</summary>
        RisingPulse = 1,

        /// <summary>The lower half and the upper half of the pole take turns being hot.</summary>
        SeeSaw = 2,

        /// <summary>The whole pole is hot for half of each cycle and dark for the other half.</summary>
        PolePulse = 3,

        /// <summary>A steady hot pole with a wider flag at its top that blinks.</summary>
        Flag = 4,

        /// <summary>A steady hot pole with no motion.</summary>
        SteadyPole = 5,

        /// <summary>The small box of builds 12 and 13 on the helmet (no pole), hot for half of each cycle.</summary>
        Box = 6,
    }

    /// <summary>The colour of the IR strobe's light; the F12 "Markers: IR strobe color" setting.</summary>
    public enum IrColor
    {
        /// <summary>A white light: all three colour channels, the brightest choice through night vision (the default from build 14).</summary>
        White = 0,

        /// <summary>A red light, as build 13 had it.</summary>
        Red = 1,
    }

    /// <summary>The hot sections of one pole at one moment. Reused every frame so nothing is allocated.</summary>
    public sealed class PoleSpans
    {
        /// <summary>The most sections a design can have at once; the plugin makes this many boxes per pole.</summary>
        public const int MAX = 6;

        /// <summary>Sections shorter than this share of the pole are dropped (a sliver that would only flicker).</summary>
        public const double MIN_LENGTH = 0.005;

        /// <summary>How many of the entries below are in use.</summary>
        public int Count;

        /// <summary>Where each section starts, as a fraction of the pole's length from its foot.</summary>
        public readonly double[] Lo = new double[MAX];

        /// <summary>Where each section ends, as a fraction of the pole's length from its foot.</summary>
        public readonly double[] Hi = new double[MAX];

        /// <summary>How much wider than the pole each section is drawn (1 = the pole's own width).</summary>
        public readonly double[] WidthScale = new double[MAX];

        /// <summary>Forgets every section.</summary>
        public void Clear()
        {
            Count = 0;
        }

        /// <summary>
        /// Adds a hot section from <paramref name="lo"/> to <paramref name="hi"/> (fractions of the pole), cut to the
        /// pole's ends. A section that ends up shorter than <see cref="MIN_LENGTH"/>, or past <see cref="MAX"/>, is dropped.
        /// </summary>
        public void Add(double lo, double hi, double widthScale = 1.0)
        {
            if (lo < 0.0)
            {
                lo = 0.0;
            }
            if (hi > 1.0)
            {
                hi = 1.0;
            }
            if (hi - lo < MIN_LENGTH || Count >= MAX)
            {
                return;
            }
            Lo[Count] = lo;
            Hi[Count] = hi;
            WidthScale[Count] = widthScale;
            Count++;
        }
    }

    /// <summary>The thermal designs; see the file header.</summary>
    public static class PoleDesigns
    {
        /// <summary>
        /// Fills <paramref name="spans"/> with the hot sections of <paramref name="design"/> at
        /// <paramref name="seconds"/> (any monotonic game time plus the bot's own offset; negative allowed). One cycle
        /// is <see cref="StrobeSettings.ThermalPeriod"/> seconds: for the barber pole it slides the bands up by one
        /// stripe, for the others it is one full run of the pattern. An unknown design value gives the steady pole.
        /// </summary>
        public static void Fill(ThermalDesign design, double seconds, StrobeSettings settings, PoleSpans spans)
        {
            spans.Clear();
            var period = settings.ThermalPeriod;
            var cycle = Wrap(seconds, period) / period;
            switch (design)
            {
                case ThermalDesign.BarberPole:
                {
                    // An endless pattern of hot bands and gaps, one stripe long each pair, slid up the pole by one
                    // stripe per cycle; the pole shows the part of it between 0 and 1.
                    var stripes = Math.Max(1, settings.BarberStripes);
                    var stripe = 1.0 / stripes;
                    var shift = cycle * stripe;
                    for (var n = -1; n < stripes; n++)
                    {
                        var lo = n * stripe + shift;
                        spans.Add(lo, lo + stripe * settings.BarberHotShare);
                    }
                    break;
                }
                case ThermalDesign.RisingPulse:
                {
                    // One band enters below the foot, climbs past the tip, and the pole is dark for the rest of the cycle.
                    if (cycle < settings.RisingTravelShare)
                    {
                        var along = cycle / settings.RisingTravelShare;
                        var band = settings.RisingBandLength;
                        var lo = along * (1.0 + band) - band;
                        spans.Add(lo, lo + band);
                    }
                    break;
                }
                case ThermalDesign.SeeSaw:
                    if (cycle < 0.5)
                    {
                        spans.Add(0.0, 0.5);
                    }
                    else
                    {
                        spans.Add(0.5, 1.0);
                    }
                    break;
                case ThermalDesign.PolePulse:
                case ThermalDesign.Box:
                    if (cycle < settings.BlinkHotShare)
                    {
                        spans.Add(0.0, 1.0);
                    }
                    break;
                case ThermalDesign.Flag:
                    spans.Add(0.0, 1.0);
                    if (cycle < settings.BlinkHotShare)
                    {
                        spans.Add(1.0 - settings.FlagLength, 1.0, settings.FlagWidthScale);
                    }
                    break;
                default:
                    spans.Add(0.0, 1.0);
                    break;
            }
        }

        private static double Wrap(double seconds, double period)
        {
            var m = seconds % period;
            return m < 0.0 ? m + period : m;
        }
    }

    /// <summary>The pole's size at a distance.</summary>
    public static class PoleGeometry
    {
        /// <summary>
        /// The pole's width in metres for a bot <paramref name="distance"/> metres from the player. With
        /// <paramref name="widenAtRange"/> the pole is never narrower than <see cref="StrobeSettings.MinPoleAngle"/>
        /// radians seen from the player (so it stays a line of a pixel or two in ECOTI's picture instead of flickering
        /// out) and never wider than <see cref="StrobeSettings.MaxPoleWidth"/>, unless the base width itself is wider.
        /// </summary>
        public static double Width(double baseWidth, double distance, bool widenAtRange, StrobeSettings settings)
        {
            if (!widenAtRange || distance <= 0.0)
            {
                return baseWidth;
            }
            var wanted = Math.Min(distance * settings.MinPoleAngle, Math.Max(baseWidth, settings.MaxPoleWidth));
            return Math.Max(baseWidth, wanted);
        }
    }

    /// <summary>The IR strobe's light colours.</summary>
    public static class IrColors
    {
        /// <summary>The red, green and blue (each 0 to 1) of the light for a colour setting; an unknown value gives white.</summary>
        public static void Rgb(IrColor color, out float r, out float g, out float b)
        {
            if (color == IrColor.Red)
            {
                r = 1f;
                g = 0f;
                b = 0f;
                return;
            }
            r = 1f;
            g = 1f;
            b = 1f;
        }
    }
}
