using System;
using UnityEngine;

namespace Reflect.Internal
{
    /// <summary>
    /// Persistent per-install UUID stored in PlayerPrefs.
    /// Survives reinstalls only if the user's OS preserves app data — by design the
    /// <c>first_install_time</c> from the OS is paired with this on the server to
    /// detect reinstalls.
    /// </summary>
    internal static class InstallUuidStore
    {
        private const string KeyUuid            = "reflect.install_uuid";
        private const string KeyInstallReported = "reflect.install_reported";

        private static string _cached;

        public static string Value
        {
            get
            {
                if (!string.IsNullOrEmpty(_cached)) return _cached;
                _cached = PlayerPrefs.GetString(KeyUuid, null);
                return _cached;
            }
        }

        public static bool IsFirstLaunch => PlayerPrefs.GetInt(KeyInstallReported, 0) == 0;

        public static void EnsureGenerated()
        {
            if (!string.IsNullOrEmpty(Value)) return;
            _cached = Guid.NewGuid().ToString("D"); // 8-4-4-4-12 hex
            PlayerPrefs.SetString(KeyUuid, _cached);
            PlayerPrefs.Save();
            ReflectLogger.Info($"Generated new install_uuid: {_cached}");
        }

        public static void MarkInstallReported()
        {
            PlayerPrefs.SetInt(KeyInstallReported, 1);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// GDPR/CCPA wipe — clear UUID + first-launch flag from PlayerPrefs.
        /// After this the next Initialize() will generate a fresh UUID and
        /// fire app_install / app_first_open as if from a brand-new device.
        /// </summary>
        public static void WipeAll()
        {
            PlayerPrefs.DeleteKey(KeyUuid);
            PlayerPrefs.DeleteKey(KeyInstallReported);
            PlayerPrefs.Save();
            _cached = null;
            ReflectLogger.Info("InstallUuid wiped — fresh identity on next Initialize.");
        }
    }
}
