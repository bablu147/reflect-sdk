using System;

namespace Reflect
{
    /// <summary>
    /// Configuration for the Reflect SDK. Pass to <see cref="ReflectSDK.Initialize(ReflectConfig)"/>.
    /// </summary>
    [Serializable]
    public sealed class ReflectConfig
    {
        /// <summary>
        /// Base URL of the Reflect ingestion server (Cloudflare Worker).
        /// Events will be POSTed to <c>{BaseUrl}/event</c>.
        /// Example: <c>https://api.reflect.example.com</c>
        ///
        /// <para>Leave <c>null</c> or empty to activate <b>debug mode</b>: the SDK
        /// collects device info, referral data, and events as usual, but no HTTP
        /// requests are made. A floating <b>R</b> button is drawn in-game that
        /// opens a developer inspection panel — useful for learning what the SDK
        /// captures while building the integration.</para>
        /// </summary>
        public string BaseUrl;

        /// <summary>
        /// Company key identifying the tenant (company account) this app belongs to.
        /// Required in SDK v2 whenever <see cref="BaseUrl"/> is set.
        /// Looks like <c>co_live_<hex></c> — find it in the Reflect admin panel
        /// under your company profile.
        /// </summary>
        public string CompanyKey;

        /// <summary>
        /// Public app key identifying this specific app within the company.
        /// Required whenever <see cref="BaseUrl"/> is set. Looks like
        /// <c>app_live_<hex></c>. The app must belong to the company
        /// identified by <see cref="CompanyKey"/>, or the server will
        /// reject the request with <c>401 app_company_mismatch</c>.
        /// </summary>
        public string AppKey;

        /// <summary>
        /// Shared secret used for HMAC-SHA256 signing of requests.
        /// Must be kept out of source control — use a build-time secret
        /// injection mechanism. Must match the secret configured for
        /// this app in the Reflect admin panel.
        /// </summary>
        public string SigningSecret;

        /// <summary>Enable verbose debug logging (Unity console).</summary>
        public bool EnableLogging = false;

        /// <summary>Automatically track app_open / session_start / session_end.</summary>
        public bool AutoSessionTracking = true;

        /// <summary>How many events to send per batch request. Default 50.</summary>
        public int BatchSize = 50;

        /// <summary>Maximum events held in the persistent offline queue. Default 1000.</summary>
        public int MaxQueueSize = 1000;

        /// <summary>Flush interval in seconds. Default 30.</summary>
        public float FlushIntervalSeconds = 30f;

        /// <summary>
        /// If true, the SDK will NOT read advertising identifiers (GAID/IDFA)
        /// until <see cref="ReflectSDK.SetAdvertisingConsent"/> is called with true.
        /// Recommended for apps distributed in EU/EEA/UK.
        /// </summary>
        public bool RequireAdvertisingConsent = false;

        /// <summary>
        /// If true, the SDK automatically triggers the iOS ATT prompt on first launch.
        /// If false, you must call <see cref="ReflectSDK.RequestIosTracking"/> yourself.
        /// </summary>
        public bool AutoRequestIosTracking = false;

        /// <summary>
        /// Force-enable the in-app developer overlay <b>even when a real
        /// <see cref="BaseUrl"/> is set</b>. Useful for inspecting what the
        /// SDK is POSTing to your Worker and what the Worker is returning.
        /// Gate this on <c>UnityEngine.Debug.isDebugBuild</c> so release builds
        /// never show it:
        /// <code>EnableDebugOverlay = UnityEngine.Debug.isDebugBuild</code>
        /// The overlay <i>always</i> appears in debug mode (null BaseUrl),
        /// regardless of this flag.
        /// </summary>
        public bool EnableDebugOverlay = false;

        /// <summary>
        /// Auto-capture unhandled exceptions / asserts as a <c>_crash</c> event.
        /// Throttled to one capture per minute so a tight crash loop can't flood
        /// the queue. Default true — devs almost always want crash signal.
        /// </summary>
        public bool AutoCaptureCrashes = true;

        /// <summary>True when <see cref="BaseUrl"/> is null/empty — the SDK runs
        /// locally with the developer overlay and never makes network requests.</summary>
        internal bool IsDebugMode => string.IsNullOrEmpty(BaseUrl);

        /// <summary>True when the overlay should be attached (debug mode OR explicit opt-in).</summary>
        internal bool IsOverlayEnabled => IsDebugMode || EnableDebugOverlay;

        internal void Validate()
        {
            // BaseUrl is OPTIONAL. Null/empty → debug mode (see <see cref="IsDebugMode"/>).
            if (!string.IsNullOrEmpty(BaseUrl))
            {
                if (!BaseUrl.StartsWith("http://") && !BaseUrl.StartsWith("https://"))
                    throw new ArgumentException("ReflectConfig.BaseUrl must start with http:// or https://");
                BaseUrl = BaseUrl.TrimEnd('/');

                // SDK v2: CompanyKey + AppKey are required whenever we're talking
                // to a real server. In debug mode (empty BaseUrl) they're both
                // optional so newcomers can try the SDK without setup.
                if (string.IsNullOrEmpty(CompanyKey))
                    throw new ArgumentException("ReflectConfig.CompanyKey is required when BaseUrl is set (SDK v2). Get it from your Reflect admin panel.");
                if (string.IsNullOrEmpty(AppKey))
                    throw new ArgumentException("ReflectConfig.AppKey is required when BaseUrl is set. Get it from your Reflect admin panel → Apps.");
                if (string.IsNullOrEmpty(SigningSecret))
                    throw new ArgumentException("ReflectConfig.SigningSecret is required when BaseUrl is set. Never commit this to source control.");
            }
            if (BatchSize <= 0) BatchSize = 50;
            if (MaxQueueSize <= 0) MaxQueueSize = 1000;
            if (FlushIntervalSeconds < 1f) FlushIntervalSeconds = 1f;
        }
    }

    /// <summary>iOS App Tracking Transparency status.</summary>
    public enum IosTrackingStatus
    {
        NotDetermined = 0,
        Restricted    = 1,
        Denied        = 2,
        Authorized    = 3,
        /// <summary>SDK couldn't reach the ATT framework (e.g. pre-iOS 14).</summary>
        Unavailable   = 99
    }
}
