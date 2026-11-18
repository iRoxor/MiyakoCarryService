// StrobeRules.cs -- the timing and visibility rules of the carry bots' two markers (fork v12; v13 shortened
// the IR flashes and keeps the IR light off while ECOTI's thermal view is on the goggles; v14 moved the thermal
// marker's looks to PoleDesigns.cs and gave the IR light a colour).
//
// Used by C2Sain/StrobeMarkers.cs, which makes the markers and applies these rules every frame.
// Unity-free so the logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can pin the
// timings. Reads and writes no files.
//
// Why: through ECOTI's thermal clip-on a carry bot is a hot body like any enemy, because ECOTI never
// looks at a player's side, group or team (its code reads none; no setting changes it). The fork, which
// makes the teammates, marks them instead, with two markers that only the matching view shows:
//   - the thermal ID marker: a tall pole on the helmet (or, in the Box design, a small box) whose hot
//     sections move in a pattern (Core/PoleDesigns.cs). It carries a heat value ECOTI's heat shader
//     reads (_CotiHeatTemp, Kelvin) and is drawn only while one of ECOTI's thermal cameras (named
//     "Coti...") renders, so the naked eye and night vision never see it. No real thermal device strobes,
//     so this is a moving pattern, not a flash.
//   - the IR strobe: a small light on the helmet that flashes quickly in a pattern (the double flash by
//     default) and shines only while night vision is on and ECOTI's thermal view is not (a thermal view
//     cannot see an IR light). StrobeMarkers switches the light itself, so its brightness and reach are the
//     F12 settings' own, whatever BorkelRNVG multiplies for the game's IR flashlights.
// The research behind it: miyako-carry-service/evidence/2026-11-18-coti-heat-research. The user's first test of
// build 12 (miyako-carry-service/evidence/2026-11-18-build-12-first-raid) led to the v13 changes, and their first
// test of build 13 (miyako-carry-service/evidence/2026-11-18-build-13-first-raid) to v14's.

using System;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>How the IR strobe blinks.</summary>
    public enum IrPattern
    {
        /// <summary>Two quick flashes close together, then dark: two 0.05 s flashes 0.10 s apart every 1.5 s (the default).</summary>
        DoubleFlash = 0,

        /// <summary>One second on, one second off.</summary>
        SlowBlink = 1,

        /// <summary>Four times a second: 0.125 s on, 0.125 s off.</summary>
        FastBlink = 2,

        /// <summary>Always lit.</summary>
        Steady = 3,
    }

    /// <summary>Tunables for <see cref="StrobeRules"/>, <see cref="PoleDesigns"/> and <see cref="PoleGeometry"/>; the plugin sets the thermal period and whether the IR strobe hides under ECOTI from its settings.</summary>
    public sealed class StrobeSettings
    {
        /// <summary>Seconds in one cycle of the double flash.</summary>
        public double DoubleFlashPeriod = 1.5;

        /// <summary>
        /// Seconds each of the double flash's two flashes lasts. 0.05 s is three frames at 60 fps and still a frame
        /// at 25 fps, so a flash is not lost on a slow frame, yet it reads as the sharp quick flash of a fire alarm's
        /// strobe. Build 12 used 0.12 s, which the user found too long on their first test (2026-11-18).
        /// </summary>
        public double FlashSeconds = 0.05;

        /// <summary>Seconds from the start of the double flash's first flash to the start of its second (a 0.10 s gap; build 12's was 0.16 s).</summary>
        public double SecondFlashAt = 0.15;

        /// <summary>
        /// Whether the IR strobe stays off while ECOTI's thermal view is on the goggles: a thermal view cannot see an
        /// IR light, and under ECOTI's Outline mode the night vision picture stays on screen, so the strobe was still
        /// seen there (the user's first test of build 12). The plugin sets this from the "hide IR strobe under ECOTI"
        /// setting.
        /// </summary>
        public bool HideIrUnderThermal = true;

        /// <summary>Seconds in one cycle of the slow blink (half on, half off).</summary>
        public double SlowBlinkPeriod = 2.0;

        /// <summary>Seconds in one cycle of the fast blink (half on, half off).</summary>
        public double FastBlinkPeriod = 0.25;

        /// <summary>
        /// Seconds one cycle of the thermal design takes (the F12 "thermal pattern period"): the barber pole slides its
        /// bands up by one stripe, the rising pulse makes one climb, the blinking designs one hot half and one dark half.
        /// Build 12 and 13 used 2 s for the beacon's pulse; the pole's patterns read better a little quicker.
        /// </summary>
        public double ThermalPeriod = 1.0;

        /// <summary>Stripes (one hot band and one gap each) along the pole in the barber pole design: 3 gives bands about 0.17 m long on a 1 m pole.</summary>
        public int BarberStripes = 3;

        /// <summary>The share of each barber pole stripe that is hot; the rest is the gap.</summary>
        public double BarberHotShare = 0.5;

        /// <summary>Length of the hot band of the rising pulse, as a fraction of the pole.</summary>
        public double RisingBandLength = 0.25;

        /// <summary>The share of each rising pulse cycle spent climbing; the rest is a dark pause before the next climb.</summary>
        public double RisingTravelShare = 0.7;

        /// <summary>The share of each cycle the blinking designs (pole pulse, flag, box) are hot: half, so they are dark for as long as they are lit.</summary>
        public double BlinkHotShare = 0.5;

        /// <summary>Length of the flag at the top of the flag design, as a fraction of the pole.</summary>
        public double FlagLength = 0.15;

        /// <summary>How many times wider than the pole the flag is drawn.</summary>
        public double FlagWidthScale = 3.0;

        /// <summary>
        /// The narrowest the pole may look from the player, in radians, when it widens at range: 0.0016 is about one and
        /// a half rows of ECOTI's picture (a row is roughly 0.0008 to 0.0011 rad: its picture is about a thousand rows
        /// tall over a 50 to 75 degree field, estimated, see the research file), enough that it does not flicker out.
        /// </summary>
        public double MinPoleAngle = 0.0016;

        /// <summary>The widest the pole gets when it widens at range, in metres (0.16 m at 100 m, 0.32 m at 200 m, then this).</summary>
        public double MaxPoleWidth = 0.5;
    }

    /// <summary>The markers' rules; see the file header.</summary>
    public static class StrobeRules
    {
        /// <summary>True while the IR strobe's light is in a flash at <paramref name="seconds"/> (any monotonic game time, negative allowed).</summary>
        public static bool IrLit(IrPattern pattern, double seconds, StrobeSettings settings)
        {
            switch (pattern)
            {
                case IrPattern.DoubleFlash:
                    var m = Wrap(seconds, settings.DoubleFlashPeriod);
                    return m < settings.FlashSeconds || (m >= settings.SecondFlashAt && m < settings.SecondFlashAt + settings.FlashSeconds);
                case IrPattern.SlowBlink:
                    return Wrap(seconds, settings.SlowBlinkPeriod) < settings.SlowBlinkPeriod * 0.5;
                case IrPattern.FastBlink:
                    return Wrap(seconds, settings.FastBlinkPeriod) < settings.FastBlinkPeriod * 0.5;
                default:
                    return true;
            }
        }

        /// <summary>
        /// A per-marker offset in seconds (0 up to <paramref name="period"/>) so the carry bots do not all flash in
        /// step: real strobes are not synchronised, and a bot's own pattern is easier to follow. Spreads the
        /// first bots evenly (golden-ratio steps).
        /// </summary>
        /// <param name="index">A number that stays the same for one bot (its place in the squad).</param>
        /// <param name="period">The pattern's period in seconds.</param>
        public static double PhaseOffset(int index, double period)
        {
            var fraction = (index * 0.6180339887498949) % 1.0;
            return (fraction < 0.0 ? fraction + 1.0 : fraction) * period;
        }

        /// <summary>True when a camera belongs to ECOTI's thermal view: its name starts with "Coti" (CotiThermalCamera, CotiOpticThermalCamera).</summary>
        public static bool IsThermalCameraName(string cameraName)
        {
            return cameraName != null && cameraName.StartsWith("Coti", StringComparison.Ordinal);
        }

        /// <summary>The thermal marker is drawn only for ECOTI's thermal cameras, and only while its switch is on.</summary>
        public static bool BeaconShows(bool cameraIsThermal, bool thermalSwitchOn)
        {
            return cameraIsThermal && thermalSwitchOn;
        }

        /// <summary>
        /// The IR light shines only while its switch is on, the pattern is in a flash and night vision is on, and not
        /// while ECOTI's thermal view is on the goggles when <see cref="StrobeSettings.HideIrUnderThermal"/> is set.
        /// </summary>
        /// <param name="thermalViewOn">ECOTI's thermal overlay is up on the player's goggles (EcotiProbe).</param>
        /// <param name="settings">Supplies <see cref="StrobeSettings.HideIrUnderThermal"/>.</param>
        public static bool IrLightOn(bool irSwitchOn, bool inFlash, bool nightVisionOn, bool thermalViewOn, StrobeSettings settings)
        {
            return irSwitchOn && inFlash && nightVisionOn && !(thermalViewOn && settings.HideIrUnderThermal);
        }

        private static double Wrap(double seconds, double period)
        {
            var m = seconds % period;
            return m < 0.0 ? m + period : m;
        }
    }
}
