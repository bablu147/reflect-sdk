using System.Collections.Generic;
using System.Text;
using Reflect.Internal;

namespace Reflect
{
    /// <summary>
    /// Internal event representation. Serialized to JSON before dispatch.
    /// </summary>
    internal sealed class ReflectEvent
    {
        public string EventId;
        public string EventName;
        public long   EventTsMs;
        public string InstallUuid;
        public string UserId;
        public string SdkVersion;
        public IosTrackingStatus AttStatus;
        public string ConsentState;
        public DeviceSnapshot  Device;
        public ReferralSnapshot Referral;
        public double? Revenue;
        public string  Currency;
        public string  TransactionId;
        public string  ProductId;
        public IDictionary<string, object> Properties;

        /// <summary>Compact JSON representation of this event. No dependencies.</summary>
        public string ToJson()
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            JsonWriter.Kv(sb, "event_id", EventId);       sb.Append(',');
            JsonWriter.Kv(sb, "event_name", EventName);   sb.Append(',');
            JsonWriter.KvNum(sb, "event_ts_ms", EventTsMs); sb.Append(',');
            JsonWriter.Kv(sb, "install_uuid", InstallUuid); sb.Append(',');
            JsonWriter.Kv(sb, "user_id", UserId);         sb.Append(',');
            JsonWriter.Kv(sb, "sdk_version", SdkVersion); sb.Append(',');
            JsonWriter.Kv(sb, "att_status", AttStatus.ToString()); sb.Append(',');
            JsonWriter.Kv(sb, "consent_state", ConsentState ?? "granted"); sb.Append(',');

            // Device object
            sb.Append("\"device\":");
            WriteDevice(sb, Device);
            sb.Append(',');

            // Referral object (nullable)
            sb.Append("\"referral\":");
            WriteReferral(sb, Referral);

            // Revenue (nullable)
            if (Revenue.HasValue)
            {
                sb.Append(',');
                JsonWriter.KvNum(sb, "revenue", Revenue.Value);
            }
            if (!string.IsNullOrEmpty(Currency))
            {
                sb.Append(',');
                JsonWriter.Kv(sb, "currency", Currency);
            }
            if (!string.IsNullOrEmpty(TransactionId))
            {
                sb.Append(',');
                JsonWriter.Kv(sb, "transaction_id", TransactionId);
            }
            if (!string.IsNullOrEmpty(ProductId))
            {
                sb.Append(',');
                JsonWriter.Kv(sb, "product_id", ProductId);
            }

            // Custom properties
            sb.Append(",\"props\":");
            WriteProps(sb, Properties);

            sb.Append('}');
            return sb.ToString();
        }

        private static void WriteDevice(StringBuilder sb, DeviceSnapshot d)
        {
            if (d == null) { sb.Append("null"); return; }
            sb.Append('{');
            JsonWriter.Kv(sb, "gaid", d.Gaid); sb.Append(',');
            JsonWriter.KvBool(sb, "lat_enabled", d.LatEnabled); sb.Append(',');
            JsonWriter.Kv(sb, "idfa", d.Idfa); sb.Append(',');
            JsonWriter.Kv(sb, "idfv", d.Idfv); sb.Append(',');
            JsonWriter.Kv(sb, "android_id", d.AndroidId); sb.Append(',');
            JsonWriter.Kv(sb, "os", d.Os); sb.Append(',');
            JsonWriter.Kv(sb, "os_version", d.OsVersion); sb.Append(',');
            JsonWriter.KvNum(sb, "api_level", d.ApiLevel); sb.Append(',');
            JsonWriter.Kv(sb, "device_model", d.DeviceModel); sb.Append(',');
            JsonWriter.Kv(sb, "device_manufacturer", d.DeviceManufacturer); sb.Append(',');
            JsonWriter.Kv(sb, "device_brand", d.DeviceBrand); sb.Append(',');
            JsonWriter.Kv(sb, "cpu_arch", d.CpuArch); sb.Append(',');
            JsonWriter.KvNum(sb, "screen_width", d.ScreenWidth); sb.Append(',');
            JsonWriter.KvNum(sb, "screen_height", d.ScreenHeight); sb.Append(',');
            JsonWriter.KvNum(sb, "screen_density", d.ScreenDensity); sb.Append(',');
            JsonWriter.KvNum(sb, "total_ram_mb", d.TotalRamMb); sb.Append(',');
            JsonWriter.Kv(sb, "app_bundle_id", d.AppBundleId); sb.Append(',');
            JsonWriter.Kv(sb, "app_version", d.AppVersion); sb.Append(',');
            JsonWriter.KvNum(sb, "app_version_code", d.AppVersionCode); sb.Append(',');
            JsonWriter.Kv(sb, "install_source", d.InstallSource); sb.Append(',');
            JsonWriter.KvNum(sb, "first_install_time", d.FirstInstallTime); sb.Append(',');
            JsonWriter.KvNum(sb, "last_update_time", d.LastUpdateTime); sb.Append(',');
            JsonWriter.Kv(sb, "language", d.Language); sb.Append(',');
            JsonWriter.Kv(sb, "locale", d.Locale); sb.Append(',');
            JsonWriter.Kv(sb, "timezone", d.Timezone); sb.Append(',');
            JsonWriter.KvNum(sb, "tz_offset_min", d.TimezoneOffsetMinutes); sb.Append(',');
            JsonWriter.Kv(sb, "connection_type", d.ConnectionType); sb.Append(',');
            JsonWriter.Kv(sb, "carrier", d.Carrier); sb.Append(',');
            JsonWriter.Kv(sb, "carrier_mcc", d.CarrierMcc); sb.Append(',');
            JsonWriter.Kv(sb, "carrier_mnc", d.CarrierMnc); sb.Append(',');
            JsonWriter.KvBool(sb, "is_emulator", d.IsEmulator); sb.Append(',');
            JsonWriter.KvBool(sb, "is_rooted", d.IsRooted); sb.Append(',');
            JsonWriter.KvBool(sb, "vpn_detected", d.VpnDetected); sb.Append(',');
            JsonWriter.KvBool(sb, "mock_location_enabled", d.MockLocationEnabled);
            sb.Append('}');
        }

        private static void WriteReferral(StringBuilder sb, ReferralSnapshot r)
        {
            if (r == null) { sb.Append("null"); return; }
            sb.Append('{');
            JsonWriter.Kv(sb, "raw", r.Raw); sb.Append(',');
            JsonWriter.KvNum(sb, "click_ts", r.ReferrerClickTs); sb.Append(',');
            JsonWriter.KvNum(sb, "install_ts", r.InstallBeginTs); sb.Append(',');
            JsonWriter.KvNum(sb, "click_server_ts", r.ReferrerClickServerTs); sb.Append(',');
            JsonWriter.KvNum(sb, "install_server_ts", r.InstallBeginServerTs); sb.Append(',');
            JsonWriter.KvBool(sb, "google_play_instant", r.GooglePlayInstant); sb.Append(',');
            JsonWriter.Kv(sb, "attribution_token", r.AttributionToken); sb.Append(',');
            JsonWriter.Kv(sb, "source", r.Source);
            sb.Append('}');
        }

        private static void WriteProps(StringBuilder sb, IDictionary<string, object> props)
        {
            if (props == null || props.Count == 0) { sb.Append("{}"); return; }
            sb.Append('{');
            bool first = true;
            foreach (var kv in props)
            {
                if (!first) sb.Append(',');
                first = false;
                JsonWriter.WriteString(sb, kv.Key);
                sb.Append(':');
                JsonWriter.WriteValue(sb, kv.Value);
            }
            sb.Append('}');
        }
    }
}
