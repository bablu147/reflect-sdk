// ────────────────────────────────────────────────────────────────────────────
//  Reflect SDK — Google AdMob ad-revenue integration.
//  Unlike MAX and LevelPlay, AdMob does not expose a single global callback
//  for impression-level revenue. Instead, each ad object fires its own
//  OnAdPaid event. This helper provides a one-liner that developers call
//  from their existing OnAdPaid handlers.
//
//  Compile guard: define REFLECT_ADMOB in Player Settings → Scripting Define
//  Symbols when the Google Mobile Ads Unity plugin is present.
// ────────────────────────────────────────────────────────────────────────────

#if REFLECT_ADMOB

using System;
using GoogleMobileAds.Api;

namespace Reflect.Integrations
{
    /// <summary>
    /// Helper for forwarding Google AdMob impression-level revenue to Reflect
    /// as <c>_ad_impression</c> events.
    ///
    /// Usage — wire up each ad's OnAdPaid callback:
    /// <code>
    /// bannerView.OnAdPaid += (AdValue adValue) =>
    /// {
    ///     ReflectAdMobIntegration.TrackAdMobRevenue(adValue, bannerView.GetAdUnitID(), "banner");
    /// };
    ///
    /// interstitialAd.OnAdPaid += (AdValue adValue) =>
    /// {
    ///     ReflectAdMobIntegration.TrackAdMobRevenue(adValue, interstitialAd.GetAdUnitID(), "interstitial");
    /// };
    ///
    /// rewardedAd.OnAdPaid += (AdValue adValue) =>
    /// {
    ///     ReflectAdMobIntegration.TrackAdMobRevenue(adValue, rewardedAd.GetAdUnitID(), "rewarded");
    /// };
    /// </code>
    /// </summary>
    public static class ReflectAdMobIntegration
    {
        /// <summary>
        /// Forward a single AdMob ad-paid impression to Reflect.
        /// Call this from each ad object's <c>OnAdPaid</c> handler.
        /// </summary>
        /// <param name="adValue">The <see cref="AdValue"/> supplied by AdMob's OnAdPaid callback.</param>
        /// <param name="adUnitId">The ad unit ID (e.g. "ca-app-pub-XXXX/YYYY").</param>
        /// <param name="adFormat">Ad format string — "banner", "interstitial", "rewarded", "rewarded_interstitial", "native", "app_open".</param>
        public static void TrackAdMobRevenue(AdValue adValue, string adUnitId, string adFormat)
        {
            if (adValue == null) return;

            // AdValue.Value is in micros (millionths of the currency unit).
            var revenueInUnits = adValue.Value / 1_000_000.0;

            var precision = MapPrecision(adValue.Precision);

            ReflectSDK.TrackAdRevenue(
                mediationPlatform: "admob",
                revenue:           revenueInUnits,
                currency:          adValue.CurrencyCode ?? "USD",
                adFormat:          adFormat,
                adNetwork:         "admob",
                adUnitId:          adUnitId,
                placement:         null,
                precision:         precision
            );
        }

        /// <summary>
        /// Map the AdMob <see cref="AdValue.PrecisionType"/> enum to a human-readable
        /// string consistent with what MAX and LevelPlay report.
        /// </summary>
        private static string MapPrecision(long precisionType)
        {
            // GoogleMobileAds.Api.AdValue.PrecisionType values:
            //   0 = Unknown, 1 = Estimated, 2 = PublisherProvided, 3 = Precise
            switch (precisionType)
            {
                case 1:  return "estimated";
                case 2:  return "publisher_provided";
                case 3:  return "precise";
                default: return "unknown";
            }
        }
    }
}

#endif
