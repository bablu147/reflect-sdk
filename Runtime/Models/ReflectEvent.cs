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
        // App version captured in pure C# (UnityEngine.Application.version) so it
        // is present even when native device collection is unavailable (e.g. the
        // native bridge was stripped from a release build). Distinct from the
        // richer device.app_version* fields, which depend on the native collector.
        public string AppVersion;
        // Adjust-parity envelope signals (pure C#).
        public string Environment;
        public bool   IsForeground;
        public string PushToken;
        public string ExternalDeviceId;   // customer-set cross-system device id (Adjust: external_device_id)
        public bool   Coppa;              // Adjust: ff_coppa
        public bool   ThirdPartySharing = true;  // Adjust: third-party data sharing opt-in
        public IosTrackingStatus AttStatus;
        public string ConsentState;
        public DeviceSnapshot  Device;
        public ReferralSnapshot Referral;
        public double? Revenue;
        public string  Currency;
        public string  TransactionId;
        public string  ProductId;
        public IDictionary<string, object> Properties;
        // Adjust parity: partner_params — a separate key/value map forwarded to ad
        // network partners (distinct from callback/global props which go to props).
        public IDictionary<string, object> PartnerParams;

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
            JsonWriter.Kv(sb, "app_version", AppVersion); sb.Append(',');
            JsonWriter.Kv(sb, "environment", Environment); sb.Append(',');
            JsonWriter.KvBool(sb, "is_foreground", IsForeground); sb.Append(',');
            JsonWriter.Kv(sb, "push_token", PushToken); sb.Append(',');
            JsonWriter.Kv(sb, "external_device_id", ExternalDeviceId); sb.Append(',');
            JsonWriter.KvBool(sb, "ff_coppa", Coppa); sb.Append(',');
            JsonWriter.KvBool(sb, "third_party_sharing", ThirdPartySharing); sb.Append(',');
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
            sb.Append(",\"partner_params\":");
            WriteProps(sb, PartnerParams);

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
            JsonWriter.Kv(sb, "device_type", d.DeviceType); sb.Append(',');
            JsonWriter.Kv(sb, "os_build", d.OsBuild); sb.Append(',');
            JsonWriter.Kv(sb, "hardware_name", d.HardwareName); sb.Append(',');
            JsonWriter.Kv(sb, "screen_size", d.ScreenSize); sb.Append(',');
            JsonWriter.Kv(sb, "screen_format", d.ScreenFormat); sb.Append(',');
            JsonWriter.Kv(sb, "ui_mode", d.UiMode); sb.Append(',');
            JsonWriter.KvBool(sb, "is_system_app", d.IsSystemApp); sb.Append(',');
            JsonWriter.Kv(sb, "app_set_id", d.AppSetId); sb.Append(',');
            JsonWriter.Kv(sb, "gaid_source", d.GaidSource); sb.Append(',');
            JsonWriter.KvNum(sb, "gaid_attempt", d.GaidAttempt); sb.Append(',');
            JsonWriter.Kv(sb, "fire_adid", d.FireAdid); sb.Append(',');
            JsonWriter.KvBool(sb, "fire_tracking_enabled", d.FireTrackingEnabled); sb.Append(',');
            JsonWriter.Kv(sb, "imei", d.Imei); sb.Append(',');
            JsonWriter.Kv(sb, "meid", d.Meid); sb.Append(',');
            JsonWriter.Kv(sb, "device_id", d.DeviceId); sb.Append(',');
            JsonWriter.Kv(sb, "oaid", d.Oaid); sb.Append(',');
            JsonWriter.Kv(sb, "oaid_src", d.OaidSrc); sb.Append(',');
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
