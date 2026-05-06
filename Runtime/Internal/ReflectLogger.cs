using Reflect.Internal.Debug;

namespace Reflect.Internal
{
    /// <summary>
    /// Thin logger that fans out to both the Unity console and the in-memory
    /// <see cref="ReflectLogBuffer"/> powering the debug overlay. Info lines only
    /// reach the Unity console when <see cref="Enabled"/> is true, but every line
    /// — regardless of level or Enabled flag — lands in the buffer so the
    /// overlay can show a complete trace.
    ///
    /// <para>Note: we fully qualify <c>UnityEngine.Debug</c> everywhere below
    /// because this file lives inside a namespace that also contains a
    /// <c>Reflect.Internal.Debug</c> sub-namespace (where
    /// <see cref="ReflectLogBuffer"/> lives). A bare <c>Debug.Log</c> would be
    /// resolved as that namespace and fail to compile — so don't be tempted
    /// to "simplify" by adding <c>using UnityEngine;</c>.</para>
    /// </summary>
    internal static class ReflectLogger
    {
        private const string Prefix = "[Reflect] ";
        public static bool Enabled = false;

        public static void Info(string msg)
        {
            ReflectLogBuffer.Append(ReflectLogBuffer.Level.Info, msg);
            if (!Enabled) return;
            UnityEngine.Debug.Log(Prefix + msg);
        }

        public static void Warn(string msg)
        {
            ReflectLogBuffer.Append(ReflectLogBuffer.Level.Warn, msg);
            UnityEngine.Debug.LogWarning(Prefix + msg);
        }

        public static void Error(string msg)
        {
            ReflectLogBuffer.Append(ReflectLogBuffer.Level.Error, msg);
            UnityEngine.Debug.LogError(Prefix + msg);
        }
    }
}
