// ────────────────────────────────────────────────────────────────────────────
//  Reflect SDK — AppLovin MAX ad-revenue integration.
//  Automatically forwards MAX impression-level revenue data to Reflect as
//  _ad_impression events. Enable with ReflectMaxIntegration.Enable() after
//  both Reflect and MAX have been initialized.
//
//  Compile guard: define REFLECT_MAX in Player Settings → Scripting Define
//  Symbols when the AppLovin MAX SDK is present in the project.
// ────────────────────────────────────────────────────────────────────────────

#if REFLECT_MAX

using System;

namespace Reflect.Integrations
{
    /// <summary>
    /// Bridges AppLovin MAX impression-level revenue callbacks into Reflect
    /// <c>_ad_impression</c> events via <see cref="ReflectSDK.TrackAdRevenue"/>.
    ///
    /// Usage:
    /// <code>
    /// ReflectSDK.Initialize(config);
    /// MaxSdk.SetSdkKey("YOUR_KEY");
    /// MaxSdk.InitializeSdk();
    /// ReflectMaxIntegration.Enable();
    /// </code>
    /// </summary>
    public static class ReflectMaxIntegration
    {
        private static bool _enabled;

        /// <summary>
        /// Subscribe to all MAX ad-revenue callbacks (interstitial, rewarded, banner, MREC).
        /// Safe to call multiple times — subsequent calls are no-ops.
        /// </summary>
        public static void Enable()
        {
            if (_enabled) return;
            _enabled = true;

            MaxSdkCallbacks.Interstitial.OnAdRevenuePaidEvent += OnInterstitialRevenuePaid;
            MaxSdkCallbacks.Rewarded.OnAdRevenuePaidEvent     += OnRewardedRevenuePaid;
            MaxSdkCallbacks.Banner.OnAdRevenuePaidEvent       += OnBannerRevenuePaid;
            MaxSdkCallbacks.MRec.OnAdRevenuePaidEvent         += OnMRecRevenuePaid;
        }

        /// <summary>
        /// Unsubscribe from all MAX ad-revenue callbacks.
        /// </summary>
        public static void Disable()
        {
            if (!_enabled) return;
            _enabled = false;

            MaxSdkCallbacks.Interstitial.OnAdRevenuePaidEvent -= OnInterstitialRevenuePaid;
            MaxSdkCallbacks.Rewarded.OnAdRevenuePaidEvent     -= OnRewardedRevenuePaid;
            MaxSdkCallbacks.Banner.OnAdRevenuePaidEvent       -= OnBannerRevenuePaid;
            MaxSdkCallbacks.MRec.OnAdRevenuePaidEvent         -= OnMRecRevenuePaid;
        }

        // ── Callbacks ────────────────────────────────────────────────────

        private static void OnInterstitialRevenuePaid(string adUnitId, MaxSdkBase.AdInfo adInfo)
            => TrackFromAdInfo(adInfo, adUnitId, "interstitial");

        private static void OnRewardedRevenuePaid(string adUnitId, MaxSdkBase.AdInfo adInfo)
            => TrackFromAdInfo(adInfo, adUnitId, "rewarded");

        private static void OnBannerRevenuePaid(string adUnitId, MaxSdkBase.AdInfo adInfo)
            => TrackFromAdInfo(adInfo, adUnitId, "banner");

        private static void OnMRecRevenuePaid(string adUnitId, MaxSdkBase.AdInfo adInfo)
            => TrackFromAdInfo(adInfo, adUnitId, "mrec");

        // ── Shared helper ────────────────────────────────────────────────

        private static void TrackFromAdInfo(MaxSdkBase.AdInfo adInfo, string adUnitId, string adFormat)
        {
            if (adInfo == null) return;

            ReflectSDK.TrackAdRevenue(
                mediationPlatform: "applovin_max",
                revenue:           adInfo.Revenue,
                currency:          "USD",                           // MAX always reports in USD
                adFormat:          adFormat,
                adNetwork:         adInfo.NetworkName,
                adUnitId:          adUnitId,
                placement:         adInfo.Placement,
                precision:         adInfo.RevenuePrecision
            );
        }
    }
}

#endif
