namespace Reflect.Internal
{
    /// <summary>
    /// Thin logger over the Unity console. Info lines are gated by <see cref="Enabled"/>
    /// (set from <c>ReflectConfig.EnableLogging</c>); warnings + errors always print.
    /// </summary>
    internal static class ReflectLogger
    {
        private const string Prefix = "[Reflect] ";
        public static bool Enabled = false;

        public static void Info(string msg)
        {
            if (!Enabled) return;
            UnityEngine.Debug.Log(Prefix + msg);
        }

        public static void Warn(string msg) => UnityEngine.Debug.LogWarning(Prefix + msg);

        public static void Error(string msg) => UnityEngine.Debug.LogError(Prefix + msg);
    }
}
