using System.Collections.Generic;
using UnityEngine;

namespace Reflect.Samples
{
    /// <summary>
    /// Drop this on any GameObject in your first scene. Replace the BaseUrl below
    /// with your Cloudflare Worker endpoint, then press Play in the Editor — you
    /// should see "Enqueued 'app_install'" in the console.
    /// </summary>
    public sealed class ReflectBootstrap : MonoBehaviour
    {
        [SerializeField] private string baseUrl      = "https://reflect-worker.yourdomain.workers.dev";
        [SerializeField] private string appKey       = "app_live_example";
        [SerializeField] private string signingSecret = "";
        [SerializeField] private bool   enableLogging = true;
        [SerializeField] private bool   autoSessionTracking = true;
        [SerializeField] private bool   requestIosTracking = true;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);

            ReflectSDK.Initialize(new ReflectConfig
            {
                BaseUrl             = baseUrl,
                AppKey              = appKey,
                SigningSecret       = signingSecret,
                EnableLogging       = enableLogging,
                AutoSessionTracking = autoSessionTracking,
                AutoRequestIosTracking = requestIosTracking
            });
        }

        // ── Example hooks you would call from your game code ─────────

        public void OnUserSignedUp(string userId)
        {
            ReflectSDK.SetUserId(userId);
            ReflectSDK.TrackEvent("sign_up", new Dictionary<string, object>
            {
                { "method", "google" }
            });
        }

        public void OnUserPurchased(string productId, double priceInLocal, string currency, string txId)
        {
            ReflectSDK.TrackPurchase(productId, priceInLocal, currency, txId);
        }

        public void OnLevelComplete(int levelId, int stars, float durationSeconds)
        {
            ReflectSDK.TrackEvent("level_complete", new Dictionary<string, object>
            {
                { "level_id", levelId },
                { "stars",    stars },
                { "duration_s", durationSeconds }
            });
        }
    }
}
