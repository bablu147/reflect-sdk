// ────────────────────────────────────────────────────────────────────────────
//  Reflect SDK for Unity — designed and built by Bablu.
//  Mobile measurement / attribution SDK with zero third-party dependencies.
//  See README.md §10 for credits & license.
// ────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Reflect.Internal;
using Reflect.Internal.Debug;
using Reflect.Internal.Platform;
using UnityEngine;
using UnityEngine.Networking;

namespace Reflect
{
    /// <summary>
    /// Public API for the Reflect SDK.
    /// Call <see cref="Initialize(string)"/> once at app start, then use
    /// <see cref="TrackEvent(string, IDictionary{string, object})"/> to log events.
    ///
    /// Named <c>ReflectSDK</c> (not <c>Reflect</c>) so the class doesn't
    /// collide with the enclosing <c>Reflect</c> namespace in user code.
    /// </summary>
    public static class ReflectSDK
    {
        private static bool _initialized;
        private static ReflectConfig _config;
        private static IPlatformBridge _platform;
        private static EventQueue _queue;
        private static HttpDispatcher _dispatcher;
        private static ReflectCallbackReceiver _receiver;
        private static ReflectDebugOverlay _overlay;
        private static string _userId;
        private static DeviceSnapshot _deviceSnapshot;
        private static ReferralSnapshot _referralSnapshot;
        // Captured once at Initialize on the main thread (UnityEngine.Application
        // must be read from the main thread). Pure-C# source for app_version so it
        // survives even if the native device collector is stripped/stalls.
        private static string _appVersion;
        // Adjust-parity envelope signals (pure C#). is_foreground tracks lifecycle;
        // push_token is set by the host app via SetPushToken (no FCM/APNS dependency
        // bundled — the app already owns its token).
        private static bool   _isForeground = true;
        private static string _pushToken;
        private static string _externalDeviceId;
        private static bool   _enabled = true;            // Adjust: setEnabled (pause/resume all tracking)
        private static bool   _thirdPartySharing = true;  // Adjust: third-party sharing opt-in
        // Start of the current foreground session (epoch ms) so session_end can
        // report session_length_ms — feeds aggregates_sessions.total_active_ms.
        private static long   _sessionStartMs;
        private static IosTrackingStatus _attStatus = IosTrackingStatus.NotDetermined;
        private static bool _advertisingConsent = true;
        private static string _consentState = "granted";
        private static readonly Dictionary<string, object> _globalProps = new Dictionary<string, object>();
        private static readonly object _globalPropsLock = new object();
        // Adjust parity: partner_params — forwarded to ad-network partners, kept
        // separate from callback/global props.
        private static readonly Dictionary<string, object> _partnerParams = new Dictionary<string, object>();
        private static readonly object _partnerParamsLock = new object();

        // ── Attribution check state (Sprint I) ──────────────────────────
        private static bool _attributionCheckedThisSession;
        private static long _lastAttributionCheckMs;
        private const string ATTRIBUTION_CHECK_PREFS_KEY = "reflect_last_attribution_check_ms";

        /// <summary>
        /// Fired when the server reports a new or changed attribution for this
        /// install. The dictionary contains keys: attribution_type, partner_slug,
        /// campaign_name, click_id, attributed_at_ms. Subscribe before calling
        /// <see cref="Initialize(ReflectConfig)"/> to catch the first callback.
        /// </summary>
        public static event Action<Dictionary<string, object>> OnAttributionUpdated;

        /// <summary>True when initialized with a null/empty BaseUrl — the SDK runs
        /// locally with the developer overlay and never makes network requests.</summary>
        public static bool IsDebugMode => _initialized && _config != null && _config.IsDebugMode;

        /// <summary>True if <see cref="Initialize(ReflectConfig)"/> has been called successfully.</summary>
        public static bool IsInitialized => _initialized;

        /// <summary>The install UUID assigned to this device. Null until initialized.</summary>
        public static string InstallUuid => _initialized ? InstallUuidStore.Value : null;

        /// <summary>The currently-set user ID, or null if not set (anonymous).</summary>
        public static string UserId => _userId;

        // ───────────────────────── Initialization ─────────────────────────

        /// <summary>Simple init — baseUrl only.</summary>
        public static void Initialize(string baseUrl)
            => Initialize(new ReflectConfig { BaseUrl = baseUrl });

        /// <summary>Full init with a configuration object.</summary>
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
            // Capture app version on the main thread (Application.* is main-thread
            // only). Pure C# so it's attached to every event — including the
            // first-launch app_install — regardless of native device collection.
            _appVersion = Application.version;
            // Any time the overlay is on we force logging so its Logs tab is useful.
            ReflectLogger.Enabled = config.EnableLogging || config.IsOverlayEnabled;

            try
            {
                _platform = PlatformBridgeFactory.Create();
                _receiver = ReflectCallbackReceiver.Ensure();
                _queue = new EventQueue(config.MaxQueueSize);
                _dispatcher = new HttpDispatcher(config, _queue);

                _receiver.OnDeviceInfoReadyHandler = OnDeviceInfoReady;
                _receiver.OnReferralReadyHandler   = OnReferralReady;
                _receiver.OnAttStatusHandler       = OnAttStatus;
                _receiver.OnPauseHandler           = OnApplicationPauseInternal;
                _receiver.OnTickHandler            = OnTick;

                if (config.IsOverlayEnabled) AttachDebugOverlay();

                InstallUuidStore.EnsureGenerated();

                _advertisingConsent = !config.RequireAdvertisingConsent;

                // Kick off async native collection.
                _platform.Initialize(_receiver.gameObject.name, _advertisingConsent, config.CollectImei, config.CollectOaid);
                _platform.CollectDeviceInfo();
                _platform.CollectReferral();

                // Auto-capture unhandled exceptions as a `_crash` event so devs
                // see crash rates by app version without bolting on a separate
                // SDK. Cheap — one event per crash, piggybacks on /event ingest.
                if (config.AutoCaptureCrashes)
                {
                    Application.logMessageReceived += OnUnityLogMessage;
                }

                _initialized = true;
                ReflectLogger.Info(SdkVersion.FullTag);
                if (config.IsDebugMode)
                {
                    ReflectLogger.Warn("DEBUG MODE — no BaseUrl set. Events are captured locally but NOT dispatched. " +
                                       "Tap the floating R button on-screen to open the developer overlay.");
                }
                else if (config.EnableDebugOverlay)
                {
                    ReflectLogger.Warn("INSPECTION MODE — events ARE being dispatched AND a floating R button is visible " +
                                       "on-screen. Keep this OFF in release builds (gate on Debug.isDebugBuild).");
                }
                ReflectLogger.Info($"Reflect initialized — baseUrl={(config.IsDebugMode ? "(debug / none)" : config.BaseUrl)}, installUuid={InstallUuidStore.Value}");

                // Load persisted consent state from PlayerPrefs.
                if (PlayerPrefs.HasKey("reflect_consent_state"))
                {
                    _consentState = PlayerPrefs.GetString("reflect_consent_state", "granted");
                }

                if (InstallUuidStore.IsFirstLaunch)
                {
                    // Actual app_install event will be fired once device info + referral arrive.
                    _receiver.PendingInstallEvent = true;
                    // Backstop: if native device/referral collection stalls or was
                    // stripped from a release build, force-fire app_install after
                    // the configured timeout so an install is never black-holed.
                    _receiver.StartCoroutine(InstallTimeoutCo(config.InstallEventTimeoutSeconds));

                    // Deferred deep link: ask the server to fingerprint-match this
                    // install to a recent click's deep_link_path (covers iOS /
                    // probabilistic / referrer-less installs the `dl` param misses).
                    if (config.AutoResolveDeferredDeepLink && !config.IsDebugMode)
                    {
                        _receiver.StartCoroutine(_dispatcher.ResolveDeferredDeepLink(
                            config.AppKey, InstallUuidStore.Value, path =>
                            {
                                if (!string.IsNullOrEmpty(path))
                                    DispatchDeepLink(new DeepLinkData
                                    {
                                        Url    = path,
                                        Path   = ExtractPath(path),
                                        Source = DeepLinkSource.Deferred,
                                    });
                            }));
                    }
                }

                if (config.AutoSessionTracking)
                {
                    _sessionStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    TrackEvent("app_open");
                }

                if (config.AutoRequestIosTracking && Application.platform == RuntimePlatform.IPhonePlayer)
                    RequestIosTracking(null);

                // Arm SKAdNetwork on launch. Apple requires the app to call
                // updatePostbackConversionValue (iOS 16.1+) / registerApp (legacy)
                // ONCE or the SKAN attribution timer never starts and SKAN-driven
                // installs are never reported. We do it automatically with an
                // initial conversion value of 0; the operator can refine it later
                // via UpdateConversionValue. No-op on Android/Editor.
                if (config.AutoRegisterSkan && !config.IsDebugMode
                    && Application.platform == RuntimePlatform.IPhonePlayer)
                {
                    _platform.UpdateSkanConversionValue(0, "", false);
                }
            }
            catch (Exception ex)
            {
                ReflectLogger.Error($"Initialize failed: {ex}");
            }
        }

        // ───────────────────────── Event tracking ─────────────────────────

        /// <summary>Track a named event with optional string/number/bool properties.</summary>
        public static void TrackEvent(string name)
            => TrackEvent(name, (IDictionary<string, object>)null);

        /// <summary>Track a named event with optional properties.</summary>
        public static void TrackEvent(string name, IDictionary<string, object> props)
        {
            if (!EnsureReady()) return;
            // Adjust parity: when disabled via SetEnabled(false), record nothing.
            if (!_enabled) return;

            // Validate + clean before we waste queue slots / R2 bytes / D1 inserts on bad data.
            // The server enforces the same limits — this just makes failures visible at dev time.
            var v = EventValidator.Validate(name, props);
            if (!v.Ok)
            {
                ReflectLogger.Warn($"TrackEvent('{name}') rejected: {v.Reason}");
                return;
            }

            // Merge in any registered global properties WITHOUT mutating the caller's dict.
            // Per-event props win over globals on key collision (so callers can override).
            var merged = MergeWithGlobals(v.CleanedProps);

            EnqueueEvent(name, merged, revenue: null, currency: null, txId: null, productId: null);
        }

        /// <summary>
        /// Convenience helper for purchase events. Price is in local currency.
        /// Optionally pass <paramref name="receiptData"/> (base64 StoreKit receipt
        /// or Play purchase token); the server will validate it against Apple/Google
        /// to flip <c>attributions.is_revenue_validated</c> on success and
        /// flag spoofed receipts as fraud.
        /// </summary>
        public static void TrackPurchase(string productId, double price, string currencyCode, string transactionId,
                                          IDictionary<string, object> extraProps = null, string receiptData = null)
        {
            if (!EnsureReady()) return;
            var props = extraProps != null ? new Dictionary<string, object>(extraProps) : new Dictionary<string, object>();
            props["product_id"]     = productId;
            props["price_local"]    = price;
            props["currency_code"]  = currencyCode;
            props["transaction_id"] = transactionId;
            if (!string.IsNullOrEmpty(receiptData)) props["receipt_data"] = receiptData;
            EnqueueEvent("purchase", MergeWithGlobals(props), price, currencyCode, transactionId, productId);
        }

        /// <summary>Convenience helper for subscription events.</summary>
        public static void TrackSubscription(string productId, double price, string currencyCode,
                                              string transactionId, bool isTrial,
                                              IDictionary<string, object> extraProps = null, string receiptData = null)
        {
            if (!EnsureReady()) return;
            var props = extraProps != null ? new Dictionary<string, object>(extraProps) : new Dictionary<string, object>();
            props["product_id"]     = productId;
            props["price_local"]    = price;
            props["currency_code"]  = currencyCode;
            props["transaction_id"] = transactionId;
            props["is_trial"]       = isTrial;
            if (!string.IsNullOrEmpty(receiptData)) props["receipt_data"] = receiptData;
            EnqueueEvent("subscribe", MergeWithGlobals(props), price, currencyCode, transactionId, productId);
        }

        // ───────────────────────── Ad revenue ────────────────────────────

        /// <summary>Track ad revenue from a mediation platform. Fires _ad_impression event.</summary>
        public static void TrackAdRevenue(string mediationPlatform, double revenue, string currency,
            string adFormat = null, string adNetwork = null, string adUnitId = null,
            string placement = null, string precision = "estimated")
        {
            if (!EnsureReady()) return;
            var props = new Dictionary<string, object>
            {
                { "mediation_platform", mediationPlatform },
                { "revenue", revenue },
                { "currency", currency ?? "USD" },
                { "precision", precision ?? "estimated" },
            };
            if (adFormat != null) props["ad_format"] = adFormat;
            if (adNetwork != null) props["ad_network"] = adNetwork;
            if (adUnitId != null) props["ad_unit_id"] = adUnitId;
            if (placement != null) props["placement"] = placement;
            TrackEvent("_ad_impression", props);
        }

        // ───────────────────────── Identity & consent ─────────────────────

        /// <summary>
        /// Associate subsequent events with a user ID. Null clears it.
        /// On the FIRST non-null SetUserId call (transition from anonymous → known)
        /// fires a single <c>_user_alias</c> event so the server can stitch the
        /// install_uuid to the new user_id. Re-issuing the same id is a no-op.
        /// </summary>
        public static void SetUserId(string userId)
        {
            if (_userId == userId) return;

            var wasAnon = _userId == null;
            _userId = userId;

            if (wasAnon && !string.IsNullOrEmpty(userId))
            {
                // Stitch event — server writes user_aliases (install_uuid -> user_id).
                // Single row per transition. Cheap.
                var props = new Dictionary<string, object>(2)
                {
                    { "user_id_new", userId },
                    { "previous_anonymous", true },
                };
                EnqueueEvent(ReflectStandardEvents.UserAlias, MergeWithGlobals(props),
                             revenue: null, currency: null, txId: null, productId: null);
            }
            ReflectLogger.Info($"UserId set to '{userId ?? "(null)"}'");
        }

        /// <summary>
        /// Set the device push notification token (FCM on Android, APNS on iOS).
        /// Pass the token your app already obtains from Firebase / APNS — the SDK
        /// does not bundle a messaging dependency. It is then attached to every
        /// event (envelope <c>push_token</c>) so partner re-engagement postbacks can
        /// reach the device. Cleared on consent denial server-side. Adjust parity:
        /// <c>Adjust.setPushToken()</c>.
        /// </summary>
        public static void SetPushToken(string token)
        {
            _pushToken = string.IsNullOrEmpty(token) ? null : token;
            ReflectLogger.Info("Push token set.");
        }

        /// <summary>
        /// Set a customer-owned external device identifier, attached to every event
        /// (envelope <c>external_device_id</c>) so you can join Reflect data to your
        /// own backend's device records. Adjust parity: <c>external_device_id</c>.
        /// </summary>
        public static void SetExternalDeviceId(string externalId)
        {
            _externalDeviceId = string.IsNullOrEmpty(externalId) ? null : externalId;
        }

        /// <summary>
        /// Enable or disable the SDK at runtime. While disabled, no events are
        /// recorded or dispatched (the install/session lifecycle resumes when
        /// re-enabled). Adjust parity: <c>Adjust.setEnabled()</c> / disable.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            ReflectLogger.Info("Reflect " + (enabled ? "enabled" : "disabled"));
        }

        /// <summary>
        /// Opt the user in/out of third-party data sharing (reported as
        /// <c>third_party_sharing</c> on every event; the server can suppress
        /// partner postbacks when false). Adjust parity:
        /// <c>trackThirdPartySharing</c>.
        /// </summary>
        public static void SetThirdPartySharing(bool granted)
        {
            _thirdPartySharing = granted;
        }

        /// <summary>
        /// Offline mode. While true the SDK keeps recording events into the
        /// persistent queue but does NOT dispatch them; turn it off to flush.
        /// Adjust parity: <c>Adjust.setOfflineMode()</c>.
        /// </summary>
        public static void SetOfflineMode(bool offline)
        {
            if (!EnsureReady()) return;
            _dispatcher.SetOffline(offline);
            if (!offline) _dispatcher.RequestFlushSoon();
            ReflectLogger.Info("Offline mode " + (offline ? "ON" : "OFF"));
        }

        /// <summary>
        /// Add a global partner parameter — a key/value forwarded to ad-network
        /// partners on every event (sent as <c>partner_params</c>, distinct from
        /// callback/global props). Pass null value to remove. Adjust parity:
        /// <c>addGlobalPartnerParameter</c>.
        /// </summary>
        public static void AddGlobalPartnerParameter(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_partnerParamsLock)
            {
                if (value == null) _partnerParams.Remove(key);
                else _partnerParams[key] = value;
            }
        }

        /// <summary>Remove a single global partner parameter. Adjust parity:
        /// <c>removeGlobalPartnerParameter</c>.</summary>
        public static void RemoveGlobalPartnerParameter(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_partnerParamsLock) { _partnerParams.Remove(key); }
        }

        private static Dictionary<string, object> SnapshotPartnerParams()
        {
            lock (_partnerParamsLock)
            {
                return _partnerParams.Count == 0
                    ? null
                    : new Dictionary<string, object>(_partnerParams);
            }
        }

        // ───────────────────────── Global event properties ─────────────────

        /// <summary>
        /// Set a global property that will be merged into every subsequent event.
        /// Per-event props in TrackEvent override globals on key collision.
        /// Set the value to null to remove the global.
        /// </summary>
        public static void SetGlobalProperty(string key, object value)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_globalPropsLock)
            {
                if (value == null) _globalProps.Remove(key);
                else _globalProps[key] = value;
            }
        }

        /// <summary>Remove a single global property.</summary>
        public static void UnsetGlobalProperty(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_globalPropsLock) { _globalProps.Remove(key); }
        }

        /// <summary>Drop all registered global properties.</summary>
        public static void ClearGlobalProperties()
        {
            lock (_globalPropsLock) { _globalProps.Clear(); }
        }

        /// <summary>
        /// Build a new dict containing globals + the per-event props (per-event wins).
        /// Returns null if there are neither globals nor per-event props.
        /// Snapshots globals under lock, then merges outside the lock to minimize
        /// contention with SetGlobalProperty calls from other threads.
        /// </summary>
        private static IDictionary<string, object> MergeWithGlobals(IDictionary<string, object> perEvent)
        {
            Dictionary<string, object> snapshot;
            lock (_globalPropsLock)
            {
                if (_globalProps.Count == 0) return perEvent;
                snapshot = new Dictionary<string, object>(_globalProps);
            }
            var merged = new Dictionary<string, object>(snapshot.Count + (perEvent?.Count ?? 0));
            foreach (var kv in snapshot) merged[kv.Key] = kv.Value;
            if (perEvent != null)
                foreach (var kv in perEvent) merged[kv.Key] = kv.Value;   // per-event overrides
            return merged;
        }

        /// <summary>
        /// Set the user's data-collection consent state. Persists across sessions
        /// via PlayerPrefs. The value is attached to every event as <c>consent_state</c>.
        /// </summary>
        /// <param name="granted">true = "granted", false = "denied".</param>
        public static void SetConsent(bool granted)
        {
            _consentState = granted ? "granted" : "denied";
            PlayerPrefs.SetString("reflect_consent_state", _consentState);
            PlayerPrefs.Save();
            ReflectLogger.Info($"Consent state set to '{_consentState}'");
        }

        /// <summary>Returns the current consent state — "granted" or "denied".</summary>
        public static string GetConsent() => _consentState;

        /// <summary>
        /// Signals user has granted (or denied) consent for advertising identifiers.
        /// Only meaningful when RequireAdvertisingConsent=true in config.
        /// </summary>
        public static void SetAdvertisingConsent(bool granted)
        {
            _advertisingConsent = granted;
            if (_initialized)
            {
                _platform.SetAdvertisingConsent(granted);
                if (granted)
                {
                    // Re-collect device info to pick up IDs now allowed.
                    _platform.CollectDeviceInfo();
                }
            }
        }

        /// <summary>
        /// Request iOS App Tracking Transparency. On non-iOS platforms invokes callback with Unavailable.
        /// </summary>
        public static void RequestIosTracking(Action<IosTrackingStatus> callback)
        {
            if (!EnsureReady())
            {
                callback?.Invoke(IosTrackingStatus.Unavailable);
                return;
            }
            _receiver.PendingAttCallback = callback;
            _platform.RequestIosTracking();
        }

        // ───────────────────────── Deep linking ────────────────────────────

        /// <summary>
        /// Subscribe to deep-link events. Fires for:
        ///   - WARM: app already running, OS dispatches a URI (intent / userActivity)
        ///   - COLD: app launched FROM a deep link
        ///   - DEFERRED: first launch where the install referrer / AdServices
        ///     payload contained a `dl=...` parameter — the SDK fires once after
        ///     the app_install event lands.
        ///
        /// Multiple subscribers supported; each is called for every event.
        /// </summary>
        public static event Action<DeepLinkData> OnDeepLink;

        /// <summary>
        /// Public-facing API for warm/cold deep links. Call this from your own
        /// Activity (Android <c>onNewIntent</c>) or AppDelegate (iOS
        /// <c>application:openURL:</c>) with the full URL the OS handed you.
        /// Pass <paramref name="isCold"/>=true if this URL launched the app.
        /// </summary>
        public static void HandleDeepLink(string url, bool isCold = false)
        {
            if (string.IsNullOrEmpty(url)) return;
            DispatchDeepLink(new DeepLinkData
            {
                Url    = url,
                Path   = ExtractPath(url),
                Source = isCold ? DeepLinkSource.Cold : DeepLinkSource.Warm,
            });
        }

        private static string ExtractPath(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                var u = new Uri(url, UriKind.RelativeOrAbsolute);
                return u.IsAbsoluteUri ? u.AbsolutePath : url;
            }
            catch { return null; }
        }

        /// <summary>
        /// Internal: invoked by the platform bridges (Android intent, iOS URL
        /// scheme, deferred from referral) and by the public API for tests.
        /// </summary>
        internal static void DispatchDeepLink(DeepLinkData data)
        {
            if (data == null) return;
            try { OnDeepLink?.Invoke(data); }
            catch (Exception ex) { ReflectLogger.Error($"Deep link handler threw: {ex}"); }

            // Also enqueue an event so the server sees the dispatch — useful
            // for measuring deep-link conversion. Single small event, no extra
            // request beyond the normal batch.
            if (_initialized)
            {
                var props = new Dictionary<string, object>(3)
                {
                    { "url",    data.Url ?? "" },
                    { "source", data.Source.ToString().ToLowerInvariant() },
                };
                if (!string.IsNullOrEmpty(data.Path)) props["path"] = data.Path;
                EnqueueEvent("deep_link_opened", MergeWithGlobals(props),
                             revenue: null, currency: null, txId: null, productId: null);
            }
        }

        // ───────────────────────── Audience tagging ───────────────────────

        /// <summary>
        /// Tag this install with one or more audience labels (e.g. "paying",
        /// "whale_v3"). Server stores them in install_audiences so reports can
        /// filter by segment. Calling again with the same tags is idempotent.
        /// Cost-conscious: re-uses the existing /event ingestion pipe — no
        /// extra Worker request — by emitting a single _set_audience event.
        /// </summary>
        public static void SetAudience(params string[] tags)
        {
            if (!EnsureReady()) return;
            if (tags == null || tags.Length == 0) return;
            var props = new Dictionary<string, object>(1) { { "tags", tags } };
            EnqueueEvent("_set_audience", MergeWithGlobals(props),
                         revenue: null, currency: null, txId: null, productId: null);
        }

        // ───────────────────────── SKAN Conversion Value ────────────────────

        /// <summary>
        /// Update the SKAN conversion value. On iOS 17.4+ this uses AdAttributionKit;
        /// on iOS 16.1+ uses SKAdNetwork 4.0; falls back to legacy SKAN on older iOS.
        /// No-op on Android and Editor (Editor logs the call).
        ///
        /// <paramref name="fineValue"/>: 0-63 fine conversion value.
        /// <paramref name="coarseValue"/>: "low", "medium", "high", or null for none (SKAN 4.0+).
        /// <paramref name="lockWindow"/>: if true, lock the current postback window immediately.
        /// <paramref name="onComplete"/>: optional callback — (success, errorMessage).
        /// </summary>
        public static void UpdateConversionValue(int fineValue, string coarseValue = null,
                                                   bool lockWindow = false,
                                                   Action<bool, string> onComplete = null)
        {
            if (!EnsureReady())
            {
                onComplete?.Invoke(false, "not_initialized");
                return;
            }
            if (fineValue < 0 || fineValue > 63)
            {
                ReflectLogger.Warn($"UpdateConversionValue: fineValue {fineValue} out of range 0-63");
                onComplete?.Invoke(false, "fine_value_out_of_range");
                return;
            }

            _receiver.PendingSkanCvCallback = onComplete;
            _platform.UpdateSkanConversionValue(fineValue, coarseValue ?? "", lockWindow);
        }

        // ───────────────────────── Push token registration ────────────────

        /// <summary>
        /// Register a push notification token with the Reflect server.
        /// Fires an internal <c>_push_token</c> event and POSTs the token
        /// directly to <c>{BaseUrl}/push-token</c>.
        ///
        /// <paramref name="token"/>: the device push token (APNs or FCM).
        /// <paramref name="provider"/>: "apns" or "fcm". If null, defaults to
        /// "apns" on iOS and "fcm" on Android.
        /// </summary>
        public static void RegisterPushToken(string token, string provider = null)
        {
            if (!EnsureReady()) return;
            if (string.IsNullOrEmpty(token))
            {
                ReflectLogger.Warn("RegisterPushToken: token is null/empty — ignoring.");
                return;
            }

            // Default provider based on platform.
            if (string.IsNullOrEmpty(provider))
            {
                provider = Application.platform == RuntimePlatform.IPhonePlayer ? "apns" : "fcm";
            }

            // 1) Fire an internal _push_token event so the event stream records it.
            var props = new Dictionary<string, object>(2)
            {
                { "token",    token },
                { "provider", provider },
            };
            EnqueueEvent("_push_token", MergeWithGlobals(props),
                         revenue: null, currency: null, txId: null, productId: null);

            // 2) Direct HTTP POST to /push-token so the server stores it immediately.
            if (!_config.IsDebugMode)
            {
                var platform = _deviceSnapshot?.Os ?? (Application.platform == RuntimePlatform.IPhonePlayer ? "iOS" : "Android");
                var runner = ReflectCallbackReceiver.Ensure();
                runner.StartCoroutine(_dispatcher.SendPushToken(
                    _config.AppKey, InstallUuidStore.Value, platform, token, provider));
            }
        }

        // ───────────────────────── Privacy / right-to-be-forgotten ─────────

        /// <summary>
        /// Wipe all locally-stored Reflect data AND request server-side
        /// deletion of every event/click/attribution row tied to this install.
        ///
        /// The server enqueues the request and processes it in nightly batches
        /// to keep D1 row-write costs bounded; this method only blocks for the
        /// HTTP POST itself.
        /// </summary>
        /// <param name="onComplete">Called with true on successful queue,
        /// false otherwise. Local data is wiped either way.</param>
        public static void DeleteUserData(Action<bool> onComplete = null)
        {
            // Wipe LOCAL first so even if the server call fails the user's
            // device is clean — strongest GDPR posture.
            try
            {
                _queue?.WipeAll();
                InstallUuidStore.WipeAll();
                _userId = null;
                ClearGlobalProperties();
            }
            catch (Exception ex)
            {
                ReflectLogger.Error($"DeleteUserData local wipe failed: {ex}");
            }

            if (!_initialized || _config == null || _config.IsDebugMode)
            {
                onComplete?.Invoke(true);
                return;
            }

            // Server-side request — fire-and-forget POST /privacy/delete with
            // HMAC. We don't queue this through the normal batch dispatcher
            // because it must succeed before we tell the OS the deletion is
            // done; doing it directly in a coroutine is simpler.
            var runner = ReflectCallbackReceiver.Ensure();
            runner.StartCoroutine(_dispatcher.SendPrivacyDelete(InstallUuid, onComplete));
        }

        // ───────────────────────── Manual flush ───────────────────────────

        /// <summary>Force-flush the event queue. Normally handled automatically.</summary>
        public static void Flush() => _dispatcher?.Flush();

        // ───────────────────────── Internals ──────────────────────────────

        private static bool EnsureReady()
        {
            if (!_initialized)
            {
                ReflectLogger.Warn("ReflectSDK API called before Initialize — ignoring.");
                return false;
            }
            return true;
        }

        private static void EnqueueEvent(string name, IDictionary<string, object> props,
                                          double? revenue, string currency, string txId, string productId)
        {
            var evt = new ReflectEvent
            {
                EventId       = Guid.NewGuid().ToString("N"),
                EventName     = name,
                EventTsMs     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                InstallUuid   = InstallUuidStore.Value,
                UserId        = _userId,
                SdkVersion    = SdkVersion.Value,
                AppVersion    = _appVersion,
                Environment   = _config.Environment,
                IsForeground  = _isForeground,
                PushToken     = _pushToken,
                ExternalDeviceId = _externalDeviceId,
                Coppa             = _config.CoppaCompliant,
                ThirdPartySharing = _thirdPartySharing,
                PartnerParams = SnapshotPartnerParams(),
                AttStatus     = _attStatus,
                ConsentState  = _consentState,
                Device        = _deviceSnapshot,
                Referral      = _referralSnapshot,
                Revenue       = revenue,
                Currency      = currency,
                TransactionId = txId,
                ProductId     = productId,
                Properties    = props
            };
            var json = evt.ToJson();
            _queue.EnqueueRaw(json);

            var note = _config.IsDebugMode ? "no BaseUrl — not dispatched" : null;
            ReflectDebugEventLog.RecordEnqueued(evt.EventId, evt.EventName, json, note);

            ReflectLogger.Info($"Enqueued '{name}' (queue={_queue.Count})");
            _dispatcher.RequestFlushSoon();

            // Sprint I: on the first app_open of this session, poll the server
            // for attribution changes so the game can react in real time.
            if (name == "app_open" && !_attributionCheckedThisSession && !_config.IsDebugMode)
            {
                _attributionCheckedThisSession = true;
                _lastAttributionCheckMs = PlayerPrefs.HasKey(ATTRIBUTION_CHECK_PREFS_KEY)
                    ? long.Parse(PlayerPrefs.GetString(ATTRIBUTION_CHECK_PREFS_KEY, "0"))
                    : 0;
                var runner = ReflectCallbackReceiver.Ensure();
                runner.StartCoroutine(AttributionCheckCo());
            }
        }

        // ── Attribution check coroutine (Sprint I) ─────────────────────
        // Polls GET /attribution/check once per session on app_open. If the
        // server reports a newer attribution row, fires OnAttributionUpdated
        // and persists the new watermark so subsequent sessions only see truly
        // new changes.

        private static IEnumerator AttributionCheckCo()
        {
            // Build query string.
            var installUuid = InstallUuidStore.Value;
            var queryString = "install_uuid=" + Uri.EscapeDataString(installUuid)
                            + "&since=" + _lastAttributionCheckMs;

            // HMAC-sign the query string (same as the server expects).
            string signature = null;
            if (!string.IsNullOrEmpty(_config.SigningSecret))
            {
                using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_config.SigningSecret)))
                {
                    var sigBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(queryString));
                    var sb = new StringBuilder(sigBytes.Length * 2);
                    for (int i = 0; i < sigBytes.Length; i++) sb.Append(sigBytes[i].ToString("x2"));
                    signature = sb.ToString();
                }
            }

            var url = _config.BaseUrl + "/attribution/check?" + queryString;

            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("X-Reflect-Sdk", SdkVersion.Value);
                if (!string.IsNullOrEmpty(_config.AppKey))
                    req.SetRequestHeader("X-Reflect-App-Key", _config.AppKey);
                if (signature != null)
                    req.SetRequestHeader("X-Reflect-Signature", signature);
                req.timeout = 15;

                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                bool isError = req.result != UnityWebRequest.Result.Success;
#else
                bool isError = req.isNetworkError || req.isHttpError;
#endif
                if (isError || req.responseCode < 200 || req.responseCode >= 300)
                {
                    ReflectLogger.Warn($"Attribution check failed ({req.responseCode}): {req.error}");
                    yield break;
                }

                // Parse JSON response.
                var responseText = req.downloadHandler != null ? req.downloadHandler.text : null;
                if (string.IsNullOrEmpty(responseText))
                {
                    yield break;
                }

                var parsed = MiniJson.Deserialize(responseText) as IDictionary<string, object>;
                if (parsed == null)
                {
                    ReflectLogger.Warn("Attribution check: failed to parse response JSON.");
                    yield break;
                }

                // Check the 'changed' flag.
                object changedObj;
                if (!parsed.TryGetValue("changed", out changedObj) || !(changedObj is bool))
                {
                    yield break;
                }
                bool changed = (bool)changedObj;
                if (!changed)
                {
                    ReflectLogger.Info("Attribution check: no change.");
                    yield break;
                }

                // Extract the data payload.
                object dataObj;
                if (!parsed.TryGetValue("data", out dataObj))
                {
                    yield break;
                }
                var dataDict = dataObj as IDictionary<string, object>;
                if (dataDict == null)
                {
                    yield break;
                }

                // Build a clean dictionary for the public event.
                var result = new Dictionary<string, object>();
                foreach (var kv in dataDict)
                {
                    result[kv.Key] = kv.Value;
                }

                // Persist the new watermark so the next session only sees newer changes.
                object attrAtMs;
                if (dataDict.TryGetValue("attributed_at_ms", out attrAtMs))
                {
                    long newMs = 0;
                    if (attrAtMs is long)   newMs = (long)attrAtMs;
                    else if (attrAtMs is double) newMs = (long)(double)attrAtMs;

                    if (newMs > _lastAttributionCheckMs)
                    {
                        _lastAttributionCheckMs = newMs;
                        PlayerPrefs.SetString(ATTRIBUTION_CHECK_PREFS_KEY, newMs.ToString());
                        PlayerPrefs.Save();
                    }
                }

                ReflectLogger.Info("Attribution check: change detected — firing OnAttributionUpdated.");

                try { OnAttributionUpdated?.Invoke(result); }
                catch (Exception ex)
                {
                    ReflectLogger.Error($"OnAttributionUpdated handler threw: {ex}");
                }
            }
        }

        private static void AttachDebugOverlay()
        {
            if (_overlay != null) return;
            _overlay = _receiver.gameObject.GetComponent<ReflectDebugOverlay>()
                       ?? _receiver.gameObject.AddComponent<ReflectDebugOverlay>();
            _overlay.DeviceSnapshotProvider   = () => _deviceSnapshot;
            _overlay.ReferralSnapshotProvider = () => _referralSnapshot;
            _overlay.QueueSizeProvider        = () => _queue?.Count ?? 0;
            _overlay.AttStatusProvider        = () => _attStatus;
            _overlay.UserIdProvider           = () => _userId;
            _overlay.BaseUrlProvider          = () => _config?.BaseUrl;
        }

        private static void OnDeviceInfoReady(DeviceSnapshot snap)
        {
            _deviceSnapshot = snap;
            ReflectLogger.Info("Device info collected.");
            MaybeFireInstallEvent();
        }

        private static void OnReferralReady(ReferralSnapshot snap)
        {
            _referralSnapshot = snap;
            ReflectLogger.Info($"Referral collected (raw='{snap?.Raw}').");
            MaybeFireInstallEvent();
        }

        private static void OnAttStatus(IosTrackingStatus status)
        {
            _attStatus = status;
            ReflectLogger.Info($"ATT status: {status}");
            if (status == IosTrackingStatus.Authorized)
                _platform.CollectDeviceInfo();

            var cb = _receiver.PendingAttCallback;
            _receiver.PendingAttCallback = null;
            cb?.Invoke(status);
        }

        // Backstop coroutine: force-fire the install event after a timeout if
        // device/referral collection never completes (e.g. the native bridge was
        // stripped from a release build, or Play Services is slow/unavailable).
        // Without this, a single collection failure black-holes the install — and
        // with it the attribution and the install count — entirely.
        private static IEnumerator InstallTimeoutCo(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            MaybeFireInstallEvent(force: true);
        }

        private static void MaybeFireInstallEvent(bool force = false)
        {
            if (_receiver == null || !_receiver.PendingInstallEvent) return;
            // Fast path: wait until BOTH device info AND the install referrer have
            // arrived, so app_install carries the click_id (deterministic
            // attribution) and the device IDs. The timeout backstop (force=true)
            // fires with whatever arrived so a stalled/stripped collector can't
            // lose the install — deterministic if the referrer made it, organic
            // otherwise, but never invisible.
            if (!force && (_deviceSnapshot == null || _referralSnapshot == null)) return;
            _receiver.PendingInstallEvent = false;
            TrackEvent(ReflectStandardEvents.AppInstall);
            // Firebase parity: app_first_open distinguishes the very first session
            // from subsequent app_opens. Always fired exactly once per install.
            TrackEvent(ReflectStandardEvents.AppFirstOpen);
            InstallUuidStore.MarkInstallReported();

            // Deferred deep link — if the install referrer carries `dl=<encoded>`,
            // fire DispatchDeepLink so the app can route to the right screen
            // even though it just installed cold.
            MaybeDispatchDeferredDeepLink();
        }

        private static void MaybeDispatchDeferredDeepLink()
        {
            if (_referralSnapshot?.ParsedParams == null) return;
            if (!_referralSnapshot.ParsedParams.TryGetValue("dl", out var dlObj)) return;
            var dl = dlObj as string;
            if (string.IsNullOrEmpty(dl)) return;
            DispatchDeepLink(new DeepLinkData
            {
                Url         = dl,
                Path        = ExtractPath(dl),
                Source      = DeepLinkSource.Deferred,
                PartnerSlug = _referralSnapshot.ParsedParams.TryGetValue("partner", out var p) ? p as string : null,
            });
        }

        private static void OnApplicationPauseInternal(bool paused)
        {
            if (!_initialized) return;
            _isForeground = !paused;
            if (paused)
            {
                if (_config.AutoSessionTracking)
                {
                    // session_length_ms drives aggregates_sessions.total_active_ms.
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var lengthMs = _sessionStartMs > 0 ? nowMs - _sessionStartMs : 0L;
                    TrackEvent("session_end", new Dictionary<string, object> { { "session_length_ms", lengthMs } });
                }
                _queue.PersistToDisk();
            }
            else
            {
                if (_config.AutoSessionTracking)
                {
                    _sessionStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    TrackEvent("session_start");
                }
                _dispatcher.RequestFlushSoon();
            }
        }

        // Throttle: at most one _crash event per minute even if the app is
        // throwing in a tight loop — protects R2 + queue from runaway logs.
        private static double _lastCrashAtMs;
        private const double CRASH_RATE_LIMIT_MS = 60_000;
        private static readonly object _crashThrottleLock = new object();

        private static void OnUnityLogMessage(string condition, string stack, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Assert) return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_crashThrottleLock)
            {
                if (now - _lastCrashAtMs < CRASH_RATE_LIMIT_MS) return;
                _lastCrashAtMs = now;
            }

            // Trim aggressively — server caps event body size and we don't want
            // a 50-line stack hogging R2.
            var trimmedStack = stack != null && stack.Length > 1024 ? stack.Substring(0, 1024) : stack;
            var trimmedMsg   = condition != null && condition.Length > 256 ? condition.Substring(0, 256) : condition;

            try
            {
                var props = new Dictionary<string, object>(3)
                {
                    { "message", trimmedMsg ?? "" },
                    { "stack",   trimmedStack ?? "" },
                    { "type",    type.ToString() },
                };
                EnqueueEvent(ReflectStandardEvents.Crash, MergeWithGlobals(props),
                             revenue: null, currency: null, txId: null, productId: null);
            }
            catch { /* don't let the crash handler crash */ }
        }

        private static float _lastFlushAt;
        private static void OnTick()
        {
            if (!_initialized) return;
            var now = Time.realtimeSinceStartup;
            if (now - _lastFlushAt >= _config.FlushIntervalSeconds)
            {
                _lastFlushAt = now;
                _dispatcher.Flush();
            }
        }
    }

    /// <summary>
    /// Version + authorship constants. Sent as the <c>sdk_version</c> field on
    /// every event and as the <c>X-Reflect-Sdk</c> header on every request.
    /// </summary>
    internal static class SdkVersion
    {
        /// <summary>Semver string. Bump on every release.</summary>
        public const string Value = "2.1.0";

        /// <summary>Human-readable tag used in logs + the debug overlay header.</summary>
        public const string FullTag = "Reflect SDK v" + Value;
    }
}
