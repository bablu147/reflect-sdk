// ────────────────────────────────────────────────────────────────────────────
//  Reflect SDK for Unity — designed and built by Bablu.
//  Mobile measurement / attribution SDK.
//
//  THIN WRAPPER over the shared native core (com.reflect.core.ReflectCore /
//  ReflectCore.swift, shipped as the reflect-android AAR + Plugins/iOS Swift).
//  ALL SDK logic — envelope, queue, gzip, HMAC signing, sessions, attribution,
//  deep links, device collection — lives in the core. This layer only:
//    • preserves the public C# API byte-for-byte,
//    • serializes each call's args + dispatches via IPlatformBridge.Call, and
//    • marshals the core's UnitySendMessage callbacks back into C# callbacks/events.
//  See README.md §10 for credits & license.
// ────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using Reflect.Internal;
using Reflect.Internal.Platform;
using UnityEngine;

namespace Reflect
{
    /// <summary>
    /// Public API for the Reflect SDK. Call <see cref="Initialize(string)"/> once at
    /// app start, then track events. Named <c>ReflectSDK</c> (not <c>Reflect</c>) so
    /// it doesn't collide with the enclosing <c>Reflect</c> namespace.
    /// </summary>
    public static class ReflectSDK
    {
        private static bool _initialized;
        private static ReflectConfig _config;
        private static IPlatformBridge _platform;
        private static ReflectCallbackReceiver _receiver;
        private static bool _isDebugMode;

        // Sync-getter caches. The native core is the source of truth; these are
        // populated at init (+ on setters) so the public SYNCHRONOUS API
        // (InstallUuid / UserId / GetConsent / LastDeepLink) is preserved without an
        // async round-trip per read.
        private static string _installUuid;
        private static string _userId;
        private static string _consentState = "granted";
        private static DeepLinkData _lastDeepLink;

        // Async results: each result-bearing Call registers a continuation keyed by an
        // incrementing id; the core replies on OnCallResult tagged with that id.
        private static int _cbSeq;
        private static readonly Dictionary<string, Action<bool, object, string>> _pending =
            new Dictionary<string, Action<bool, object, string>>();

        private static event Action<DeepLinkData> _onDeepLink;

        /// <summary>
        /// Fired when the server reports a new/changed attribution. Keys:
        /// attribution_type, partner_slug, campaign_name, click_id, attributed_at_ms.
        /// Subscribe before <see cref="Initialize(ReflectConfig)"/> to catch the first.
        /// </summary>
        public static event Action<Dictionary<string, object>> OnAttributionUpdated;

        /// <summary>Deep links received while running (cold / warm / deferred). A new
        /// subscriber is immediately replayed the last deep link if one already fired.</summary>
        public static event Action<DeepLinkData> OnDeepLink
        {
            add { _onDeepLink += value; if (_lastDeepLink != null) value?.Invoke(_lastDeepLink); }
            remove { _onDeepLink -= value; }
        }

        /// <summary>True when initialized with a null/empty BaseUrl — local, no network.</summary>
        public static bool IsDebugMode => _isDebugMode;

        /// <summary>True once <see cref="Initialize(ReflectConfig)"/> has succeeded.</summary>
        public static bool IsInitialized => _initialized;

        /// <summary>The install UUID. Null until the core reports it shortly after init.</summary>
        public static string InstallUuid => _installUuid;

        /// <summary>The current user ID, or null (anonymous).</summary>
        public static string UserId => _userId;

        /// <summary>The most recent deep link, or null.</summary>
        public static DeepLinkData LastDeepLink => _lastDeepLink;

        // ───────────────────────── Initialization ─────────────────────────

        public static void Initialize(string baseUrl)
            => Initialize(new ReflectConfig { BaseUrl = baseUrl });

        public static void Initialize(ReflectConfig config)
        {
            if (_initialized)
            {
                ReflectLogger.Warn("ReflectSDK.Initialize called more than once — ignoring.");
                return;
            }
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.Validate();

            _config = config;
            _isDebugMode = config.IsDebugMode;
            ReflectLogger.Enabled = config.EnableLogging || config.IsOverlayEnabled;

            // Seed the consent cache the same way the core derives its initial posture,
            // so a synchronous GetConsent right after Initialize is correct before the
            // core's async getConsent refresh lands.
            string legacyConsent = PlayerPrefs.GetString("reflect_consent_state", null);
            _consentState = !string.IsNullOrEmpty(legacyConsent)
                ? legacyConsent : (config.RequireConsent ? "denied" : "granted");

            _platform = PlatformBridgeFactory.Create();
            _receiver = ReflectCallbackReceiver.Ensure();

            // Auto-capture unhandled exceptions as a `_crash` event (the core throttles
            // to 1/min, so no C# throttle is needed).
            if (config.AutoCaptureCrashes)
                Application.logMessageReceived += OnUnityLogMessage;

            // Construct the core + run initialize() (config carries the migration-continuity
            // args existingInstallUuid + initialConsent read from the legacy stores).
            _platform.Initialize(_receiver.gameObject.name, config.ToJson());
            _initialized = true;

            // Refresh the sync caches from the core (source of truth).
            CallResult("getInstallUuid", null, (ok, v, e) => { if (ok) _installUuid = v as string; });
            CallResult("getConsent", null, (ok, v, e) => { if (ok && v is string s) _consentState = s; });

            // Carry legacy runtime toggles (tps / advertising consent / enabled) into the
            // core ONCE on the first migrated launch so a user's prior opt-outs survive.
            ReplayLegacyTogglesOnce();

            ReflectLogger.Info("Reflect initialized.");
        }

        // ───────────────────────── Event tracking ─────────────────────────

        public static void TrackEvent(string name)
            => CallFF("trackEvent", Args("eventName", name));

        public static void TrackEvent(string name, IDictionary<string, object> props)
        {
            var a = Args("eventName", name);
            if (props != null) a["properties"] = JsonWriter.Serialize(props);
            CallFF("trackEvent", a);
        }

        public static void TrackEvent(string name, ReflectEventOptions options)
        {
            var a = Args("eventName", name);
            if (options != null)
            {
                if (options.Properties != null) a["properties"] = JsonWriter.Serialize(options.Properties);
                if (options.PartnerParams != null) a["partnerParams"] = JsonWriter.Serialize(options.PartnerParams);
                if (options.CallbackParams != null) a["callbackParams"] = JsonWriter.Serialize(options.CallbackParams);
                if (!string.IsNullOrEmpty(options.DeduplicationId)) a["deduplicationId"] = options.DeduplicationId;
                if (!string.IsNullOrEmpty(options.CallbackId)) a["callbackId"] = options.CallbackId;
                // R2 — revenue carried on an arbitrary event. The core's trackEvent reads
                // optional revenue/currency so this isn't silently dropped.
                if (options.Revenue.HasValue) a["revenue"] = options.Revenue.Value;
                if (!string.IsNullOrEmpty(options.Currency)) a["currency"] = options.Currency;
            }
            CallFF("trackEvent", a);
        }

        public static void TrackPurchase(string productId, double price, string currencyCode,
            string transactionId, IDictionary<string, object> extraProps = null,
            string receiptData = null, string purchaseToken = null, string orderId = null)
        {
            var a = Args("productId", productId);
            a["price"] = price;
            a["currency"] = currencyCode;                       // currencyCode → currency
            if (!string.IsNullOrEmpty(transactionId)) a["transactionId"] = transactionId;
            if (!string.IsNullOrEmpty(receiptData)) a["receiptData"] = receiptData;
            if (!string.IsNullOrEmpty(purchaseToken)) a["purchaseToken"] = purchaseToken;
            if (!string.IsNullOrEmpty(orderId)) a["orderId"] = orderId;
            if (extraProps != null) a["extraProperties"] = AsObjectMap(extraProps);   // nested map (not a json string)
            CallFF("trackPurchase", a);
        }

        public static void TrackSubscription(string productId, double price, string currencyCode,
            string transactionId, bool isTrial, IDictionary<string, object> extraProps = null,
            string receiptData = null, string purchaseToken = null, string orderId = null)
        {
            var a = Args("productId", productId);
            a["price"] = price;
            a["currency"] = currencyCode;
            a["isTrial"] = isTrial;
            if (!string.IsNullOrEmpty(transactionId)) a["transactionId"] = transactionId;
            if (!string.IsNullOrEmpty(receiptData)) a["receiptData"] = receiptData;
            if (!string.IsNullOrEmpty(purchaseToken)) a["purchaseToken"] = purchaseToken;
            if (!string.IsNullOrEmpty(orderId)) a["orderId"] = orderId;
            if (extraProps != null) a["extraProperties"] = AsObjectMap(extraProps);
            CallFF("trackSubscription", a);
        }

        public static void TrackAdRevenue(string mediationPlatform, double revenue, string currency,
            string adFormat = null, string adNetwork = null, string adUnitId = null,
            string placement = null, string precision = "estimated", int impressionsCount = 1)
        {
            // R3 — the core's arg keys are renamed; a wrong key silently drops the field.
            var a = new Dictionary<string, object>
            {
                ["source"] = mediationPlatform,        // mediationPlatform → source
                ["revenue"] = revenue,
                ["currency"] = currency,
                ["impressions"] = impressionsCount,    // impressionsCount → impressions
                ["precision"] = precision,
            };
            if (!string.IsNullOrEmpty(adFormat)) a["adFormat"] = adFormat;
            if (!string.IsNullOrEmpty(adNetwork)) a["adNetwork"] = adNetwork;
            if (!string.IsNullOrEmpty(adUnitId)) a["adUnit"] = adUnitId;        // adUnitId → adUnit
            if (!string.IsNullOrEmpty(placement)) a["adPlacement"] = placement; // placement → adPlacement
            CallFF("trackAdRevenue", a);
        }

        // ───────────────────────── Purchase verification ──────────────────

        public static void VerifyPurchase(string productId, string transactionId,
            string purchaseToken, string receiptData, Action<ReflectVerificationResult> callback)
        {
            var a = Args("productId", productId);
            if (!string.IsNullOrEmpty(transactionId)) a["transactionId"] = transactionId;
            if (!string.IsNullOrEmpty(purchaseToken)) a["purchaseToken"] = purchaseToken;
            if (!string.IsNullOrEmpty(receiptData)) a["receiptData"] = receiptData;
            CallResult("verifyPurchase", a, (ok, v, e) => callback?.Invoke(ParseVerification(v)));
        }

        public static void VerifyAndTrackPurchase(string productId, double price, string currencyCode,
            string transactionId, string receiptData = null, string purchaseToken = null,
            string orderId = null, Action<ReflectVerificationResult> callback = null)
        {
            // Composed C#-side (same as the Flutter wrapper): verify, then track the
            // purchase annotated with the outcome. The purchase is tracked regardless.
            VerifyPurchase(productId, transactionId, purchaseToken, receiptData, result =>
            {
                var extra = new Dictionary<string, object>
                {
                    ["verification_status"] = result != null ? result.Status.ToString() : "Unknown",
                };
                TrackPurchase(productId, price, currencyCode, transactionId, extra, receiptData, purchaseToken, orderId);
                callback?.Invoke(result);
            });
        }

        // ───────────────────────── Identity / consent / toggles ───────────

        public static void SetUserId(string userId)
        {
            _userId = userId;   // cache (the core itself emits the `_user_alias` stitch — do NOT emit it here)
            if (string.IsNullOrEmpty(userId)) CallFF("clearUserId", null);
            else CallFF("setUserId", Args("userId", userId));
        }

        public static void SetPushToken(string token)
            => CallFF("setPushToken", Args("token", token));

        public static void SetExternalDeviceId(string externalId)
            => CallFF("setExternalDeviceId", Args("externalDeviceId", externalId));

        public static void SetEnabled(bool enabled)
            => CallFF("setEnabled", Args("enabled", enabled));

        public static void SetThirdPartySharing(bool granted)
            => CallFF("setThirdPartySharing", Args("enabled", granted));   // granted → enabled

        public static void SetPartnerSharing(string partner, string key, bool value)
        {
            var a = Args("partner", partner);
            a["key"] = key; a["value"] = value;
            CallFF("setPartnerSharing", a);
        }

        public static void SetOfflineMode(bool offline)
            => CallFF("setOfflineMode", Args("offline", offline));

        public static void AddGlobalPartnerParameter(string key, string value)
        {
            if (value == null) { RemoveGlobalPartnerParameter(key); return; }
            var a = Args("key", key); a["value"] = value;
            CallFF("setPartnerParameter", a);
        }

        public static void RemoveGlobalPartnerParameter(string key)
            => CallFF("unsetPartnerParameter", Args("key", key));

        public static void SetGlobalProperty(string key, object value)
        {
            if (value == null) { UnsetGlobalProperty(key); return; }
            var a = Args("key", key); a["value"] = value;
            CallFF("setGlobalProperty", a);
        }

        public static void UnsetGlobalProperty(string key)
            => CallFF("unsetGlobalProperty", Args("key", key));

        public static void ClearGlobalProperties()
            => CallFF("clearGlobalProperties", null);

        public static void SetConsent(bool granted)
        {
            _consentState = granted ? "granted" : "denied";
            PlayerPrefs.SetString("reflect_consent_state", _consentState);   // keep the C# cache durable across launches
            CallFF("setConsent", Args("granted", granted));
        }

        public static string GetConsent() => _consentState;

        public static void SetAdvertisingConsent(bool granted)
            => CallFF("setAdvertisingConsent", Args("granted", granted));

        // ───────────────────────── iOS ATT ────────────────────────────────

        public static void RequestIosTracking(Action<IosTrackingStatus> callback)
        {
            CallResult("requestIosTracking", null, (ok, v, e) =>
                callback?.Invoke(ParseAttStatus(v as string)));
        }

        // ───────────────────────── Deep linking ───────────────────────────

        public static void GetLastDeeplink(Action<DeepLinkData> callback)
            => callback?.Invoke(_lastDeepLink);

        public static void ResolveDeepLink(string url, Action<string> onResolved)
            => CallResult("resolveDeepLink", Args("url", url), (ok, v, e) => onResolved?.Invoke(v as string));

        public static void HandleDeepLink(string url, bool isCold = false)
            => CallFF("handleDeepLink", Args("url", url));   // R8 — core has no cold/warm param

        // ───────────────────────── Attribution query ──────────────────────

        public static void GetAttribution(Action<Dictionary<string, object>> callback)
            => CallResult("getAttribution", null, (ok, v, e) => callback?.Invoke(AsDict(v)));

        public static void GetAttributionWithTimeout(float timeoutSeconds, Action<Dictionary<string, object>> callback)
        {
            var a = new Dictionary<string, object> { ["timeoutMs"] = (int)(timeoutSeconds * 1000f) };
            CallResult("getAttributionWithTimeout", a, (ok, v, e) => callback?.Invoke(AsDict(v)));
        }

        // ───────────────────────── Audience / SKAN / push / privacy / flush ─

        public static void SetAudience(params string[] tags)
        {
            var list = new List<object>();
            if (tags != null) foreach (var t in tags) list.Add(t);
            CallFF("setAudience", new Dictionary<string, object> { ["tags"] = list });
        }

        public static void UpdateConversionValue(int fineValue, string coarseValue = null,
            bool lockWindow = false, Action<bool, string> onComplete = null)
        {
            // Client-side range guard (Dart/Flutter parity — fail fast without a round-trip).
            if (fineValue < 0 || fineValue > 63) { onComplete?.Invoke(false, "fine_value_out_of_range"); return; }
            var a = new Dictionary<string, object>
            {
                ["fineValue"] = fineValue,
                ["lockWindow"] = lockWindow,
            };
            if (!string.IsNullOrEmpty(coarseValue)) a["coarseValue"] = coarseValue;
            CallResult("updateConversionValue", a, (ok, v, e) =>
            {
                var d = AsDict(v);
                bool success = d != null && d.TryGetValue("success", out var s) && s is bool b && b;
                string method = d != null && d.TryGetValue("method", out var m) ? m as string : null;
                string err = d != null && d.TryGetValue("error", out var er) ? er as string : e;
                onComplete?.Invoke(success, success ? method : err);
            });
        }

        public static void RegisterPushToken(string token, string provider = null)
        {
            var a = Args("token", token);
            if (!string.IsNullOrEmpty(provider)) a["provider"] = provider;
            CallFF("registerPushToken", a);
        }

        public static void DeleteUserData(Action<bool> onComplete = null)
        {
            CallResult("deleteUserData", null, (ok, v, e) =>
            {
                // Also wipe the legacy Unity stores so a later re-init can't re-adopt the
                // old identity (the core wipes its own store).
                PlayerPrefs.DeleteKey("reflect.install_uuid");
                PlayerPrefs.DeleteKey("reflect.install_reported");
                PlayerPrefs.DeleteKey("reflect_consent_state");
                _installUuid = null;
                onComplete?.Invoke(v is bool b ? b : ok);
            });
        }

        public static void Flush() => CallFF("flush", null);

        // ───────────────────────── Native → C# callbacks ──────────────────

        internal static void HandleCallResult(string json)
        {
            try
            {
                if (!(MiniJson.Deserialize(json) is Dictionary<string, object> d)) return;
                string id = d.TryGetValue("id", out var idv) ? idv as string : null;
                if (string.IsNullOrEmpty(id)) return;
                if (!_pending.TryGetValue(id, out var cb)) return;
                _pending.Remove(id);
                bool ok = d.TryGetValue("ok", out var okv) && okv is bool b && b;
                d.TryGetValue("value", out var value);
                string err = d.TryGetValue("error", out var ev) ? ev as string : null;
                cb?.Invoke(ok, value, err);
            }
            catch (Exception ex) { ReflectLogger.Error($"OnCallResult parse failed: {ex}"); }
        }

        internal static void HandleDeepLinkPayload(string json)
        {
            try
            {
                var d = AsDict(MiniJson.Deserialize(json));
                if (d == null) return;
                var data = new DeepLinkData
                {
                    Url = Str(d, "url"),
                    Path = Str(d, "path"),
                    PartnerSlug = Str(d, "partner"),
                    Source = ParseDeepLinkSource(d),
                };
                if (string.IsNullOrEmpty(data.Path) && !string.IsNullOrEmpty(data.Url))
                    data.Path = ExtractPath(data.Url);
                _lastDeepLink = data;
                _onDeepLink?.Invoke(data);
            }
            catch (Exception ex) { ReflectLogger.Error($"OnDeepLink parse failed: {ex}"); }
        }

        internal static void HandleAttributionPayload(string json)
        {
            try
            {
                var d = AsDict(MiniJson.Deserialize(json));
                if (d != null) OnAttributionUpdated?.Invoke(d);
            }
            catch (Exception ex) { ReflectLogger.Error($"OnAttribution parse failed: {ex}"); }
        }

        // ───────────────────────── helpers ────────────────────────────────

        private static void CallFF(string method, Dictionary<string, object> args)
        {
            if (!EnsureReady()) return;
            _platform.Call(method, args == null ? "{}" : JsonWriter.Serialize(args), "");
        }

        private static void CallResult(string method, Dictionary<string, object> args, Action<bool, object, string> cb)
        {
            if (!EnsureReady()) { cb?.Invoke(false, null, "not_initialized"); return; }
            string id = (++_cbSeq).ToString();
            _pending[id] = cb;
            _platform.Call(method, args == null ? "{}" : JsonWriter.Serialize(args), id);
        }

        private static bool EnsureReady()
        {
            if (_initialized && _platform != null) return true;
            ReflectLogger.Warn("ReflectSDK used before Initialize — call ReflectSDK.Initialize first.");
            return false;
        }

        private static Dictionary<string, object> Args(string key, object value)
            => new Dictionary<string, object> { [key] = value };

        // Re-wrap an arbitrary IDictionary into a Dictionary<string,object> so JsonWriter
        // emits it as a nested JSON object (extraProperties path).
        private static Dictionary<string, object> AsObjectMap(IDictionary<string, object> src)
        {
            var m = new Dictionary<string, object>(src.Count);
            foreach (var kv in src) m[kv.Key] = kv.Value;
            return m;
        }

        // A core return value may arrive as a parsed Dictionary OR as a JSON string
        // (cores differ: e.g. getAttribution returns a JSON string, verifyPurchase a map).
        private static Dictionary<string, object> AsDict(object v)
        {
            if (v is Dictionary<string, object> d) return d;
            if (v is string s && !string.IsNullOrEmpty(s))
                return MiniJson.Deserialize(s) as Dictionary<string, object>;
            return null;
        }

        private static string Str(Dictionary<string, object> d, string key)
            => d != null && d.TryGetValue(key, out var v) ? v as string : null;

        private static ReflectVerificationResult ParseVerification(object v)
        {
            var d = AsDict(v);
            if (d == null) return new ReflectVerificationResult(ReflectVerificationStatus.Unknown, 0, "no_result");
            string status = Str(d, "status");
            int code = d.TryGetValue("code", out var cv) && cv != null ? Convert.ToInt32(cv) : 0;
            string message = Str(d, "message");
            ReflectVerificationStatus s;
            switch (status)
            {
                case "verified": s = ReflectVerificationStatus.Verified; break;
                case "not_verified": s = ReflectVerificationStatus.NotVerified; break;
                case "failed": s = ReflectVerificationStatus.Failed; break;
                default: s = ReflectVerificationStatus.Unknown; break;
            }
            return new ReflectVerificationResult(s, code, message);
        }

        private static IosTrackingStatus ParseAttStatus(string s)
        {
            switch (s)
            {
                case "authorized": return IosTrackingStatus.Authorized;
                case "denied": return IosTrackingStatus.Denied;
                case "restricted": return IosTrackingStatus.Restricted;
                case "not_determined": return IosTrackingStatus.NotDetermined;
                default: return IosTrackingStatus.Unavailable;
            }
        }

        private static DeepLinkSource ParseDeepLinkSource(Dictionary<string, object> d)
        {
            // Prefer the explicit isDeferred flag; else the `source` string from gap-14.
            if (d.TryGetValue("isDeferred", out var dv) && dv is bool b && b) return DeepLinkSource.Deferred;
            string src = Str(d, "source");
            if (src == "deferred") return DeepLinkSource.Deferred;
            if (src == "cold") return DeepLinkSource.Cold;
            return DeepLinkSource.Warm;
        }

        private static string ExtractPath(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                int scheme = url.IndexOf("://", StringComparison.Ordinal);
                int start = scheme >= 0 ? url.IndexOf('/', scheme + 3) : 0;
                if (start < 0) return "/";
                int q = url.IndexOf('?', start);
                return q >= 0 ? url.Substring(start, q - start) : url.Substring(start);
            }
            catch { return null; }
        }

        private static void ReplayLegacyTogglesOnce()
        {
            const string migratedKey = "reflect_migrated_v2";
            if (PlayerPrefs.GetInt(migratedKey, 0) == 1) return;
            PlayerPrefs.SetInt(migratedKey, 1);
            // third-party sharing opt-out
            if (PlayerPrefs.HasKey("reflect_third_party_sharing") && PlayerPrefs.GetInt("reflect_third_party_sharing", 1) == 0)
                CallFF("setThirdPartySharing", Args("enabled", false));
            // advertising-ID consent
            if (PlayerPrefs.HasKey("reflect_ad_consent"))
                CallFF("setAdvertisingConsent", Args("granted", PlayerPrefs.GetInt("reflect_ad_consent", 0) == 1));
            // enabled/disabled (a prior SetEnabled(false))
            if (PlayerPrefs.HasKey("reflect_enabled") && PlayerPrefs.GetInt("reflect_enabled", 1) == 0)
                CallFF("setEnabled", Args("enabled", false));
            PlayerPrefs.Save();
        }

        private static void OnUnityLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Assert) return;
            var props = new Dictionary<string, object>
            {
                ["message"] = condition,
                ["stack"] = stackTrace,
                ["fatal"] = true,
            };
            TrackEvent("_crash", props);
        }
    }
}
