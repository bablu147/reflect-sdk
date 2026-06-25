using System.Globalization;
using System.Text;
using UnityEngine;

namespace Reflect.Internal.Platform
{
    /// <summary>
    /// No-native-code implementation for Editor + fallback platforms (Windows/Mac standalone).
    /// Synthesises plausible device info so that integration can be tested in the Editor.
    /// </summary>
    internal sealed class EditorPlatformBridge : IPlatformBridge
    {
        private string _receiver;

        public void Initialize(string unityReceiverName, bool advertisingConsent, bool collectImei, bool collectOaid)
        {
            _receiver = unityReceiverName;
            ReflectLogger.Info("Editor platform bridge initialized.");
        }

        public void CollectDeviceInfo()
        {
            if (string.IsNullOrEmpty(_receiver)) return;
            var sb = new StringBuilder(512);
            sb.Append('{');
            Append(sb, "os", Application.platform.ToString()); sb.Append(',');
            Append(sb, "os_version", SystemInfo.operatingSystem); sb.Append(',');
            AppendNum(sb, "api_level", 0); sb.Append(',');
            Append(sb, "device_model", SystemInfo.deviceModel); sb.Append(',');
            Append(sb, "device_manufacturer", "Unknown"); sb.Append(',');
            Append(sb, "device_brand", "Unknown"); sb.Append(',');
            Append(sb, "cpu_arch", SystemInfo.processorType); sb.Append(',');
            AppendNum(sb, "screen_width", Screen.width); sb.Append(',');
            AppendNum(sb, "screen_height", Screen.height); sb.Append(',');
            AppendNum(sb, "screen_density", 0); sb.Append(',');
            AppendNum(sb, "total_ram_mb", SystemInfo.systemMemorySize); sb.Append(',');
            Append(sb, "app_bundle_id", Application.identifier); sb.Append(',');
            Append(sb, "app_version", Application.version); sb.Append(',');
            AppendNum(sb, "app_version_code", 0); sb.Append(',');
            Append(sb, "install_source", "editor"); sb.Append(',');
            AppendNum(sb, "first_install_time", 0); sb.Append(',');
            AppendNum(sb, "last_update_time", 0); sb.Append(',');
            Append(sb, "language", Application.systemLanguage.ToString()); sb.Append(',');
            Append(sb, "locale", CultureInfo.CurrentCulture.Name); sb.Append(',');
            Append(sb, "timezone", System.TimeZoneInfo.Local.Id); sb.Append(',');
            AppendNum(sb, "tz_offset_min", (int)System.TimeZoneInfo.Local.GetUtcOffset(System.DateTime.Now).TotalMinutes); sb.Append(',');
            Append(sb, "connection_type", ConnectionType()); sb.Append(',');
            Append(sb, "carrier", null); sb.Append(',');
            Append(sb, "carrier_mcc", null); sb.Append(',');
            Append(sb, "carrier_mnc", null); sb.Append(',');
            AppendBool(sb, "is_emulator", false); sb.Append(',');
            AppendBool(sb, "is_rooted", false); sb.Append(',');
            AppendBool(sb, "vpn_detected", false); sb.Append(',');
            AppendBool(sb, "mock_location_enabled", false);
            sb.Append('}');

            var go = GameObject.Find(_receiver);
            if (go != null) go.SendMessage("OnDeviceInfoJson", sb.ToString());
        }

        public void CollectReferral()
        {
            if (string.IsNullOrEmpty(_receiver)) return;
            // No real referral in editor — return an empty shell so C# side doesn't stall.
            var json = "{\"raw\":null,\"click_ts\":0,\"install_ts\":0,\"source\":\"editor\"}";
            var go = GameObject.Find(_receiver);
            if (go != null) go.SendMessage("OnReferralJson", json);
        }

        public void SetAdvertisingConsent(bool granted) { /* no-op */ }

        public void RequestIosTracking()
        {
            if (string.IsNullOrEmpty(_receiver)) return;
            var go = GameObject.Find(_receiver);
            if (go != null) go.SendMessage("OnAttStatusCode", "99"); // Unavailable
        }

        public void UpdateSkanConversionValue(int fineValue, string coarseValue, bool lockWindow)
        {
            ReflectLogger.Info($"Editor: UpdateSkanConversionValue({fineValue}, {coarseValue}, {lockWindow}) — no-op");
            if (string.IsNullOrEmpty(_receiver)) return;
            var go = GameObject.Find(_receiver);
            if (go != null) go.SendMessage("OnSkanCvUpdateResult", "ok");
        }

        private static string ConnectionType()
        {
            switch (Application.internetReachability)
            {
                case NetworkReachability.NotReachable:                 return "none";
                case NetworkReachability.ReachableViaCarrierDataNetwork: return "cellular";
                case NetworkReachability.ReachableViaLocalAreaNetwork:   return "wifi";
                default: return "unknown";
            }
        }

        private static void Append(StringBuilder sb, string k, string v)
        {
            JsonWriter.WriteString(sb, k); sb.Append(':');
            if (v == null) sb.Append("null"); else JsonWriter.WriteString(sb, v);
        }
        private static void AppendNum(StringBuilder sb, string k, long v)
        {
            JsonWriter.WriteString(sb, k); sb.Append(':');
            sb.Append(v.ToString(CultureInfo.InvariantCulture));
        }
        private static void AppendBool(StringBuilder sb, string k, bool v)
        {
            JsonWriter.WriteString(sb, k); sb.Append(':');
            sb.Append(v ? "true" : "false");
        }
    }
}
