using System.Collections.Generic;
using Reflect.Internal;

namespace Reflect
{
    /// <summary>Snapshot of device data at the time of collection.</summary>
    public sealed class DeviceSnapshot
    {
        // Identifiers
        public string Gaid;         // Android Advertising ID
        public bool   LatEnabled;   // Android limit-ad-tracking flag
        public string Idfa;         // iOS IDFA
        public string Idfv;         // iOS IDFV
        public string AndroidId;    // SSAID fallback

        // OS / device
        public string Os;             // "Android" | "iOS" | "Editor"
        public string OsVersion;
        public int    ApiLevel;       // Android API level
        public string DeviceModel;
        public string DeviceManufacturer;
        public string DeviceBrand;
        public string CpuArch;
        public int    ScreenWidth;
        public int    ScreenHeight;
        public int    ScreenDensity;
        public long   TotalRamMb;

        // App
        public string AppBundleId;
        public string AppVersion;
        public long   AppVersionCode;
        public string InstallSource;   // e.g. "com.android.vending"
        public long   FirstInstallTime; // epoch ms
        public long   LastUpdateTime;   // epoch ms

        // Locale
        public string Language;
        public string Locale;
        public string Timezone;
        public int    TimezoneOffsetMinutes;

        // Network (client-side hints, server fills rest from IP)
        public string ConnectionType;  // "wifi" | "cellular" | "none"
        public string Carrier;
        public string CarrierMcc;
        public string CarrierMnc;

        // Fraud signals
        public bool IsEmulator;
        public bool IsRooted;
        public bool VpnDetected;
        public bool MockLocationEnabled;

        internal static DeviceSnapshot FromJson(string json)
        {
            var d = MiniJson.Deserialize(json) as IDictionary<string, object>;
            if (d == null) return null;
            var s = new DeviceSnapshot
            {
                Gaid                  = Str(d, "gaid"),
                LatEnabled            = Bool(d, "lat_enabled"),
                Idfa                  = Str(d, "idfa"),
                Idfv                  = Str(d, "idfv"),
                AndroidId             = Str(d, "android_id"),
                Os                    = Str(d, "os"),
                OsVersion             = Str(d, "os_version"),
                ApiLevel              = Int(d, "api_level"),
                DeviceModel           = Str(d, "device_model"),
                DeviceManufacturer    = Str(d, "device_manufacturer"),
                DeviceBrand           = Str(d, "device_brand"),
                CpuArch               = Str(d, "cpu_arch"),
                ScreenWidth           = Int(d, "screen_width"),
                ScreenHeight          = Int(d, "screen_height"),
                ScreenDensity         = Int(d, "screen_density"),
                TotalRamMb            = Long(d, "total_ram_mb"),
                AppBundleId           = Str(d, "app_bundle_id"),
                AppVersion            = Str(d, "app_version"),
                AppVersionCode        = Long(d, "app_version_code"),
                InstallSource         = Str(d, "install_source"),
                FirstInstallTime      = Long(d, "first_install_time"),
                LastUpdateTime        = Long(d, "last_update_time"),
                Language              = Str(d, "language"),
                Locale                = Str(d, "locale"),
                Timezone              = Str(d, "timezone"),
                TimezoneOffsetMinutes = Int(d, "tz_offset_min"),
                ConnectionType        = Str(d, "connection_type"),
                Carrier               = Str(d, "carrier"),
                CarrierMcc            = Str(d, "carrier_mcc"),
                CarrierMnc            = Str(d, "carrier_mnc"),
                IsEmulator            = Bool(d, "is_emulator"),
                IsRooted              = Bool(d, "is_rooted"),
                VpnDetected           = Bool(d, "vpn_detected"),
                MockLocationEnabled   = Bool(d, "mock_location_enabled")
            };
            return s;
        }

        private static string Str(IDictionary<string, object> d, string k)
            => d.TryGetValue(k, out var v) && v != null ? v.ToString() : null;
        private static bool Bool(IDictionary<string, object> d, string k)
            => d.TryGetValue(k, out var v) && v is bool b && b;
        private static int Int(IDictionary<string, object> d, string k)
        {
            if (!d.TryGetValue(k, out var v) || v == null) return 0;
            if (v is long l) return (int)l;
            if (v is double dd) return (int)dd;
            int r; return int.TryParse(v.ToString(), out r) ? r : 0;
        }
        private static long Long(IDictionary<string, object> d, string k)
        {
            if (!d.TryGetValue(k, out var v) || v == null) return 0L;
            if (v is long l) return l;
            if (v is double dd) return (long)dd;
            long r; return long.TryParse(v.ToString(), out r) ? r : 0L;
        }
    }
}
