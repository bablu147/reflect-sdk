namespace Reflect.Internal.Platform
{
    /// <summary>
    /// Abstraction for per-platform native integration.
    /// Implementations push results back via UnitySendMessage to
    /// <see cref="ReflectCallbackReceiver"/>.
    /// </summary>
    internal interface IPlatformBridge
    {
        /// <summary>Set up native side with the Unity GameObject name used for callbacks.
        /// collectImei/collectOaid opt into the China-market identifiers (off by default).</summary>
        void Initialize(string unityReceiverName, bool advertisingConsent, bool collectImei, bool collectOaid);

        /// <summary>Collect device info asynchronously. Result delivered via <c>OnDeviceInfoJson</c>.</summary>
        void CollectDeviceInfo();

        /// <summary>Collect referral / attribution info asynchronously. Result via <c>OnReferralJson</c>.</summary>
        void CollectReferral();

        /// <summary>Update advertising-ID consent flag.</summary>
        void SetAdvertisingConsent(bool granted);

        /// <summary>iOS only — show ATT prompt. Result via <c>OnAttStatusCode</c>. No-op elsewhere.</summary>
        void RequestIosTracking();

        /// <summary>
        /// Update the SKAN conversion value. Uses AdAttributionKit on iOS 17.4+,
        /// SKAdNetwork 4.0 on iOS 16.1+, or legacy SKAN on older versions.
        /// Result via <c>OnSkanCvUpdateResult</c>. No-op on Android/Editor.
        /// </summary>
        /// <param name="fineValue">Fine conversion value (0-63).</param>
        /// <param name="coarseValue">"low", "medium", "high", or empty for none.</param>
        /// <param name="lockWindow">If true, lock the current postback window.</param>
        void UpdateSkanConversionValue(int fineValue, string coarseValue, bool lockWindow);
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
