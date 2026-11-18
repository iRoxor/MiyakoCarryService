// StrobeRules.cs -- the timing and visibility rules of the carry bots' two markers (fork v12; v13 shortened
// the IR flashes and keeps the IR light off while ECOTI's thermal view is on the goggles).
//
// Used by C2Sain/StrobeMarkers.cs, which makes the markers and applies these rules every frame.
// Unity-free so the logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can pin the
// timings. Reads and writes no files.
//
// Why: through ECOTI's thermal clip-on a carry bot is a hot body like any enemy, because ECOTI never
// looks at a player's side, group or team (its code reads none; no setting changes it). The fork, which
// makes the teammates, marks them instead, with two markers that only the matching view shows:
//   - the thermal ID beacon: a small solid marker on the helmet whose heat value pulses between hot and
//     cold, as if a rotating heated element sat inside it. It carries a heat value ECOTI's heat shader
//     reads (_CotiHeatTemp, Kelvin) and is drawn only while one of ECOTI's thermal cameras (named
//     "Coti...") renders, so the naked eye and night vision never see it. No real thermal device strobes,
//     so this is a pulse, not a flash.
//   - the IR strobe: a small red light on the helmet that flashes quickly in a pattern (the double flash by
//     default) and shines only while night vision is on and ECOTI's thermal view is not (a thermal view
//     cannot see an IR light). StrobeMarkers switches the light itself, so its brightness and reach are the
//     F12 settings' own, whatever BorkelRNVG multiplies for the game's IR flashlights.
// The research behind it: miyako-carry-service/evidence/2026-11-18-coti-heat-research. The user's first test of
// build 12 (miyako-carry-service/evidence/2026-11-18-build-12-first-raid) led to the v13 changes.

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

    /// <summary>Tunables for <see cref="StrobeRules"/>; the plugin sets the thermal period and heat and whether the IR strobe hides under ECOTI from its settings.</summary>
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

        /// <summary>Seconds between the thermal beacon's hot peaks.</summary>
        public double ThermalPeriod = 2.0;

        /// <summary>The thermal beacon's hottest temperature (Celsius). 65 is far above any air temperature, so ECOTI draws it at full brightness.</summary>
        public double HotCelsius = 65.0;

        /// <summary>
        /// The thermal beacon's coldest temperature (Celsius). At or below the air temperature plus 1 degree ECOTI
        /// draws it cold (black), which is the "cold" half of the pulse. -30 keeps the hot and cold halves of the
        /// pulse about equal at 15 degrees air, and stays cold in any weather the game has.
        /// </summary>
        public double ColdCelsius = -30.0;
    }

    /// <summary>The markers' rules; see the file header.</summary>
    public static class StrobeRules
    {
        /// <summary>
        /// ECOTI's heat shader draws a renderer hot only when its heat is more than this many degrees Celsius
        /// above the air (read from the shader's disassembled Opaque fragment, fork research 2026-11-18).
        /// </summary>
        public const double ECOTI_HOT_ABOVE_AIR = 1.0;

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
        /// The thermal beacon's temperature (Celsius) at <paramref name="seconds"/>: a smooth pulse from
        /// <see cref="StrobeSettings.ColdCelsius"/> at the start of each period up to
        /// <see cref="StrobeSettings.HotCelsius"/> halfway through it and back.
        /// </summary>
        public static double ThermalCelsius(double seconds, StrobeSettings settings)
        {
            var share = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * Wrap(seconds, settings.ThermalPeriod) / settings.ThermalPeriod);
            return settings.ColdCelsius + (settings.HotCelsius - settings.ColdCelsius) * share;
        }

        /// <summary>
        /// The share of each period (0 to 1) that ECOTI draws the thermal beacon hot at the given air temperature:
        /// the time its temperature is more than <see cref="ECOTI_HOT_ABOVE_AIR"/> degrees above the air. Under
        /// 1 means the beacon does go cold; the harness requires it to for every air temperature the game has.
        /// </summary>
        public static double ThermalHotShare(double airCelsius, StrobeSettings settings)
        {
            var range = settings.HotCelsius - settings.ColdCelsius;
            if (range <= 0.0)
            {
                return settings.HotCelsius > airCelsius + ECOTI_HOT_ABOVE_AIR ? 1.0 : 0.0;
            }
            var needed = (airCelsius + ECOTI_HOT_ABOVE_AIR - settings.ColdCelsius) / range;
            if (needed <= 0.0)
            {
                return 1.0;
            }
            if (needed >= 1.0)
            {
                return 0.0;
            }
            return 1.0 - Math.Acos(1.0 - 2.0 * needed) / Math.PI;
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

        /// <summary>The thermal beacon is drawn only for ECOTI's thermal cameras, and only while its switch is on.</summary>
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
