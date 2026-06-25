#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;

namespace Reflect.Internal.Platform
{
    internal sealed class IOSPlatformBridge : IPlatformBridge
    {
        [DllImport("__Internal")] private static extern void _reflect_initialize(string receiver, bool adConsent);
        [DllImport("__Internal")] private static extern void _reflect_collect_device_info();
        [DllImport("__Internal")] private static extern void _reflect_collect_referral();
        [DllImport("__Internal")] private static extern void _reflect_set_ad_consent(bool granted);
        [DllImport("__Internal")] private static extern void _reflect_request_att();
        [DllImport("__Internal")] private static extern void _reflect_update_conversion_value(int fineValue, string coarseValue, bool lockWindow);

        public void Initialize(string unityReceiverName, bool advertisingConsent, bool collectImei, bool collectOaid)
            => _reflect_initialize(unityReceiverName, advertisingConsent);   // China IDs are Android-only
        public void CollectDeviceInfo() => _reflect_collect_device_info();
        public void CollectReferral() => _reflect_collect_referral();
        public void SetAdvertisingConsent(bool granted) => _reflect_set_ad_consent(granted);
        public void RequestIosTracking() => _reflect_request_att();
        public void UpdateSkanConversionValue(int fineValue, string coarseValue, bool lockWindow)
            => _reflect_update_conversion_value(fineValue, coarseValue ?? "", lockWindow);
    }
}
#endif
