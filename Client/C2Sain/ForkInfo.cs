// ForkInfo.cs -- identity of the C2/SAIN fork build.
//
// Read by MiyakoCarryServiceClient.Start (startup log line) and ForkDebug (the
// in-raid debug overlay), so a log or screenshot names the build it came from.
// This fork keeps no analyzable records (it writes only to the BepInEx log), so
// one behaviour counter is enough; there is no schema version.

namespace MiyakoCarryService.Client.C2Sain
{
    /// <summary>Version stamp of the fork. Bump on any change a player could observe.</summary>
    internal static class ForkInfo
    {
        /// <summary>
        /// Behaviour version of the fork, separate from the upstream MCS version (1.1.4.0).
        ///
        /// Changelog:
        ///  1 (2026-11-04) Phase 2 core: SAIN layers kept (minus Extract/Debug), the game's
        ///    AvoidDanger and Malfunction layers kept, a priority-19 Guard layer added, team modes
        ///    C2 / Guard / Combat, the SAIN decision leash (3-5 s chases), Break off from combat,
        ///    gap crossing and MCS fast doors off by default, follow spots must be in the player's
        ///    line on the navmesh, "too far" fixed (was ~4.5 m). Measured: nothing yet; this is
        ///    the first in-raid test build. Falsifier: if guard/combat hand-offs make bots freeze
        ///    or flap between layers in the user's raid test, the gate in C2Gate goes back.
        /// </summary>
        public const int ForkVersion = 1;

        /// <summary>Short label used in logs and the overlay.</summary>
        public const string Label = "C2/SAIN fork v1";
    }
}
