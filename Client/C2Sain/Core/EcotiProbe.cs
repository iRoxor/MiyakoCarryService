// EcotiProbe.cs -- reads whether ECOTI's thermal view is up on the player's goggles (fork v13).
//
// Why: a thermal view cannot see an IR light. With ECOTI on the night vision goggles (its default Outline mode
// keeps the night vision picture and draws heat outlines over it) the carry bots' IR strobe was still seen there,
// which the user reported after their first raid with build 12 (2026-11-18,
// miyako-carry-service/evidence/2026-11-18-build-12-first-raid). C2Sain/StrobeMarkers keeps the IR light off while
// this class says the thermal view is on.
//
// How: ECOTI 3.5.1 (Coti.Client.dll, read with IL tools on 2026-11-18) keeps the goggle thermal state in a static
// field, Coti.Client.CotiState.Active, set once a frame by CotiState.Update: true only while the goggles' thermal is
// powered up and shown (false while it is off, still starting, or ECOTI has no entry for the goggles worn). This
// class finds that field by reflection in the loaded Coti.Client assembly and reads it. It only reads: nothing of
// ECOTI is changed or called, and ECOTI is not referenced at build time, so MCS loads the same without it.
//
// Why not "a camera named Coti... rendered": ECOTI's 1x thermal camera also renders whenever a thermal riser is
// powered on the held weapon, aimed or not (CotiCameraDuty.OneX), so that test would hide the IR strobe all the
// time for a player who keeps a riser on. A thermal riser's scope view is not looked at here: its lens shows heat
// only, so the IR light never shows in it anyway.
//
// Unity-free so the logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it against a fake
// assembly built at run time. Owner: StrobeMarkers holds the one instance the plugin uses. Main thread only.
// Reads and writes no files.

using System;
using System.Reflection;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>Reads ECOTI's goggle thermal state; see the file header.</summary>
    public sealed class EcotiProbe
    {
        /// <summary>The name of ECOTI's client assembly (ECOTI 3.5.1).</summary>
        public const string ASSEMBLY = "Coti.Client";

        /// <summary>The class that holds ECOTI's goggle state (ECOTI 3.5.1).</summary>
        public const string STATE_TYPE = "Coti.Client.CotiState";

        /// <summary>The static bool that is true while the goggles' thermal is up (ECOTI 3.5.1).</summary>
        public const string ACTIVE_FIELD = "Active";

        private readonly string _assemblyName;
        private readonly string _typeName;
        private readonly string _fieldName;
        private FieldInfo _active;
        private bool _resolved;
        private string _status = "not looked for yet";

        /// <summary>Makes a probe; the plugin uses the defaults, the harness points it at a fake assembly.</summary>
        /// <param name="assemblyName">Simple name of the assembly that holds the state.</param>
        /// <param name="typeName">Full name of the class that holds it.</param>
        /// <param name="fieldName">Name of its static bool field.</param>
        public EcotiProbe(string assemblyName = ASSEMBLY, string typeName = STATE_TYPE, string fieldName = ACTIVE_FIELD)
        {
            _assemblyName = assemblyName;
            _typeName = typeName;
            _fieldName = fieldName;
        }

        /// <summary>What the last look found, for the diagnostic log: where the state is read from, or why it cannot be.</summary>
        public string Status => _status;

        /// <summary>
        /// True while ECOTI's thermal overlay is up on the goggles; false when it is not, when ECOTI is not installed
        /// and when its state cannot be read (<see cref="Status"/> says which). Finds the field on the first call after
        /// construction or <see cref="Reset"/>. A read by reflection boxes a value (24 bytes), so poll a few times a
        /// second rather than every frame.
        /// </summary>
        public bool GoggleThermalOn()
        {
            if (!_resolved)
            {
                Resolve();
            }
            if (_active == null)
            {
                return false;
            }
            try
            {
                return (bool)_active.GetValue(null);
            }
            catch (Exception e)
            {
                _active = null;
                _status = $"reading {_typeName}.{_fieldName} failed ({e.GetType().Name})";
                return false;
            }
        }

        /// <summary>Forgets what was found so the next call looks again (a new raid; ECOTI may have loaded since).</summary>
        public void Reset()
        {
            _resolved = false;
            _active = null;
            _status = "not looked for yet";
        }

        /// <summary>Looks for the assembly, the class and the field once, and records the outcome in <see cref="Status"/>.</summary>
        private void Resolve()
        {
            _resolved = true;
            _active = null;
            try
            {
                Assembly ecoti = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (assembly.GetName().Name == _assemblyName)
                        {
                            ecoti = assembly;
                            break;
                        }
                    }
                    catch (Exception)
                    {
                        // An assembly whose name cannot be read is not ECOTI's: go on to the next.
                    }
                }
                if (ecoti == null)
                {
                    _status = $"ECOTI is not loaded (no assembly {_assemblyName})";
                    return;
                }
                var state = ecoti.GetType(_typeName, false);
                if (state == null)
                {
                    _status = $"ECOTI is loaded but has no class {_typeName} (a version this fork was not written for)";
                    return;
                }
                var field = state.GetField(_fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null || field.FieldType != typeof(bool))
                {
                    _status = $"{_typeName} has no static bool field {_fieldName} (a version this fork was not written for)";
                    return;
                }
                _active = field;
                _status = $"read from {_typeName}.{_fieldName}";
            }
            catch (Exception e)
            {
                _active = null;
                _status = $"looking for {_typeName}.{_fieldName} failed ({e.GetType().Name})";
            }
        }
    }
}
