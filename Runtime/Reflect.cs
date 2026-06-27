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
        // Set at Initialize when AutoSessionTracking is on; the cold-start app_open /
        // session_start are held until the device snapshot arrives (or a timeout) so
        // they carry device data. Flushed exactly once by FlushColdStartIfPending.
        private static bool _coldStartPending;
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
        // Adjust-parity session bookkeeping (threshold, counts, subsessions,
        // cumulative length, last_interval, session_id). Null when AutoSessionTracking
        // is off, in which case no session fields are attached to events.
        private static ReflectSession _session;
        private static IosTrackingStatus _attStatus = IosTrackingStatus.NotDetermined;
        private static bool _advertisingConsent = true;
        private static string _consentState = "granted";

        // Persisted runtime-toggle keys (survive relaunch).
        private const string PREF_ENABLED       = "reflect_enabled";
        private const string PREF_AD_CONSENT    = "reflect_ad_consent";
        private const string PREF_TPS           = "reflect_third_party_sharing";
        private const string PREF_PENDING_DELETE = "reflect_pending_delete";
        // Per-partner third-party-sharing overrides (Adjust parity: AddPartnerSharingSetting).
        private static readonly Dictionary<string, object> _partnerSharing = new Dictionary<string, object>();
        private static readonly object _partnerSharingLock = new object();
        private static readonly Dictionary<string, object> _globalProps = new Dictionary<string, object>();
        private static readonly object _globalPropsLock = new object();
        // Adjust parity: partner_params — forwarded to ad-network partners, kept
        // separate from callback/global props.
        private static readonly Dictionary<string, object> _partnerParams = new Dictionary<string, object>();
        private static readonly object _partnerParamsLock = new object();

        // ── Attribution check state (Sprint I) ──────────────────────────
        private static bool _attributionCheckedThisSession;
        private static long _lastAttributionCheckMs;
        // Last attribution payload observed (for GetAttribution / GetAttributionWithTimeout).
        private static Dictionary<string, object> _lastAttribution;
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
            _dedupMax = config.EventDeduplicationIdsMaxSize;
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
                _receiver.OnQuitHandler            = OnApplicationQuitInternal;
                _receiver.OnTickHandler            = OnTick;

                if (config.IsOverlayEnabled) AttachDebugOverlay();

                InstallUuidStore.EnsureGenerated();

                // Load persisted consent state from PlayerPrefs. If none is stored yet,
                // the initial posture depends on RequireConsent: fail-closed ("denied")
                // when consent is required, otherwise "granted" (legacy behavior).
                if (PlayerPrefs.HasKey("reflect_consent_state"))
                    _consentState = PlayerPrefs.GetString("reflect_consent_state", "granted");
                else
                    _consentState = config.RequireConsent ? "denied" : "granted";
                bool consentDenied = _consentState == "denied";

                // Restore runtime toggles that must survive a relaunch (previously these
                // reset every launch, silently re-enabling tracking / re-collecting IDs /
                // re-sharing after the user had turned them off).
                _enabled = PlayerPrefs.GetInt(PREF_ENABLED, 1) == 1;
                _thirdPartySharing = PlayerPrefs.GetInt(PREF_TPS, 1) == 1;

                // Advertising IDs (GAID/IDFA) are only read when ALL gates allow it:
                //   • the persisted advertising-consent decision allows it (or, if none
                //     stored, the config default !RequireAdvertisingConsent),
                //   • not a COPPA/kids app (identifiers must never be read), and
                //   • general data-collection consent not denied.
                bool adConsentBase = PlayerPrefs.HasKey(PREF_AD_CONSENT)
                    ? PlayerPrefs.GetInt(PREF_AD_CONSENT, 0) == 1
                    : !config.RequireAdvertisingConsent;
                _advertisingConsent = adConsentBase && !config.CoppaCompliant && !consentDenied;

                // Consent denied → hold everything on-device until it is granted.
                _dispatcher.SetConsentBlocked(consentDenied);

                // Retry a GDPR deletion that was requested but not confirmed last run.
                RetryPendingDeletionIfAny();

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
                if (consentDenied)
                    ReflectLogger.Warn("CONSENT DENIED — events are recorded on-device but NOT transmitted, " +
                                       "and advertising IDs are not read. Call ReflectSDK.SetConsent(true) to enable.");

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
                    if (config.AutoResolveDeferredDeepLink && !config.IsDebugMode && !consentDenied)
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

                    // LinkMe: recover a deferred deep link the user copied at click time
                    // (improves iOS deferred match rate). Opt-in (shows the iOS paste banner).
                    if (config.LinkMeEnabled && !consentDenied)
                        MaybeReadLinkMeClipboard();
                }

                if (config.AutoSessionTracking)
                {
                    _session = new ReflectSession(config.SessionThresholdSeconds * 1000L);
                    // Defer the cold-start app_open / session_start until the device
                    // snapshot has been collected, so they carry full device data — they
                    // used to fire here synchronously, BEFORE async native collection
                    // finished, landing with a null device. Fired from OnDeviceInfoReady
                    // (after app_install, preserving order) or after a timeout backstop so
                    // app_open is never lost if collection stalls.
                    _coldStartPending = true;
                    _receiver.StartCoroutine(ColdStartTimeoutCo(config.InstallEventTimeoutSeconds));
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
        /// Track a named event with rich per-event options — per-event partner/callback
        /// parameters, a client-side deduplication id, a callback id, and optional
        /// revenue. See <see cref="ReflectEventOptions"/>. Adjust parity.
        /// </summary>
        public static void TrackEvent(string name, ReflectEventOptions options)
        {
            if (!EnsureReady()) return;
            if (!_enabled) return;
            if (options == null) { TrackEvent(name, (IDictionary<string, object>)null); return; }

            var v = EventValidator.Validate(name, options.Properties);
            if (!v.Ok)
            {
                ReflectLogger.Warn($"TrackEvent('{name}') rejected: {v.Reason}");
                return;
            }
            var merged = MergeWithGlobals(v.CleanedProps);
            EnqueueEvent(name, merged, options.Revenue, options.Currency, txId: null, productId: null,
                         dedupId: options.DeduplicationId, callbackId: options.CallbackId,
                         perEventPartnerParams: options.PartnerParams, callbackParams: options.CallbackParams);
        }

        /// <summary>
        /// Convenience helper for purchase events. Price is in local currency.
        /// Optionally pass <paramref name="receiptData"/> (base64 StoreKit receipt
        /// or Play purchase token); the server will validate it against Apple/Google
        /// to flip <c>attributions.is_revenue_validated</c> on success and
        /// flag spoofed receipts as fraud.
        /// </summary>
        public static void TrackPurchase(string productId, double price, string currencyCode, string transactionId,
                                          IDictionary<string, object> extraProps = null, string receiptData = null,
                                          string purchaseToken = null, string orderId = null)
        {
            if (!EnsureReady()) return;
            var props = extraProps != null ? new Dictionary<string, object>(extraProps) : new Dictionary<string, object>();
            props["product_id"]     = productId;
            props["price_local"]    = price;
            props["currency_code"]  = currencyCode;
            props["transaction_id"] = transactionId;
            if (!string.IsNullOrEmpty(receiptData)) props["receipt_data"] = receiptData;
            // Dedup on the platform-correct key: Play purchase_token on Android,
            // StoreKit transaction_id on iOS. Both ride the envelope so server-side
            // receipt validation has what it needs.
            var dedup = !string.IsNullOrEmpty(purchaseToken) ? purchaseToken : transactionId;
            EnqueueEvent("purchase", MergeWithGlobals(props), price, currencyCode, transactionId, productId,
                         purchaseToken: purchaseToken, orderId: orderId, dedupId: dedup);
        }

        /// <summary>Convenience helper for subscription events.</summary>
        public static void TrackSubscription(string productId, double price, string currencyCode,
                                              string transactionId, bool isTrial,
                                              IDictionary<string, object> extraProps = null, string receiptData = null,
                                              string purchaseToken = null, string orderId = null)
        {
            if (!EnsureReady()) return;
            var props = extraProps != null ? new Dictionary<string, object>(extraProps) : new Dictionary<string, object>();
            props["product_id"]     = productId;
            props["price_local"]    = price;
            props["currency_code"]  = currencyCode;
            props["transaction_id"] = transactionId;
            props["is_trial"]       = isTrial;
            if (!string.IsNullOrEmpty(receiptData)) props["receipt_data"] = receiptData;
            var dedup = !string.IsNullOrEmpty(purchaseToken) ? purchaseToken : transactionId;
            EnqueueEvent("subscribe", MergeWithGlobals(props), price, currencyCode, transactionId, productId,
                         purchaseToken: purchaseToken, orderId: orderId, dedupId: dedup);
        }

        // ───────────────────────── Purchase verification ──────────────────

        /// <summary>
        /// Verify a purchase receipt server-side (Apple/Google) WITHOUT tracking an
        /// event, returning a typed <see cref="ReflectVerificationResult"/> so the app
        /// can gate entitlements on the outcome. Pass the iOS StoreKit
        /// <paramref name="transactionId"/> + <paramref name="receiptData"/>, or the
        /// Android <paramref name="purchaseToken"/>. Adjust parity:
        /// <c>VerifyAppStorePurchase</c> / <c>VerifyPlayStorePurchase</c>.
        /// </summary>
        public static void VerifyPurchase(string productId, string transactionId, string purchaseToken,
            string receiptData, Action<ReflectVerificationResult> callback)
        {
            if (!EnsureReady())
            {
                callback?.Invoke(new ReflectVerificationResult(ReflectVerificationStatus.Failed, 0, "not_initialized"));
                return;
            }
            if (_config.IsDebugMode)
            {
                callback?.Invoke(new ReflectVerificationResult(ReflectVerificationStatus.Unknown, 0, "debug_mode"));
                return;
            }
            ReflectCallbackReceiver.Ensure().StartCoroutine(_dispatcher.VerifyPurchase(
                _config.AppKey, InstallUuidStore.Value, productId, transactionId, purchaseToken, receiptData, callback));
        }

        /// <summary>
        /// Verify a purchase receipt AND track the purchase, annotating the event with
        /// the verification outcome. The purchase is tracked regardless of the result
        /// (the <c>verification_status</c> prop records it). Adjust parity:
        /// <c>VerifyAndTrackAppStorePurchase</c> / <c>VerifyAndTrackPlayStorePurchase</c>.
        /// </summary>
        public static void VerifyAndTrackPurchase(string productId, double price, string currencyCode,
            string transactionId, string receiptData = null, string purchaseToken = null,
            string orderId = null, Action<ReflectVerificationResult> callback = null)
        {
            if (!EnsureReady())
            {
                callback?.Invoke(new ReflectVerificationResult(ReflectVerificationStatus.Failed, 0, "not_initialized"));
                return;
            }
            VerifyPurchase(productId, transactionId, purchaseToken, receiptData, result =>
            {
                var extra = new Dictionary<string, object>
                {
                    { "verification_status", (result?.Status ?? ReflectVerificationStatus.Unknown).ToString() },
                };
                TrackPurchase(productId, price, currencyCode, transactionId, extra, receiptData, purchaseToken, orderId);
                callback?.Invoke(result);
            });
        }

        // ───────────────────────── Ad revenue ────────────────────────────

        /// <summary>
        /// Track ad revenue from a mediation platform. Fires the canonical
        /// <c>ad_impression</c> event (same name as <see cref="ReflectStandardEvents.AdShown"/>)
        /// and — crucially — puts the revenue/currency in the event <b>envelope</b> (not
        /// just props) so server-side revenue sums include ad revenue. Adjust parity:
        /// <c>Adjust.TrackAdRevenue</c>.
        /// </summary>
        public static void TrackAdRevenue(string mediationPlatform, double revenue, string currency,
            string adFormat = null, string adNetwork = null, string adUnitId = null,
            string placement = null, string precision = "estimated", int impressionsCount = 1)
        {
            if (!EnsureReady()) return;
            var cur = string.IsNullOrEmpty(currency) ? "USD" : currency;
            var props = new Dictionary<string, object>
            {
                { "mediation_platform", mediationPlatform },
                { "precision", precision ?? "estimated" },
            };
            if (adFormat != null) props["ad_format"] = adFormat;
            if (adNetwork != null) props["ad_network"] = adNetwork;
            if (adUnitId != null) props["ad_unit_id"] = adUnitId;
            if (placement != null) props["placement"] = placement;
            // Envelope revenue/currency (like TrackPurchase) — NOT buried in props.
            EnqueueEvent(ReflectStandardEvents.AdImpression, MergeWithGlobals(props),
                         revenue: revenue, currency: cur, txId: null, productId: null,
                         impressionsCount: impressionsCount < 1 ? 1 : impressionsCount);
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

            // Only fire the stitch event once the SDK is initialized — calling
            // SetUserId before Initialize would otherwise NRE on _config/_queue. The
            // id is still recorded above so it applies to events once init completes.
            if (wasAnon && !string.IsNullOrEmpty(userId) && _initialized)
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
            PlayerPrefs.SetInt(PREF_ENABLED, enabled ? 1 : 0);
            PlayerPrefs.Save();
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
            PlayerPrefs.SetInt(PREF_TPS, granted ? 1 : 0);
            PlayerPrefs.Save();
            // Send a dedicated, authoritative signal on change (Adjust parity:
            // trackThirdPartySharing) rather than relying only on the per-event flag,
            // and carry any per-partner granular settings.
            if (_initialized && !_config.IsDebugMode)
            {
                var props = new Dictionary<string, object>(2) { { "granted", granted } };
                lock (_partnerSharingLock)
                    if (_partnerSharing.Count > 0)
                        props["partners"] = new Dictionary<string, object>(_partnerSharing);
                EnqueueEvent("_third_party_sharing", MergeWithGlobals(props),
                             revenue: null, currency: null, txId: null, productId: null);
            }
        }

        /// <summary>
        /// Per-partner third-party-sharing override (e.g. allow sharing with one ad
        /// network but not others). Stored and attached to the next third-party-sharing
        /// signal. Adjust parity: <c>AddPartnerSharingSetting</c>. Pass value null to clear.
        /// </summary>
        public static void SetPartnerSharing(string partner, string key, bool value)
        {
            if (string.IsNullOrEmpty(partner) || string.IsNullOrEmpty(key)) return;
            lock (_partnerSharingLock)
            {
                if (!(_partnerSharing.TryGetValue(partner, out var existing) && existing is Dictionary<string, object> map))
                {
                    map = new Dictionary<string, object>();
                    _partnerSharing[partner] = map;
                }
                map[key] = value;
            }
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

        /// <summary>Merge global partner params with optional per-event partner params
        /// (per-event wins on collision). Returns null when both are empty.</summary>
        private static Dictionary<string, object> MergePartnerParams(IDictionary<string, object> perEvent)
        {
            lock (_partnerParamsLock)
            {
                bool noPerEvent = perEvent == null || perEvent.Count == 0;
                if (_partnerParams.Count == 0 && noPerEvent) return null;
                var merged = new Dictionary<string, object>(_partnerParams.Count + (perEvent?.Count ?? 0));
                foreach (var kv in _partnerParams) merged[kv.Key] = kv.Value;
                if (!noPerEvent) foreach (var kv in perEvent) merged[kv.Key] = kv.Value;
                return merged;
            }
        }

        // ── Client-side event de-duplication (Adjust parity) ────────────────
        // Bounded LRU of recently-seen deduplication_ids. An event whose id is
        // already in the window is dropped before it reaches the queue.
        private static readonly HashSet<string> _seenDedupIds = new HashSet<string>();
        private static readonly Queue<string> _dedupOrder = new Queue<string>();
        private static readonly object _dedupLock = new object();
        private static int _dedupMax = 10;

        private static bool IsDuplicateEvent(string dedupId)
        {
            if (_dedupMax <= 0) return false;
            lock (_dedupLock)
            {
                if (_seenDedupIds.Contains(dedupId)) return true;
                _seenDedupIds.Add(dedupId);
                _dedupOrder.Enqueue(dedupId);
                while (_dedupOrder.Count > _dedupMax)
                    _seenDedupIds.Remove(_dedupOrder.Dequeue());
                return false;
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

            // Enforce client-side, not just annotate: when denied, stop transmitting
            // (events stay in the on-device queue) and stop reading advertising IDs.
            // When granted, resume dispatch + re-collect device info to pick up IDs,
            // and flush anything that accumulated while denied.
            if (!_initialized) return;
            _dispatcher?.SetConsentBlocked(!granted);
            if (granted)
            {
                // Restore advertising-ID reading only if no other gate forbids it
                // (advertising-consent requirement / COPPA).
                bool adAllowed = !_config.RequireAdvertisingConsent && !_config.CoppaCompliant;
                if (adAllowed && !_advertisingConsent)
                {
                    _advertisingConsent = true;
                    _platform?.SetAdvertisingConsent(true);
                    _platform?.CollectDeviceInfo();
                }
                _dispatcher?.RequestFlushSoon();
                _dispatcher?.Flush();
            }
            else
            {
                _advertisingConsent = false;
                _platform?.SetAdvertisingConsent(false);
            }
        }

        /// <summary>Returns the current consent state — "granted" or "denied".</summary>
        public static string GetConsent() => _consentState;

        /// <summary>
        /// Signals user has granted (or denied) consent for advertising identifiers.
        /// Only meaningful when RequireAdvertisingConsent=true in config.
        /// </summary>
        public static void SetAdvertisingConsent(bool granted)
        {
            // COPPA / kids apps must never read advertising IDs, and a denied
            // data-collection consent overrides an advertising-consent grant. In
            // those cases a grant here is ignored (fail-closed).
            if (granted && _config != null && (_config.CoppaCompliant || _consentState == "denied"))
            {
                ReflectLogger.Warn("SetAdvertisingConsent(true) ignored — blocked by " +
                                   (_config.CoppaCompliant ? "COPPA compliance" : "denied consent") + ".");
                return;
            }

            _advertisingConsent = granted;
            // Persist so the decision survives relaunch (it was previously reset to the
            // config default on every Initialize).
            PlayerPrefs.SetInt(PREF_AD_CONSENT, granted ? 1 : 0);
            PlayerPrefs.Save();
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
        /// A subscriber added AFTER a cold-start link was already dispatched is
        /// immediately replayed the most recent link, so deep-link routing that
        /// registers late (e.g. in a scene that loads after Initialize) never misses it.
        /// </summary>
        public static event Action<DeepLinkData> OnDeepLink
        {
            add
            {
                _onDeepLink += value;
                if (_lastDeepLink != null)
                {
                    try { value(_lastDeepLink); }
                    catch (Exception ex) { ReflectLogger.Error($"Deep link replay threw: {ex}"); }
                }
            }
            remove { _onDeepLink -= value; }
        }
        private static Action<DeepLinkData> _onDeepLink;
        private static DeepLinkData _lastDeepLink;

        /// <summary>The most recent deep link delivered to the app (cold/warm/deferred),
        /// or null if none yet. Adjust parity: <c>GetLastDeeplink</c>.</summary>
        public static DeepLinkData LastDeepLink => _lastDeepLink;

        /// <summary>Invoke <paramref name="callback"/> with the last deep link (or null) —
        /// query the launch deep link regardless of subscription timing.</summary>
        public static void GetLastDeeplink(Action<DeepLinkData> callback) => callback?.Invoke(_lastDeepLink);

        /// <summary>
        /// Resolve / unshorten a Reflect tracking or branded short link into its target
        /// URL via the server, returning the expanded URL (or null) to
        /// <paramref name="onResolved"/>. Adjust parity: <c>ProcessAndResolveDeeplink</c>.
        /// In debug mode (no BaseUrl) the input URL is returned unchanged.
        /// </summary>
        public static void ResolveDeepLink(string url, Action<string> onResolved)
        {
            if (!EnsureReady()) { onResolved?.Invoke(null); return; }
            if (string.IsNullOrEmpty(url)) { onResolved?.Invoke(null); return; }
            if (_config.IsDebugMode) { onResolved?.Invoke(url); return; }
            ReflectCallbackReceiver.Ensure().StartCoroutine(
                _dispatcher.ResolveLink(_config.AppKey, InstallUuidStore.Value, url, onResolved));
        }

        /// <summary>
        /// Return the most recently observed attribution (or null if none yet) to
        /// <paramref name="callback"/>. Adjust parity: <c>GetAttribution</c>.
        /// </summary>
        public static void GetAttribution(Action<Dictionary<string, object>> callback)
            => callback?.Invoke(_lastAttribution);

        /// <summary>
        /// Fetch the current attribution, waiting up to <paramref name="timeoutSeconds"/>
        /// for a server round-trip if none is cached yet, then invoke
        /// <paramref name="callback"/> with the result (or null). Adjust parity:
        /// <c>GetAttributionWithTimeout</c>.
        /// </summary>
        public static void GetAttributionWithTimeout(float timeoutSeconds, Action<Dictionary<string, object>> callback)
        {
            if (callback == null) return;
            if (!EnsureReady() || _config.IsDebugMode || _consentState == "denied" || _lastAttribution != null)
            {
                callback(_lastAttribution);
                return;
            }
            ReflectCallbackReceiver.Ensure().StartCoroutine(GetAttributionWithTimeoutCo(timeoutSeconds, callback));
        }

        private static IEnumerator GetAttributionWithTimeoutCo(float timeoutSeconds, Action<Dictionary<string, object>> callback)
        {
            bool done = false;
            ReflectCallbackReceiver.Ensure().StartCoroutine(AttributionCheckCo(_ => done = true));
            float waited = 0f;
            while (!done && waited < timeoutSeconds) { yield return null; waited += Time.unscaledDeltaTime; }
            callback?.Invoke(_lastAttribution);
        }

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
            _lastDeepLink = data;   // cache for GetLastDeeplink + late-subscriber replay
            try { _onDeepLink?.Invoke(data); }
            catch (Exception ex) { ReflectLogger.Error($"Deep link handler threw: {ex}"); }

            // Also enqueue an event so the server sees the dispatch — useful
            // for measuring deep-link conversion and, when the link carries tracking
            // parameters, for server-side REATTRIBUTION of re-engagement / retargeting
            // clicks (Adjust parity: a deep link routed through the SDK records a click).
            if (_initialized)
            {
                var props = new Dictionary<string, object>(8)
                {
                    { "url",    data.Url ?? "" },
                    { "source", data.Source.ToString().ToLowerInvariant() },
                };
                if (!string.IsNullOrEmpty(data.Path)) props["path"] = data.Path;
                if (!string.IsNullOrEmpty(data.PartnerSlug)) props["partner"] = data.PartnerSlug;

                var tracking = ParseTrackingParams(data.Url);
                if (tracking != null && tracking.Count > 0)
                {
                    props["is_reattribution"] = true;
                    foreach (var kv in tracking)
                    {
                        var key = kv.Key.Length > 30 ? kv.Key.Substring(0, 30) : kv.Key;
                        props["dl_" + key] = kv.Value;   // prefixed so it can't clash with app props
                    }
                }
                EnqueueEvent("deep_link_opened", MergeWithGlobals(props),
                             revenue: null, currency: null, txId: null, productId: null);
            }
        }

        // Pull query parameters off a deep link / tracking URL (campaign-matching
        // signal for server-side reattribution). Best-effort; never throws.
        private static Dictionary<string, string> ParseTrackingParams(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                var q = url.IndexOf('?');
                if (q < 0 || q == url.Length - 1) return null;
                var map = new Dictionary<string, string>();
                foreach (var pair in url.Substring(q + 1).Split('&'))
                {
                    var eq = pair.IndexOf('=');
                    if (eq < 1) continue;
                    var k = Uri.UnescapeDataString(pair.Substring(0, eq));
                    var v = Uri.UnescapeDataString(pair.Substring(eq + 1));
                    if (!string.IsNullOrEmpty(k)) map[k] = v;
                }
                return map;
            }
            catch { return null; }
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
            // Capture the install_uuid to delete BEFORE we wipe + regenerate it below,
            // otherwise the server request would target the fresh identity, not the old.
            var uuidToDelete = InstallUuidStore.Value;

            // Wipe LOCAL first so even if the server call fails the user's
            // device is clean — strongest GDPR posture.
            try
            {
                _queue?.WipeAll();
                InstallUuidStore.WipeAll();
                _session?.WipeAll();
                _userId = null;
                ClearGlobalProperties();
                // Reset attribution state so the wiped install isn't linked to the old one.
                _attributionCheckedThisSession = false;
                _lastAttribution = null;
                // Regenerate a FRESH anonymous identity so the SDK keeps functioning after
                // deletion (otherwise every later event ships an empty install_uuid — the
                // first-install flow is guarded by _initialized and can't re-run). Continued
                // use is a brand-new identity, unlinked from the deleted data.
                if (_initialized)
                {
                    InstallUuidStore.EnsureGenerated();
                    if (_config != null && _config.AutoSessionTracking)
                    {
                        _session = new ReflectSession(_config.SessionThresholdSeconds * 1000L);
                        BeginForegroundSession(coldStart: true);
                    }
                }
            }
            catch (Exception ex)
            {
                ReflectLogger.Error($"DeleteUserData local wipe failed: {ex}");
            }

            if (!_initialized || _config == null || _config.IsDebugMode || string.IsNullOrEmpty(uuidToDelete))
            {
                onComplete?.Invoke(true);
                return;
            }

            // Persist a pending-deletion marker so a failed request is RETRIED on the
            // next launch (was previously fire-and-forget — a failed deletion was lost,
            // a real CCPA/GDPR compliance gap). Cleared once the server confirms.
            PlayerPrefs.SetString(PREF_PENDING_DELETE, uuidToDelete);
            PlayerPrefs.Save();

            var runner = ReflectCallbackReceiver.Ensure();
            runner.StartCoroutine(_dispatcher.SendPrivacyDelete(uuidToDelete, ok =>
            {
                if (ok) { PlayerPrefs.DeleteKey(PREF_PENDING_DELETE); PlayerPrefs.Save(); }
                onComplete?.Invoke(ok);
            }));
        }

        // Retry a GDPR deletion that was requested but never confirmed by the server.
        private static void RetryPendingDeletionIfAny()
        {
            if (_config == null || _config.IsDebugMode || !PlayerPrefs.HasKey(PREF_PENDING_DELETE)) return;
            var uuid = PlayerPrefs.GetString(PREF_PENDING_DELETE, null);
            if (string.IsNullOrEmpty(uuid)) { PlayerPrefs.DeleteKey(PREF_PENDING_DELETE); return; }
            ReflectLogger.Info("Retrying a previously-unconfirmed GDPR deletion.");
            ReflectCallbackReceiver.Ensure().StartCoroutine(_dispatcher.SendPrivacyDelete(uuid, ok =>
            {
                if (ok) { PlayerPrefs.DeleteKey(PREF_PENDING_DELETE); PlayerPrefs.Save(); }
            }));
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
                                          double? revenue, string currency, string txId, string productId,
                                          string purchaseToken = null, string orderId = null,
                                          string dedupId = null, string callbackId = null,
                                          int impressionsCount = 0,
                                          IDictionary<string, object> perEventPartnerParams = null,
                                          IDictionary<string, object> callbackParams = null)
        {
            // Adjust-parity client-side de-dup: drop an event whose deduplication_id
            // was seen recently (bounded LRU), so caller double-fires / at-least-once
            // retries don't inflate counts.
            if (!string.IsNullOrEmpty(dedupId) && IsDuplicateEvent(dedupId))
            {
                ReflectLogger.Info($"Dropped duplicate '{name}' (dedup_id={dedupId}).");
                return;
            }

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
                SessionId       = _session?.SessionId,
                SessionCount    = _session?.SessionCount ?? 0,
                SubsessionCount = _session?.SubsessionCount ?? 0,
                PushToken     = _pushToken,
                ExternalDeviceId = _externalDeviceId,
                Coppa             = _config.CoppaCompliant,
                ThirdPartySharing = _thirdPartySharing,
                PartnerParams = MergePartnerParams(perEventPartnerParams),
                CallbackParams = callbackParams,
                AttStatus     = _attStatus,
                ConsentState  = _consentState,
                Device        = _deviceSnapshot,
                Referral      = _referralSnapshot,
                Revenue       = revenue,
                Currency      = currency,
                TransactionId = txId,
                PurchaseToken = purchaseToken,
                OrderId       = orderId,
                ProductId     = productId,
                DeduplicationId = dedupId,
                CallbackId    = callbackId,
                ImpressionsCount = impressionsCount,
                Properties    = props
            };
            var json = evt.ToJson();
            _queue.EnqueueRaw(json);

            var note = _config.IsDebugMode ? "no BaseUrl — not dispatched" : null;
            ReflectDebugEventLog.RecordEnqueued(evt.EventId, evt.EventName, json, note);

            ReflectLogger.Info($"Enqueued '{name}' (queue={_queue.Count})");
            _dispatcher.RequestFlushSoon();

            // On the opening event of each session (app_open on cold start, session_start
            // on a new resumed session) poll the server for attribution changes. The
            // per-session flag is reset when a new session begins, so attribution is
            // re-checked every session — not just once per process. Skipped while consent
            // is denied (the check transmits install_uuid).
            if ((name == "app_open" || name == "session_start")
                && !_attributionCheckedThisSession && !_config.IsDebugMode
                && _consentState != "denied")
            {
                _attributionCheckedThisSession = true;
                ReflectCallbackReceiver.Ensure().StartCoroutine(AttributionCheckCo());
            }
        }

        // ── Attribution check coroutine (Sprint I) ─────────────────────
        // Polls GET /attribution/check once per session on app_open. If the
        // server reports a newer attribution row, fires OnAttributionUpdated
        // and persists the new watermark so subsequent sessions only see truly
        // new changes.

        // Retry the attribution poll on transient failures (network error / 5xx /
        // timeout) with backoff, instead of silently giving up on the single most
        // common failure moment (offline cold start). 4xx is treated as permanent.
        private static IEnumerator AttributionCheckCo(Action<Dictionary<string, object>> onComplete = null)
        {
            // Load the persisted watermark so we only see genuinely new attribution.
            _lastAttributionCheckMs = PlayerPrefs.HasKey(ATTRIBUTION_CHECK_PREFS_KEY)
                ? SafeParseLong(PlayerPrefs.GetString(ATTRIBUTION_CHECK_PREFS_KEY, "0"))
                : 0;

            const int maxAttempts = 3;
            var backoff = new[] { 2f, 5f };
            Dictionary<string, object> result = null;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var queryString = "install_uuid=" + Uri.EscapeDataString(InstallUuidStore.Value)
                                + "&since=" + _lastAttributionCheckMs;
                var signature = SignHex(queryString);
                var url = _config.BaseUrl + "/attribution/check?" + queryString;
                bool transient = false;

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
                    long code = req.responseCode;
                    if (!isError && code >= 200 && code < 300)
                    {
                        result = ProcessAttributionResponse(req.downloadHandler != null ? req.downloadHandler.text : null);
                        break;   // success (whether or not attribution changed)
                    }
                    if (code >= 400 && code < 500 && code != 408 && code != 429)
                    {
                        ReflectLogger.Warn($"Attribution check {code} — not retrying.");
                        break;   // permanent client error
                    }
                    transient = true;
                    ReflectLogger.Warn($"Attribution check failed ({code}/{req.error}) — attempt {attempt + 1}/{maxAttempts}");
                }

                if (transient && attempt < maxAttempts - 1)
                    yield return new WaitForSeconds(backoff[Math.Min(attempt, backoff.Length - 1)]);
            }

            onComplete?.Invoke(result);
        }

        private static string SignHex(string payload)
        {
            if (string.IsNullOrEmpty(_config.SigningSecret)) return null;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_config.SigningSecret)))
            {
                var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
                var sb = new StringBuilder(sig.Length * 2);
                for (int i = 0; i < sig.Length; i++) sb.Append(sig[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static long SafeParseLong(string s)
        {
            return long.TryParse(s, out var v) ? v : 0L;
        }

        // Parse + cache an attribution-check response, persist the watermark, and fire
        // OnAttributionUpdated on change. Returns the attribution dict (or null).
        private static Dictionary<string, object> ProcessAttributionResponse(string responseText)
        {
            if (string.IsNullOrEmpty(responseText)) return null;
            var parsed = MiniJson.Deserialize(responseText) as IDictionary<string, object>;
            if (parsed == null) { ReflectLogger.Warn("Attribution check: bad JSON."); return null; }
            if (!parsed.TryGetValue("changed", out var changedObj) || !(changedObj is bool) || !(bool)changedObj)
            {
                ReflectLogger.Info("Attribution check: no change.");
                return null;
            }
            if (!parsed.TryGetValue("data", out var dataObj) || !(dataObj is IDictionary<string, object> dataDict))
                return null;

            var result = new Dictionary<string, object>();
            foreach (var kv in dataDict) result[kv.Key] = kv.Value;

            if (dataDict.TryGetValue("attributed_at_ms", out var attrAtMs))
            {
                long newMs = attrAtMs is long l ? l : (attrAtMs is double d ? (long)d : 0L);
                if (newMs > _lastAttributionCheckMs)
                {
                    _lastAttributionCheckMs = newMs;
                    PlayerPrefs.SetString(ATTRIBUTION_CHECK_PREFS_KEY, newMs.ToString());
                    PlayerPrefs.Save();
                }
            }

            _lastAttribution = result;   // cache for GetAttribution
            ReflectLogger.Info("Attribution change detected — firing OnAttributionUpdated.");
            try { OnAttributionUpdated?.Invoke(result); }
            catch (Exception ex) { ReflectLogger.Error($"OnAttributionUpdated handler threw: {ex}"); }
            return result;
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
            // Release the deferred cold-start app_open / session_start now that device
            // data is present — but only if no install is still pending, so on a first
            // launch app_install / app_first_open keep their place ahead of app_open
            // (when the install is still pending, the flush happens inside
            // MaybeFireInstallEvent right after the install fires).
            if (_receiver == null || !_receiver.PendingInstallEvent)
                FlushColdStartIfPending();
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

            // Now that app_install / app_first_open have fired (and device data is
            // present), release the deferred cold-start app_open / session_start so
            // they land right after — with full device data and correct ordering.
            FlushColdStartIfPending();
        }

        // The cold-start app_open / session_start are deferred at Initialize until the
        // device snapshot arrives (so they carry device data instead of a null device).
        // Idempotent: the first caller to win the flag emits the session; the rest no-op.
        // Called from OnDeviceInfoReady, MaybeFireInstallEvent, and the timeout backstop.
        private static void FlushColdStartIfPending()
        {
            if (!_coldStartPending) return;
            _coldStartPending = false;
            BeginForegroundSession(coldStart: true);
        }

        // Backstop: if native device collection never completes (stripped bridge,
        // Play Services hang), still emit the cold-start app_open after the timeout so
        // a session open is never lost — it just lands without device data, same as
        // the pre-deferral behavior.
        private static IEnumerator ColdStartTimeoutCo(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            FlushColdStartIfPending();
        }

        // LinkMe (opt-in): read the system clipboard once on first launch; if it holds
        // an http(s) URL placed there at click time, route it as a deferred deep link.
        private static void MaybeReadLinkMeClipboard()
        {
            try
            {
                var clip = GUIUtility.systemCopyBuffer;
                if (string.IsNullOrEmpty(clip)) return;
                clip = clip.Trim();
                if (clip.StartsWith("http://") || clip.StartsWith("https://"))
                {
                    ReflectLogger.Info("LinkMe: recovered a deferred deep link from the clipboard.");
                    DispatchDeepLink(new DeepLinkData
                    {
                        Url    = clip,
                        Path   = ExtractPath(clip),
                        Source = DeepLinkSource.Deferred,
                    });
                }
            }
            catch (Exception ex) { ReflectLogger.Warn($"LinkMe clipboard read failed: {ex.Message}"); }
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
                // Accumulate the foreground interval into the session length and persist.
                // We do NOT emit session_end here: whether this background is just a
                // subsession bounce or a true session end isn't known until the next
                // foreground. The session_end is emitted then (or, if the app is killed
                // while backgrounded, recovered on the next launch). This is what stops
                // brief bounces from manufacturing spurious sessions.
                _session?.Background(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                _queue.PersistToDisk();
            }
            else
            {
                // During the deferred cold-start window the FIRST foreground transition
                // IS the cold start — let FlushColdStartIfPending emit it (app_open +
                // session_start) once device data is ready. Without this guard, Unity's
                // early OnApplicationPause(false) on launch drives a session_start before
                // native device collection finishes, landing it with a null device and
                // ahead of app_install. (The cold-start timeout backstop clears the flag
                // within InstallEventTimeoutSeconds, so real resumes are never swallowed.)
                if (_coldStartPending)
                {
                    _dispatcher.RequestFlushSoon();
                    return;
                }
                BeginForegroundSession(coldStart: false);
                _dispatcher.RequestFlushSoon();
            }
        }

        /// <summary>
        /// Drive the session state machine for an app-foreground transition (cold start
        /// or resume) and emit the appropriate lifecycle events:
        ///   • a deferred <c>session_end</c> for a prior session that ended while
        ///     backgrounded / was killed without a pause,
        ///   • <c>app_open</c> on a cold start (process-launch marker), and
        ///   • <c>session_start</c> only when a genuinely NEW session begins (gap beyond
        ///     the threshold) — never on a brief subsession bounce.
        /// </summary>
        private static void BeginForegroundSession(bool coldStart)
        {
            if (!_config.AutoSessionTracking || _session == null) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var r = _session.Foreground(now);

            // A new session re-arms the attribution poll (so it runs once per session).
            if (r.NewSession) _attributionCheckedThisSession = false;

            if (r.ClosedPriorSession)
            {
                EnqueueEvent("session_end", MergeWithGlobals(new Dictionary<string, object>
                    {
                        { "session_length_ms", r.PriorSessionLengthMs },
                        { "prior_session_id",  r.PriorSessionId },
                    }), revenue: null, currency: null, txId: null, productId: null);
            }

            if (coldStart)
                TrackEvent("app_open");   // process-launch marker (Firebase parity)

            if (r.NewSession)
            {
                EnqueueEvent("session_start", MergeWithGlobals(new Dictionary<string, object>
                    {
                        { "last_interval_ms", r.LastIntervalMs },
                    }), revenue: null, currency: null, txId: null, productId: null);
            }
        }

        // Clean-quit durability backstop. Mobile backgrounding routes through
        // OnApplicationPause (which already persists), but desktop/editor quit and
        // some Android termination paths only deliver OnApplicationQuit.
        private static void OnApplicationQuitInternal()
        {
            if (!_initialized) return;
            try { _queue?.PersistToDisk(); }
            catch (Exception ex) { ReflectLogger.Warn($"Persist on quit failed: {ex.Message}"); }
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
                // Keep last_activity fresh + fold in elapsed foreground time so a crash
                // loses at most one flush interval of session length.
                _session?.Heartbeat(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
        public const string Value = "2.3.0";

        /// <summary>Human-readable tag used in logs + the debug overlay header.</summary>
        public const string FullTag = "Reflect SDK v" + Value;
    }
}
