#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;

namespace Reflect.Internal.Platform
{
    internal sealed class AndroidPlatformBridge : IPlatformBridge
    {
        private const string JavaBridge = "com.reflect.sdk.ReflectBridge";
        private AndroidJavaClass _bridge;

        private AndroidJavaClass Bridge
        {
            get
            {
                if (_bridge == null) _bridge = new AndroidJavaClass(JavaBridge);
                return _bridge;
            }
        }

        public void Initialize(string unityReceiverName, bool advertisingConsent, bool collectImei, bool collectOaid)
        {
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    Bridge.CallStatic("initialize", activity, unityReceiverName, advertisingConsent, collectImei, collectOaid);
                }
            }
            catch (System.Exception ex) { ReflectLogger.Error($"Android initialize failed: {ex}"); }
        }

        public void CollectDeviceInfo()
        {
            try { Bridge.CallStatic("collectDeviceInfo"); }
            catch (System.Exception ex) { ReflectLogger.Error($"Android collectDeviceInfo failed: {ex}"); }
        }

        public void CollectReferral()
        {
            try { Bridge.CallStatic("collectReferral"); }
            catch (System.Exception ex) { ReflectLogger.Error($"Android collectReferral failed: {ex}"); }
        }

        public void SetAdvertisingConsent(bool granted)
        {
            try { Bridge.CallStatic("setAdvertisingConsent", granted); }
            catch (System.Exception ex) { ReflectLogger.Error($"Android setAdvertisingConsent failed: {ex}"); }
        }

        public void RequestIosTracking()
        {
            // No-op on Android.
        }

        public void UpdateSkanConversionValue(int fineValue, string coarseValue, bool lockWindow)
        {
            // No-op on Android — SKAN is iOS only.
        }
    }
}
#endif
