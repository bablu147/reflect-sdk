# Reflect SDK for Unity

**Reflect · v2.0.0** · Proprietary and confidential

> **Breaking change in v2.0.0:** `ReflectConfig.CompanyKey` is now required
> whenever `BaseUrl` is set. Upgrading from v1 → v2? See §10 at the bottom.

The Reflect SDK collects device + referral data on Android and iOS, queues
events offline, and sends HMAC-signed batches to the Reflect ingestion
server. One lightweight C# drop-in, no third-party attribution SDKs under
the hood.

- **No third-party dependencies** (no Firebase, no AppsFlyer)
- Offline-first event queue (survives app kill)
- HMAC-SHA256 signed requests
- GDPR / iOS ATT consent hooks
- Unity **2021.3+**, Android **minSdk 21**, iOS **12+**

---

## 1. Install

Reflect is a proprietary SDK distributed only to invited customers. Your
Reflect operator will share the `reflect-sdk/` folder (or a signed UPM
tarball). Drop it into `Assets/Reflect/` inside your Unity project.

---

## 2. Android setup

Required libraries (added via Gradle — see
[`Plugins/Android/REFLECT_GRADLE_SETUP.md`](Plugins/Android/REFLECT_GRADLE_SETUP.md)):

```gradle
implementation 'com.google.android.gms:play-services-ads-identifier:18.0.1'
implementation 'com.android.installreferrer:installreferrer:2.2'
```

Player Settings → Android:

- **minSdkVersion:** 21+
- **targetSdkVersion:** 33+
- **Scripting Backend:** IL2CPP
- **Target Architectures:** ARMv7 + ARM64
- **Custom Proguard File:** ✅ **required for release builds.** Minified (R8)
  release builds strip `com.reflect.sdk.**` — the native bridge for device-info
  **and** install-referrer collection — so `app_install` never fires and
  installs/attribution disappear (a debug APK won't reveal this). The plugin
  ships as Java source, so its keep-rules are **not** auto-applied; enabling this
  makes Unity apply the bundled `Plugins/Android/proguard-user.txt`. See
  [`Plugins/Android/REFLECT_GRADLE_SETUP.md`](Plugins/Android/REFLECT_GRADLE_SETUP.md)
  → "Release builds".

> The `AD_ID` permission (for the GAID) is declared by the SDK manifest and
> merges automatically — **you don't add it** (remove it only for COPPA/kids
> apps). You **do** declare advertising-ID usage in **Play Console → App content
> → Advertising ID** and **Data safety** (Device or other IDs).

---

## 3. iOS setup

Everything is handled automatically by
[`Editor/ReflectBuildPostProcessor.cs`](Editor/ReflectBuildPostProcessor.cs):

- Adds `AdSupport.framework`
- Weakly links `AppTrackingTransparency.framework` (iOS 14+)
- Weakly links `AdServices.framework` (iOS 14.3+, Apple Search Ads)
- Adds `NSUserTrackingUsageDescription` to Info.plist

To customise the ATT prompt string:

```csharp
// In any editor script run before the build
Reflect.Editor.ReflectBuildPostProcessor.AttUsageDescription =
    "We use your identifier to credit the creator who referred you.";
```

---

## 4. Minimal integration

```csharp
using Reflect;
using System.Collections.Generic;
using UnityEngine;

public class Bootstrap : MonoBehaviour
{
    void Awake()
    {
        DontDestroyOnLoad(gameObject);

        ReflectSDK.Initialize(new ReflectConfig {
            BaseUrl              = "https://reflect.yourdomain.com",
            CompanyKey           = "co_live_<hex>",        // from admin → Settings
            AppKey               = "app_live_<hex>",       // from admin → Apps
            SigningSecret        = "your-shared-secret",   // keep out of source control in prod
            EnableLogging        = Debug.isDebugBuild,
            AutoSessionTracking  = true,
            AutoRequestIosTracking = true
        });
    }

    public void OnSignUp(string userId)
    {
        ReflectSDK.SetUserId(userId);
        ReflectSDK.TrackEvent("sign_up", new Dictionary<string, object> {
            { "method", "google" }
        });
    }

    public void OnPurchase(string sku, double price, string currency, string txId)
    {
        ReflectSDK.TrackPurchase(sku, price, currency, txId);
    }
}
```

The SDK automatically fires `app_install` on first launch once the Install
Referrer has been read. Every subsequent event inherits the same `install_uuid`
so your server can stitch them back to the original click.

---

## 4b. Developer overlay — debug mode & production inspection

The SDK ships with an in-game developer overlay that runs in **two modes**.
Both modes show a draggable floating **R** button; tapping it opens a 6-tab
inspection panel.

### Mode 1 — Debug mode (no backend required)

Call `Initialize` **without** a `BaseUrl` (pass `null` or leave the field
empty) and the SDK switches into debug mode:

- Device + referral collection runs exactly as in production
- Events are enqueued and serialized exactly as in production
- **No HTTP requests are made** — nothing is sent anywhere
- The overlay shows a **red DEBUG MODE** banner

### Mode 2 — Production inspection mode

Set a real `BaseUrl` **and** flip `EnableDebugOverlay = true` on your config.
The SDK runs normally (events are dispatched to your Reflect account) **and**
the overlay is available so you can see exactly what went over the wire and
what came back:

- Everything from production mode (sending, retries, backoff)
- The overlay shows a **yellow INSPECTION MODE** banner
- The new **Network** tab shows every HTTP request/response pair (last 30)

```csharp
ReflectSDK.Initialize(new ReflectConfig {
    BaseUrl             = "https://reflect.yourdomain.com",
    AppKey              = "app_live_abc123",
    SigningSecret       = "your-shared-secret",
    // Gate on isDebugBuild so release builds NEVER get the overlay:
    EnableDebugOverlay  = UnityEngine.Debug.isDebugBuild,
});
```

### Tabs

| Tab | Shows |
|---|---|
| **Overview** | SDK version, install UUID, first-launch flag, ATT status, queue size, session counters, app bundle / version / device summary |
| **Device** | Every field of `DeviceSnapshot` (IDFA / GAID masked), grouped by Identifiers / OS / App / Locale / Network / Fraud signals |
| **Referral** | Source, raw referrer string, timestamps, parsed query params, iOS AdServices token |
| **Events** | Last ~100 events — tap any row to expand its full JSON payload |
| **Network** | Last 30 HTTP round-trips: timestamp, method + path, HTTP code, duration, batch size — tap any row to see full URL, request headers, prettified request body, response body, and error detail. Status-colored: 🟢 2xx, 🟠 4xx, 🔴 5xx / transport error, ⚫ pending. `X-Reflect-Signature` is masked in the header list |
| **Logs** | Last ~500 SDK log lines, color-coded by level, with auto-scroll + clear |

```csharp
// Debug-mode drop-in for development / onboarding a new teammate:
ReflectSDK.Initialize(new ReflectConfig { BaseUrl = null });
// …or simply:
ReflectSDK.Initialize((string)null);

// Track as normal — events are visible in the overlay but never leave the device.
ReflectSDK.TrackEvent("level_complete", new Dictionary<string, object> {
    { "level_id", 3 }, { "stars", 2 }
});
```

Check `ReflectSDK.IsDebugMode` from game code to, e.g., hide gameplay UI
while the overlay is visible.

> **Ship safety:** the overlay is intentionally obtrusive so you never forget
> to turn it off before shipping. Debug mode is automatic when `BaseUrl`
> is empty; production inspection is opt-in via `EnableDebugOverlay`. Always
> gate `EnableDebugOverlay` on `UnityEngine.Debug.isDebugBuild` so release
> builds never show the floating button.

---

## 5. Event JSON format

Each POST body to `{BaseUrl}/event`:

```json
{
  "events": [
    {
      "event_id": "d3f5c...",
      "event_name": "app_install",
      "event_ts_ms": 1744779300000,
      "install_uuid": "6ff0...",
      "user_id": null,
      "sdk_version": "1.0.0",
      "att_status": "Authorized",

      "device": {
        "gaid": "...", "idfa": null, "idfv": null, "android_id": "...",
        "os": "Android", "os_version": "14", "api_level": 34,
        "device_model": "Pixel 7", "device_manufacturer": "Google",
        "screen_width": 1080, "screen_height": 2400,
        "app_bundle_id": "com.your.game", "app_version": "1.0.0",
        "first_install_time": 1744779000000, "install_source": "com.android.vending",
        "language": "en", "locale": "en_IN", "timezone": "Asia/Kolkata",
        "is_emulator": false, "is_rooted": false,
        "connection_type": "wifi", "carrier": "Jio"
      },

      "referral": {
        "raw": "click_id=abc123&pub=xyz",
        "click_ts": 1744779200, "install_ts": 1744779250,
        "source": "play_install_referrer"
      },

      "props": { }
    }
  ],
  "sent_at_ms": 1744779301234
}
```

Headers:

| Header | Value |
|---|---|
| `Content-Type` | `application/json` |
| `X-Reflect-Sdk` | SDK version (e.g. `2.0.0`) |
| `X-Reflect-Company-Key` | Your company key (SDK v2 required) |
| `X-Reflect-App-Key` | Your app key |
| `X-Reflect-Signature` | Hex HMAC-SHA256 of the raw body, keyed with `SigningSecret` |

---

## 6. Permissions & privacy

### What the SDK handles automatically

| Platform | Item | How |
|---|---|---|
| Android | `INTERNET`, `ACCESS_NETWORK_STATE`, `AD_ID`, `<queries>` | Declared in the SDK's merged `AndroidManifest.xml` |
| Android | Respects `limitAdTracking` flag | GAID not read when limit-ad-tracking is on |
| iOS | `NSUserTrackingUsageDescription` | Injected by `ReflectBuildPostProcessor` |
| iOS | `AdSupport`, `AppTrackingTransparency`, `AdServices` frameworks | Linked (weak for ATT / AdServices) |
| iOS | `PrivacyInfo.xcprivacy` | SDK-level manifest copied into Xcode project |
| iOS | ATT prompt + IDFA gating | Shown via `ReflectSDK.RequestIosTracking(...)` |

### What YOU must still do

1. **GDPR consent dialog (EU/EEA/UK users).** The SDK exposes
   `RequireAdvertisingConsent = true` and `ReflectSDK.SetAdvertisingConsent(bool)`
   but does **not** ship a consent UI. Use a CMP (Google UMP, Didomi, OneTrust,
   or your own screen) and call `SetAdvertisingConsent(true)` only after
   explicit opt-in.
2. **Play Store → Data Safety form.** At submission, declare:
   - *Device or other IDs*: GAID, Android ID — collected, linked, used for analytics + advertising
   - *App activity*: in-app actions — collected, linked, analytics
   - *App info and performance*: diagnostics — collected, linked, analytics
   - *Purchase history*: collected, linked, analytics
3. **App Store Connect → App Privacy.** Mirror the categories declared in
   `PrivacyInfo.xcprivacy`: Device ID, Product Interaction, Purchase History,
   (Coarse Location if your backend does IP geo-lookup).
4. **Add your own `PrivacyInfo.xcprivacy` at the app level.** The one bundled
   with this SDK only covers what *this SDK* collects. Your app needs its own
   manifest at the root of the main bundle too.
5. **Add your attribution domain to `NSPrivacyTrackingDomains`** in *both*
   your app-level and the SDK's privacy manifest (the SDK ships an empty
   array — add e.g. `reflect.yourdomain.com`).
6. **Kids / COPPA apps**: remove the `<uses-permission
   android:name="com.google.android.gms.permission.AD_ID"/>` line from
   `Plugins/Android/AndroidManifest.xml` and set `RequireAdvertisingConsent =
   true`. Do not call `SetAdvertisingConsent(true)` for users under 13.

### Behavior at a glance

- The SDK never reads the IDFA until ATT returns `Authorized`.
- The SDK never reads the GAID if `RequireAdvertisingConsent=true` and
  consent has not been granted.
- Events are always tagged with `install_uuid` — even without IDFA/GAID
  your server can stitch install → signup → purchase via this ID.
- No runtime permission prompts are needed on Android for anything the SDK
  collects (`INTERNET` / `ACCESS_NETWORK_STATE` / `AD_ID` are all normal
  permissions, granted at install time).

---

## 7. What's in the box

```
com.reflect.sdk/
├── Runtime/
│   ├── Reflect.cs                   # Public API
│   ├── ReflectConfig.cs
│   ├── ReflectCallbackReceiver.cs   # Native ↔ Unity bridge
│   ├── Models/
│   │   ├── DeviceSnapshot.cs
│   │   ├── ReferralSnapshot.cs
│   │   └── ReflectEvent.cs
│   ├── Internal/
│   │   ├── EventQueue.cs            # Offline queue, disk-persistent
│   │   ├── HttpDispatcher.cs        # Batched POST, HMAC, backoff
│   │   ├── InstallUuidStore.cs
│   │   ├── JsonWriter.cs
│   │   ├── MiniJson.cs              # Zero-dep JSON parser
│   │   ├── ReflectLogger.cs
│   │   └── Platform/
│   │       ├── IPlatformBridge.cs
│   │       ├── AndroidPlatformBridge.cs
│   │       ├── IOSPlatformBridge.cs
│   │       └── EditorPlatformBridge.cs
│   └── Debug/
│       ├── ReflectLogBuffer.cs      # 500-entry ring for in-app log view
│       ├── ReflectDebugEventLog.cs  # 100-entry ring for event inspection
│       ├── ReflectNetworkLog.cs     # 30-entry ring of HTTP req/resp pairs
│       └── ReflectDebugOverlay.cs   # IMGUI floating button + tabbed panel
├── Plugins/
│   ├── Android/
│   │   ├── AndroidManifest.xml
│   │   ├── REFLECT_GRADLE_SETUP.md
│   │   └── com/reflect/sdk/
│   │       ├── ReflectBridge.java        # Entry point
│   │       ├── DeviceInfoCollector.java
│   │       ├── ReferralCollector.java
│   │       ├── EmulatorDetector.java
│   │       └── RootDetector.java
│   └── iOS/
│       ├── ReflectBridge.h
│       ├── ReflectBridge.mm
│       └── PrivacyInfo.xcprivacy          # Apple privacy manifest
├── Editor/
│   ├── Reflect.Editor.asmdef
│   └── ReflectBuildPostProcessor.cs      # iOS frameworks + Info.plist
├── Samples~/
│   └── BasicUsage/ReflectBootstrap.cs
├── link.xml                              # Prevent IL2CPP stripping
└── package.json
```

---

## 8. Troubleshooting

| Symptom | Fix |
|---|---|
| `ReflectSDK API called before Initialize` | Move `ReflectSDK.Initialize` into the first scene's `Awake`. |
| Floating **R** button visible in-game | Either `BaseUrl` is null/empty (debug mode) OR `EnableDebugOverlay = true`. Set a real URL and/or `EnableDebugOverlay = false` (or gate on `Debug.isDebugBuild`) to hide. |
| `Network` tab shows "No requests yet" | Dispatcher flushes on its interval (`FlushIntervalSeconds`, default 30s) or when the queue hits `BatchSize`. Call `ReflectSDK.Flush()` to force a send. |
| No `app_install` fires in Editor | Editor bridge returns a mocked snapshot — look for "Enqueued 'app_install'" in console. |
| Android build fails on `AdvertisingIdClient` | Missing Gradle dep — see `REFLECT_GRADLE_SETUP.md`. |
| iOS build fails on `ATTrackingManager` | Enable `Custom Player Settings → Target iOS 12+`; frameworks are added weak-linked. |
| `IL2CPP stripped HttpDispatcher` | Ensure `link.xml` shipped with the package is present. |

---

## 9. License

- Every event carries the SDK version in the `sdk_version` field and the
  `X-Reflect-Sdk` request header, so your ingestion pipeline always knows
  which build of the SDK it's talking to.
- The in-game debug overlay (see §4b) shows the Reflect SDK version and
  your tenant name in its header bar.

**License: Proprietary.** This SDK is licensed to your company for use
within the apps you identify to Reflect via `AppKey`. Redistribution,
reverse-engineering, or use outside the invited account is prohibited.
Questions about licensing, contact your Reflect account manager.

---

## 9a. What's new in v2.2

Adjust-level data-collection parity + a hard fix for the #1 integration failure.

- **Release builds can no longer silently break attribution.** A Unity Editor
  post-processor auto-applies the ProGuard keep-rules + Google deps to the Android
  build, so R8 can never strip the native bridge (the failure that made installs
  vanish in minified Play Store builds). No manual *Custom Proguard File* step.
- **`app_install` is never black-holed** — a configurable timeout fallback fires it
  even if native device/referral collection stalls; `app_version` is captured in
  pure C# so it survives native failures.
- **Broad signal parity:** device taxonomy (type/os_build/screen/ui_mode/…), App Set
  ID, Fire ID, GAID source/attempt, China IMEI/MEID/OAID (opt-in), session length,
  `environment` (prod/sandbox), foreground state, push token, external device id.
- **New runtime APIs:** `SetPushToken`, `SetExternalDeviceId`, `SetEnabled`,
  `SetOfflineMode`, `SetThirdPartySharing`, global partner parameters (see §9b).
- **Server-side deferred deep-link resolution** (fingerprint match) + SKAN
  auto-registration on launch.
- All new identifiers are consent-gated and scrubbed server-side on denial.

Upgrade: just bump the package tag — no API breakages; existing calls keep working.

---

## 9b. What's new in v2.1

This release closes the biggest MMP-parity gaps from the v2.0 audit. Every
addition was designed to **never increase Cloudflare cost per install** —
some of them actively reduce bandwidth.

### New SDK APIs

```csharp
// Standard event vocabulary (50+ helpers, AppsFlyer/Firebase parity)
ReflectStandardEvents.SignUpWith("google");
ReflectStandardEvents.LevelAchieved(7);
ReflectStandardEvents.AddedToCart("sku_123", 9.99, "USD");
ReflectStandardEvents.AdShown("admob", "rewarded", revenue: 0.012, currencyCode: "USD");
// (see ReflectStandardEvents.cs for the full list)

// Global properties — merged into every event automatically
ReflectSDK.SetGlobalProperty("user_tier", "premium");
ReflectSDK.SetGlobalProperty("ab_variant", "B");
ReflectSDK.UnsetGlobalProperty("ab_variant");
ReflectSDK.ClearGlobalProperties();

// Audience tagging — for cohort filtering in reports
ReflectSDK.SetAudience("paying", "whale_v3");

// GDPR / CCPA right-to-be-forgotten — wipes local data + queues server cascade
ReflectSDK.DeleteUserData(success => Debug.Log("Deletion queued: " + success));

// Receipt validation — pass the StoreKit/Play receipt blob
ReflectSDK.TrackPurchase("sku_pro", 9.99, "USD", txId, receiptData: receipt);

// Anonymous → known user stitching — fires once per anon→known transition
ReflectSDK.SetUserId("user_42");        // Auto-fires _user_alias

// Deep linking
ReflectSDK.OnDeepLink += data => Router.Navigate(data.Path);
ReflectSDK.HandleDeepLink(url, isCold: true);     // wire from Activity / AppDelegate
// Deferred deep links (from install referrer or AdServices) fire automatically.

// Crash auto-capture — on by default, throttled to 1 per minute
new ReflectConfig { AutoCaptureCrashes = false };  // opt out if you have your own
```

### Adjust-parity APIs

```csharp
// Re-engagement push token (FCM/APNS) — attached to every event as push_token.
// Pass the token your app already obtains; no messaging dependency is bundled.
ReflectSDK.SetPushToken(fcmOrApnsToken);

// Customer-owned external device id — join Reflect data to your own backend.
ReflectSDK.SetExternalDeviceId("your_device_ref");

// Enable/disable all tracking at runtime (events neither recorded nor sent).
ReflectSDK.SetEnabled(false);

// Offline mode — keep recording into the persistent queue, hold dispatch.
ReflectSDK.SetOfflineMode(true);   // flush by calling SetOfflineMode(false)

// Third-party data-sharing opt-in (reported as third_party_sharing per event).
ReflectSDK.SetThirdPartySharing(false);

// Partner parameters — key/values forwarded to ad-network partners (partner_params),
// distinct from SetGlobalProperty (which goes into props / your own callbacks).
ReflectSDK.AddGlobalPartnerParameter("pub_id", "12345");
ReflectSDK.RemoveGlobalPartnerParameter("pub_id");
```

```csharp
// Config-time flags
new ReflectConfig {
    Environment   = "sandbox",   // "production" (default) | "sandbox" — excluded from billing/revenue
    CoppaCompliant = true,       // child-directed: reports ff_coppa, suppress ad ids
    CollectImei    = true,       // CHINA opt-in (Android, needs READ_PHONE_STATE; OS-blocked on 13+)
    CollectOaid    = true,       // CHINA opt-in (needs the MSA/Huawei OAID SDK)
    InstallEventTimeoutSeconds = 5f,  // backstop so app_install always fires
};
```

Beyond these, the SDK now also collects (in `device`): `device_type`, `os_build`, `hardware_name`,
`screen_size`/`screen_format`, `ui_mode`, `is_system_app`, `gaid_source`/`gaid_attempt`, the Google
**App Set ID**, **Fire ID**, and (opt-in) **IMEI/MEID/OAID** — and on the envelope `environment`,
`is_foreground`, `session_length_ms`. All identifiers are consent-gated and scrubbed on denial.

### New event names (auto-recognized by the server)

`app_first_open`, `sign_up`, `login`, `tutorial_begin`, `tutorial_complete`,
`level_start`, `level_up`, `level_complete`, `achievement_unlocked`,
`view_item`, `search`, `share`, `rate`, `add_to_cart`, `begin_checkout`,
`start_trial`, `trial_converted`, `subscription_renewed`,
`subscription_cancelled`, `subscription_refunded`, `ad_impression`,
`ad_click`, `deep_link_opened`, `_user_alias`, `_set_audience`, `_crash`.

### Transport improvements

- **Gzip on batches ≥10 events** — typically 80% bandwidth reduction. The
  Worker decompresses after HMAC verify (signature is over wire bytes).
  Direct Cloudflare cost saving on R2 audit + ingress.
- **Event validator** — drops bad events client-side (length, prop count,
  type) so we don't waste local queue / R2 storage on rejected payloads.
- **ProGuard keep-rules** — shipped at `Plugins/Android/proguard-user.txt`.
  ⚠️ Because the plugin ships as Java source (not an `.aar`), these are **not**
  auto-applied — enable **Player → Android → Publishing Settings → Custom
  Proguard File** so R8 doesn't strip the native bridge on release builds.
  (`consumer-rules.pro` is kept for reference / AAR-based setups.)
- **Resilient `app_install`** — the install event now fires on a short timeout
  (`ReflectConfig.InstallEventTimeoutSeconds`, default 5s) even if native
  device/referrer collection stalls or is stripped, so an install is never lost.
- **`app_version` on every event** — captured in pure C# (`Application.version`),
  so it survives even when native collection is unavailable; promoted to a
  queryable `events.app_version` column server-side (plus `{app_version}` postback
  macro).
- **Adjust-parity signal expansion** — the SDK now also collects `device_type`,
  `os_build`, `hardware_name`, `screen_size`/`screen_format`, `ui_mode`,
  `is_system_app`, `gaid_source`, and the Google **App Set ID** (Android 12+,
  optional `play-services-appset` dep); iOS reports `device_type`. New envelope
  signals: `environment` (`ReflectConfig.Environment` — `"sandbox"` excluded from
  billing), `is_foreground` (lifecycle), and `push_token` via
  `ReflectSDK.SetPushToken(token)` (pass your FCM/APNS token — no messaging
  dependency bundled). All consent-gated and scrubbed on denial. Server promotes
  the common dashboard dims (device_type, environment, is_foreground, app_set_id,
  push_token, install_store, ad_network/placement) to `events` columns and rolls
  up engagement (`aggregates_sessions`) + ad revenue (`aggregates_ad_revenue`).

### Server-side additions (no migration required from your end)

- New endpoint `POST /privacy/delete` (called by `DeleteUserData()`).
- New endpoint `POST /skan-postback` (Apple SKAdNetwork — register it in
  your iOS app's `Info.plist` once `NSAdvertisingAttributionReportEndpoint`
  routing is configured).
- `tracking_links.deep_link_path` — set in admin panel; SDK consumes
  via deferred deep link mechanism.
- Receipt validation cache (`receipt_validations` table) keyed by
  `transaction_id`. Heavy KV reuse so repeat purchases don't re-hit Apple/Google.
- Daily retention cohort rollup — populates `retention_cohorts` table.
- Privacy deletion cron — drains queued deletion requests in nightly batches
  of ≤25 requests × ≤1000 rows/table to keep D1 row writes bounded.

### Upgrading from 2.0 → 2.1

Just bump the SDK package — no API breakages. Your existing `TrackEvent` /
`TrackPurchase` calls keep working exactly as before. The new APIs are all
additive; opt in as you need them.

---

## 10. Migrating from v1.x → v2.0.0

v2 is invite-only SaaS — every integration now belongs to a **company**
(tenant) in addition to its existing **app**. Concretely:

### What changed

- `ReflectConfig.CompanyKey` is a new **required** field when `BaseUrl`
  is set. Format: `co_live_<hex>` — find it in the admin panel at
  `/settings`.
- `ReflectConfig.AppKey` is also now required when `BaseUrl` is set (was
  optional in v1 — some v1 setups relied on BaseUrl-based routing).
- Every `POST /event` now sends a new header: `X-Reflect-Company-Key: <key>`.
- Server rejects requests where the app doesn't belong to the company
  with `401 app_company_mismatch`.
- Server rejects suspended companies with `401 company_suspended`.
- `X-Reflect-Sdk` bumped from `1.0.0` → `2.0.0`.

### What DIDN'T change

- HMAC signing mechanics (still SHA-256 over raw body, hex-encoded).
- Event payload shape — `device`, `referral`, `props` all unchanged.
- Install UUID persistence / offline queue / retry semantics.
- Play Install Referrer + AdServices attribution paths.
- The developer overlay.

### Upgrade steps

1. Open your Reflect admin panel → **Settings** → copy your `CompanyKey`.
2. In your Unity project, add one line to your `ReflectConfig`:
   ```csharp
   CompanyKey = "co_live_<paste>",
   ```
3. Ship a new build. You're done.

### Backward compatibility

For **90 days** following v2 release, the server accepts v1 SDK traffic
(missing `X-Reflect-Company-Key`) and logs a `sdk_v1_deprecated` warning.
After that window the server starts returning `400 missing_auth_headers`
for v1 traffic — upgrade clients before then.

You can monitor v1 traffic via `wrangler tail` — grep for
`sdk_v1_deprecated`.
