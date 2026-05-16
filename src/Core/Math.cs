// Thin aliases so the rest of the codebase uses familiar names
// while staying on pure System.Numerics — no engine dependency.
global using Vector2    = System.Numerics.Vector2;
global using Vector3    = System.Numerics.Vector3;
global using Vector4    = System.Numerics.Vector4;
global using Quaternion = System.Numerics.Quaternion;
global using Matrix4x4  = System.Numerics.Matrix4x4;
global using Color      = System.Numerics.Vector4;   // RGBA float

using System.Numerics;
using System.Runtime.CompilerServices;

namespace SLQuest.Core
{
    public static class MathEx
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
            => Vector3.Lerp(a, b, t);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
            => Quaternion.Slerp(a, b, t);

        /// <summary>Converts Second Life (Z-up) coordinates to our Y-up world space.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 SLToWorld(OpenMetaverse.Vector3 sl)
            => new(sl.X, sl.Z, sl.Y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion SLToWorld(OpenMetaverse.Quaternion sl)
            => new(sl.X, sl.Z, sl.Y, sl.W);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static OpenMetaverse.Vector3 WorldToSL(Vector3 w)
            => new(w.X, w.Z, w.Y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static OpenMetaverse.Quaternion WorldToSL(Quaternion w)
            => new(w.X, w.Z, w.Y, w.W);

        public static Matrix4x4 TRS(Vector3 pos, Quaternion rot, Vector3 scale)
        {
            var m = Matrix4x4.CreateScale(scale)
                  * Matrix4x4.CreateFromQuaternion(rot);
            m.Translation = pos;
            return m;
        }

        public static readonly Vector3 Up      = Vector3.UnitY;
        public static readonly Vector3 Forward = Vector3.UnitZ;
        public static readonly Vector3 Right   = Vector3.UnitX;
    }

    public readonly record struct Transform3D(Vector3 Position, Quaternion Rotation, Vector3 Scale)
    {
        public static readonly Transform3D Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
        public Matrix4x4 ToMatrix() => MathEx.TRS(Position, Rotation, Scale);
    }

    public readonly record struct Color32(byte R, byte G, byte B, byte A)
    {
        public Color ToFloat() => new(R / 255f, G / 255f, B / 255f, A / 255f);
        public static readonly Color32 White = new(255, 255, 255, 255);
        public static readonly Color32 Black = new(0, 0, 0, 255);
    }
}
