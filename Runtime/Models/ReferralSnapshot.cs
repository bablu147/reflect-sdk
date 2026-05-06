using System.Collections.Generic;
using Reflect.Internal;

namespace Reflect
{
    /// <summary>
    /// Referral / attribution data at install time.
    /// Android fields populated by Play Install Referrer API.
    /// iOS fields populated by AdServices attribution token (iOS 14.3+).
    /// </summary>
    public sealed class ReferralSnapshot
    {
        // Android (Play Install Referrer)
        public string Raw;                       // "click_id=abc&pub=xyz"
        public long   ReferrerClickTs;           // UNIX seconds (device)
        public long   InstallBeginTs;            // UNIX seconds (device)
        public long   ReferrerClickServerTs;     // UNIX seconds (Google-verified)
        public long   InstallBeginServerTs;      // UNIX seconds (Google-verified)
        public bool   GooglePlayInstant;

        // iOS (AdServices.framework)
        public string AttributionToken;          // Opaque token; server calls Apple to resolve
        public string Source;                    // e.g. "play_install_referrer" | "ios_adservices"

        // Parsed key-value map for quick access (from Raw on Android)
        public Dictionary<string, string> ParsedParams;

        internal static ReferralSnapshot FromJson(string json)
        {
            var d = MiniJson.Deserialize(json) as IDictionary<string, object>;
            if (d == null) return null;
            var s = new ReferralSnapshot
            {
                Raw                    = Str(d, "raw"),
                ReferrerClickTs        = Long(d, "click_ts"),
                InstallBeginTs         = Long(d, "install_ts"),
                ReferrerClickServerTs  = Long(d, "click_server_ts"),
                InstallBeginServerTs   = Long(d, "install_server_ts"),
                GooglePlayInstant      = Bool(d, "google_play_instant"),
                AttributionToken       = Str(d, "attribution_token"),
                Source                 = Str(d, "source")
            };
            s.ParsedParams = ParseQuery(s.Raw);
            return s;
        }

        private static Dictionary<string, string> ParseQuery(string raw)
        {
            var map = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(raw)) return map;
            foreach (var pair in raw.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq < 1) continue;
                var k = System.Uri.UnescapeDataString(pair.Substring(0, eq));
                var v = System.Uri.UnescapeDataString(pair.Substring(eq + 1));
                map[k] = v;
            }
            return map;
        }

        private static string Str(IDictionary<string, object> d, string k)
            => d.TryGetValue(k, out var v) && v != null ? v.ToString() : null;
        private static bool Bool(IDictionary<string, object> d, string k)
            => d.TryGetValue(k, out var v) && v is bool b && b;
        private static long Long(IDictionary<string, object> d, string k)
        {
            if (!d.TryGetValue(k, out var v) || v == null) return 0L;
            if (v is long l) return l;
            if (v is double dd) return (long)dd;
            long r; return long.TryParse(v.ToString(), out r) ? r : 0L;
        }
    }
}
