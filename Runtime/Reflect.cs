// ────────────────────────────────────────────────────────────────────────────
//  Reflect SDK for Unity — designed and built by Bablu.
//  Mobile measurement / attribution SDK with zero third-party dependencies.
//  See README.md §10 for credits & license.
// ────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using Reflect.Internal;
using Reflect.Internal.Debug;
using Reflect.Internal.Platform;
using UnityEngine;

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
        private static IosTrackingStatus _attStatus = IosTrackingStatus.NotDetermined;
        private static bool _advertisingConsent = true;
        private static readonly Dictionary<string, object> _globalProps = new Dictionary<string, object>();
        private static readonly object _globalPropsLock = new object();

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
                _platform.Initialize(_receiver.gameObject.name, _advertisingConsent);
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

                if (InstallUuidStore.IsFirstLaunch)
                {
                    // Actual app_install event will be fired once device info + referral arrive.
                    _receiver.PendingInstallEvent = true;
                }

                if (config.AutoSessionTracking)
                    TrackEvent("app_open");

                if (config.AutoRequestIosTracking && Application.platform == RuntimePlatform.IPhonePlayer)
                    RequestIosTracking(null);
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
        /// </summary>
        private static IDictionary<string, object> MergeWithGlobals(IDictionary<string, object> perEvent)
        {
            lock (_globalPropsLock)
            {
                if (_globalProps.Count == 0) return perEvent;
                var merged = new Dictionary<string, object>(_globalProps.Count + (perEvent?.Count ?? 0));
                foreach (var kv in _globalProps) merged[kv.Key] = kv.Value;
                if (perEvent != null)
                    foreach (var kv in perEvent) merged[kv.Key] = kv.Value;   // per-event overrides
                return merged;
            }
        }

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
                AttStatus     = _attStatus,
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

        private static void MaybeFireInstallEvent()
        {
            if (!_receiver.PendingInstallEvent) return;
            if (_deviceSnapshot == null) return;
            // Referral may legitimately be null on iOS / organic installs — don't block.
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
            if (paused)
            {
                if (_config.AutoSessionTracking) TrackEvent("session_end");
                _queue.PersistToDisk();
            }
            else
            {
                if (_config.AutoSessionTracking) TrackEvent("session_start");
                _dispatcher.RequestFlushSoon();
            }
        }

        // Throttle: at most one _crash event per minute even if the app is
        // throwing in a tight loop — protects R2 + queue from runaway logs.
        private static double _lastCrashAtMs;
        private const double CRASH_RATE_LIMIT_MS = 60_000;

        private static void OnUnityLogMessage(string condition, string stack, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Assert) return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastCrashAtMs < CRASH_RATE_LIMIT_MS) return;
            _lastCrashAtMs = now;

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
