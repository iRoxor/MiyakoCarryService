// V3.cs -- a minimal 3D vector for the C2/SAIN fork's Unity-free core.
//
// The files under C2Sain/Core compile both into the BepInEx plugin and into the
// logic harness (.for-claude-to-reference/harnesses/mcs_fork_logic), which runs
// on plain .NET without UnityEngine. This struct is what lets them share code.
// Reads and writes nothing.

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>An immutable 3D position. Plugin code converts to and from UnityEngine.Vector3.</summary>
    public readonly struct V3
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        /// <summary>Builds a position from its three components.</summary>
        public V3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>Straight-line (3D) distance between two positions.</summary>
        public static float Distance(V3 a, V3 b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return (float)System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Debug text such as "(1.0, 2.0, 3.0)".</summary>
        public override string ToString()
        {
            return $"({X:0.0}, {Y:0.0}, {Z:0.0})";
        }
    }
}
