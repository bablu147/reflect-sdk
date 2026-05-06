// ────────────────────────────────────────────────────────────────────────────
//  Reflect SDK — ironSource / LevelPlay ad-revenue integration.
//  Automatically forwards LevelPlay impression-level revenue data to Reflect
//  as _ad_impression events. Enable with ReflectLevelPlayIntegration.Enable()
//  after both Reflect and ironSource have been initialized.
//
//  Compile guard: define REFLECT_LEVELPLAY in Player Settings → Scripting
//  Define Symbols when the ironSource / LevelPlay SDK is present.
// ────────────────────────────────────────────────────────────────────────────

#if REFLECT_LEVELPLAY

using System;

namespace Reflect.Integrations
{
    /// <summary>
    /// Bridges ironSource / LevelPlay impression-level revenue callbacks into
    /// Reflect <c>_ad_impression</c> events via <see cref="ReflectSDK.TrackAdRevenue"/>.
    ///
    /// Usage:
    /// <code>
    /// ReflectSDK.Initialize(config);
    /// IronSource.Agent.init("YOUR_APP_KEY");
    /// ReflectLevelPlayIntegration.Enable();
    /// </code>
    /// </summary>
    public static class ReflectLevelPlayIntegration
    {
        private static bool _enabled;

        /// <summary>
        /// Subscribe to the LevelPlay impression data callback.
        /// Safe to call multiple times — subsequent calls are no-ops.
        /// </summary>
        public static void Enable()
        {
            if (_enabled) return;
            _enabled = true;

            IronSourceEvents.onImpressionDataReadyEvent += OnImpressionDataReady;
        }

        /// <summary>
        /// Unsubscribe from the LevelPlay impression data callback.
        /// </summary>
        public static void Disable()
        {
            if (!_enabled) return;
            _enabled = false;

            IronSourceEvents.onImpressionDataReadyEvent -= OnImpressionDataReady;
        }

        // ── Callback ─────────────────────────────────────────────────────

        private static void OnImpressionDataReady(IronSourceImpressionData impressionData)
        {
            if (impressionData == null) return;

            // ironSource reports revenue as a nullable double in USD.
            var revenue = impressionData.revenue ?? 0.0;

            ReflectSDK.TrackAdRevenue(
                mediationPlatform: "ironsource_levelplay",
                revenue:           revenue,
                currency:          "USD",                                    // LevelPlay always reports in USD
                adFormat:          impressionData.adUnit,
                adNetwork:         impressionData.adNetwork,
                adUnitId:          impressionData.instanceId,
                placement:         impressionData.placement,
                precision:         impressionData.precision ?? "estimated"
            );
        }
    }
}

#endif
