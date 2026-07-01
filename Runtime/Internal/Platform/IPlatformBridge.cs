namespace Reflect.Internal.Platform
{
    /// <summary>
    /// Thin transport to the shared native core. Every SDK operation is one
    /// <see cref="Call"/> onto <c>ReflectCore.handle(method, args, result)</c>; the
    /// core pushes async results + the deep-link/attribution streams back via
    /// <c>UnitySendMessage</c> to <see cref="ReflectCallbackReceiver"/>
    /// (channels <c>OnCallResult</c> / <c>OnDeepLink</c> / <c>OnAttribution</c>).
    ///
    /// This replaces the old collection-only bridge — in the shared-core model the
    /// native engine owns the envelope, queue, signing, sessions, attribution and
    /// device collection; C# only marshals.
    /// </summary>
    internal interface IPlatformBridge
    {
        /// <summary>Construct the core, wire the listener, and run <c>initialize</c>.
        /// <paramref name="configJson"/> is <see cref="ReflectConfig.ToJson"/>.</summary>
        void Initialize(string unityReceiverName, string configJson);

        /// <summary>Dispatch a core method. <paramref name="argsJson"/> is a JSON object of
        /// that method's args; <paramref name="callbackId"/> is a non-empty correlation id
        /// when the caller wants the result back via <c>OnCallResult</c>, or "" for
        /// fire-and-forget.</summary>
        void Call(string method, string argsJson, string callbackId);
    }

    internal static class PlatformBridgeFactory
    {
        public static IPlatformBridge Create()
        {
#if UNITY_EDITOR
            return new EditorPlatformBridge();
#elif UNITY_ANDROID
            return new AndroidPlatformBridge();
#elif UNITY_IOS
            return new IOSPlatformBridge();
#else
            return new EditorPlatformBridge();
#endif
        }
    }
}
