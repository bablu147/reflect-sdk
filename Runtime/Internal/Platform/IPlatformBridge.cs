namespace Reflect.Internal.Platform
{
    /// <summary>
    /// Abstraction for per-platform native integration.
    /// Implementations push results back via UnitySendMessage to
    /// <see cref="ReflectCallbackReceiver"/>.
    /// </summary>
    internal interface IPlatformBridge
    {
        /// <summary>Set up native side with the Unity GameObject name used for callbacks.</summary>
        void Initialize(string unityReceiverName, bool advertisingConsent);

        /// <summary>Collect device info asynchronously. Result delivered via <c>OnDeviceInfoJson</c>.</summary>
        void CollectDeviceInfo();

        /// <summary>Collect referral / attribution info asynchronously. Result via <c>OnReferralJson</c>.</summary>
        void CollectReferral();

        /// <summary>Update advertising-ID consent flag.</summary>
        void SetAdvertisingConsent(bool granted);

        /// <summary>iOS only — show ATT prompt. Result via <c>OnAttStatusCode</c>. No-op elsewhere.</summary>
        void RequestIosTracking();
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
