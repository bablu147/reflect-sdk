// ────────────────────────────────────────────────────────────────────────────
//  ReflectStandardEvents — vocabulary of pre-defined event names that match
//  what AppsFlyer / Adjust / Firebase Analytics use, so reports look familiar
//  and server-side taxonomy rows can be pre-seeded with the right
//  attribution-required / fires-postback / dedupe-key defaults.
//
//  Two flavors:
//    - constants for the event names (use directly with TrackEvent)
//    - typed helpers that fill in the conventional property keys for you
//
//  All helpers are zero-allocation when you pass null props (most don't need
//  extras).
// ────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;

namespace Reflect
{
    /// <summary>
    /// Pre-defined event vocabulary matching standard MMP taxonomy. Use the
    /// constants when you want to send raw, or the helpers for the common
    /// shape with the right property keys baked in.
    /// </summary>
    public static class ReflectStandardEvents
    {
        // ── Lifecycle ──────────────────────────────────────────────────────
        public const string AppInstall      = "app_install";
        public const string AppOpen         = "app_open";
        public const string AppFirstOpen    = "app_first_open";   // distinct from app_open — fires once
        public const string SessionStart    = "session_start";
        public const string SessionEnd      = "session_end";

        // ── Acquisition / activation ───────────────────────────────────────
        public const string SignUp          = "sign_up";
        public const string Login           = "login";
        public const string TutorialBegin   = "tutorial_begin";
        public const string TutorialComplete= "tutorial_complete";

        // ── Engagement ─────────────────────────────────────────────────────
        public const string LevelStart      = "level_start";
        public const string LevelUp         = "level_up";
        public const string LevelComplete   = "level_complete";
        public const string AchievementUnlocked = "achievement_unlocked";
        public const string ContentView     = "view_item";
        public const string Search          = "search";
        public const string Share           = "share";
        public const string RateApp         = "rate";

        // ── Commerce ───────────────────────────────────────────────────────
        public const string AddToCart       = "add_to_cart";
        public const string BeginCheckout   = "begin_checkout";
        public const string Purchase        = "purchase";
        public const string Subscribe       = "subscribe";
        public const string StartTrial      = "start_trial";
        public const string TrialConverted  = "trial_converted";
        public const string SubscriptionRenewed   = "subscription_renewed";
        public const string SubscriptionCancelled = "subscription_cancelled";
        public const string SubscriptionRefunded  = "subscription_refunded";

        // ── Ad monetization ────────────────────────────────────────────────
        public const string AdImpression    = "ad_impression";
        public const string AdClick         = "ad_click";

        // ── Internal / reserved (underscore-prefixed) ──────────────────────
        public const string UserAlias       = "_user_alias";
        public const string Crash           = "_crash";

        // ─────────────────────────────────────────────────────────────────────
        //  Typed helpers — use these to ensure consistent property keys
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Fires <c>sign_up</c> with <c>method</c> = e.g. "email", "google", "apple".</summary>
        public static void SignUpWith(string method, IDictionary<string, object> extra = null)
            => Send(SignUp, "method", method, extra);

        /// <summary>Fires <c>login</c> with <c>method</c>.</summary>
        public static void LoginWith(string method, IDictionary<string, object> extra = null)
            => Send(Login, "method", method, extra);

        /// <summary>Fires <c>tutorial_complete</c> with the step name/id.</summary>
        public static void TutorialCompleted(string step, IDictionary<string, object> extra = null)
            => Send(TutorialComplete, "step", step, extra);

        /// <summary>Fires <c>level_up</c> / <c>level_complete</c>.</summary>
        public static void LevelAchieved(int level, IDictionary<string, object> extra = null)
        {
            var p = MergeExtra(extra);
            p["level"] = level;
            ReflectSDK.TrackEvent(LevelUp, p);
        }
        public static void LevelStarted(int level, IDictionary<string, object> extra = null)
        {
            var p = MergeExtra(extra);
            p["level"] = level;
            ReflectSDK.TrackEvent(LevelStart, p);
        }
        public static void LevelCompleted(int level, double? score = null, IDictionary<string, object> extra = null)
        {
            var p = MergeExtra(extra);
            p["level"] = level;
            if (score.HasValue) p["score"] = score.Value;
            ReflectSDK.TrackEvent(LevelComplete, p);
        }

        /// <summary>Fires <c>achievement_unlocked</c>.</summary>
        public static void Achievement(string achievementId, IDictionary<string, object> extra = null)
            => Send(AchievementUnlocked, "achievement_id", achievementId, extra);

        /// <summary>Fires <c>view_item</c>.</summary>
        public static void ViewItem(string contentType, string itemId, IDictionary<string, object> extra = null)
        {
            var p = MergeExtra(extra);
            p["content_type"] = contentType;
            p["item_id"]      = itemId;
            ReflectSDK.TrackEvent(ContentView, p);
        }

        /// <summary>Fires <c>search</c>.</summary>
        public static void SearchPerformed(string query, IDictionary<string, object> extra = null)
            => Send(Search, "query", query, extra);

        /// <summary>Fires <c>share</c>.</summary>
        public static void Shared(string contentType, string itemId, IDictionary<string, object> extra = null)
        {
            var p = MergeExtra(extra);
            p["content_type"] = contentType;
            p["item_id"]      = itemId;
            ReflectSDK.TrackEvent(Share, p);
        }

        /// <summary>Fires <c>rate</c> with a 1–5 score.</summary>
        public static void Rated(int rating, IDictionary<string, object> extra = null)
        {
            var p = MergeExtra(extra);
            p["rating"] = rating;
            ReflectSDK.TrackEvent(RateApp, p);
        }

        /// <summary>Fires <c>add_to_cart</c>.</summary>
        public static void AddedToCart(string sku, double price, string currencyCode, int quantity = 1, IDictionary<string, object> extra = null)
        {
            var p = MergeExtra(extra);
            p["product_id"]    = sku;
            p["price_local"]   = price;
            p["currency_code"] = currencyCode;
            p["quantity"]      = quantity;
            ReflectSDK.TrackEvent(AddToCart, p);
        }

        /// <summary>Fires <c>begin_checkout</c>.</summary>
        public static void CheckoutBegan(double cartValue, string currencyCode, int itemCount, IDictionary<string, object> extra = null)
        {
            var p = MergeExtra(extra);
            p["price_local"]   = cartValue;
            p["currency_code"] = currencyCode;
            p["item_count"]    = itemCount;
            ReflectSDK.TrackEvent(BeginCheckout, p);
        }

        /// <summary>Fires <c>start_trial</c>. Use TrackSubscription for the actual paid conversion.</summary>
        public static void TrialStarted(string productId, IDictionary<string, object> extra = null)
            => Send(StartTrial, "product_id", productId, extra);

        /// <summary>Fires <c>trial_converted</c> once the user moves from trial to paid.</summary>
        public static void TrialConvertedTo(string productId, double price, string currencyCode, string transactionId, IDictionary<string, object> extra = null)
        {
            var p = MergeExtra(extra);
            p["product_id"]     = productId;
            p["price_local"]    = price;
            p["currency_code"]  = currencyCode;
            p["transaction_id"] = transactionId;
            ReflectSDK.TrackEvent(TrialConverted, p);
        }

        /// <summary>Fires <c>subscription_renewed</c>.</summary>
        public static void SubscriptionDidRenew(string productId, double price, string currencyCode, string transactionId, IDictionary<string, object> extra = null)
        {
            var p = MergeExtra(extra);
            p["product_id"]     = productId;
            p["price_local"]    = price;
            p["currency_code"]  = currencyCode;
            p["transaction_id"] = transactionId;
            ReflectSDK.TrackEvent(SubscriptionRenewed, p);
        }

        /// <summary>Fires <c>subscription_cancelled</c>.</summary>
        public static void SubscriptionDidCancel(string productId, string reason = null, IDictionary<string, object> extra = null)
        {
            var p = MergeExtra(extra);
            p["product_id"] = productId;
            if (!string.IsNullOrEmpty(reason)) p["reason"] = reason;
            ReflectSDK.TrackEvent(SubscriptionCancelled, p);
        }

        /// <summary>Fires <c>subscription_refunded</c>.</summary>
        public static void SubscriptionDidRefund(string productId, double refundAmount, string currencyCode, string transactionId, IDictionary<string, object> extra = null)
        {
            var p = MergeExtra(extra);
            p["product_id"]     = productId;
            p["price_local"]    = refundAmount;
            p["currency_code"]  = currencyCode;
            p["transaction_id"] = transactionId;
            ReflectSDK.TrackEvent(SubscriptionRefunded, p);
        }

        /// <summary>Fires <c>ad_impression</c> for ad-network / mediation tracking.</summary>
        public static void AdShown(string adNetwork, string adFormat, double? revenue = null, string currencyCode = null, IDictionary<string, object> extra = null)
        {
            var p = MergeExtra(extra);
            p["ad_network"] = adNetwork;
            p["ad_format"]  = adFormat;
            if (revenue.HasValue)        p["revenue"]       = revenue.Value;
            if (!string.IsNullOrEmpty(currencyCode)) p["currency_code"] = currencyCode;
            ReflectSDK.TrackEvent(AdImpression, p);
        }

        /// <summary>Fires <c>ad_click</c>.</summary>
        public static void AdClicked(string adNetwork, string adFormat, IDictionary<string, object> extra = null)
        {
            var p = MergeExtra(extra);
            p["ad_network"] = adNetwork;
            p["ad_format"]  = adFormat;
            ReflectSDK.TrackEvent(AdClick, p);
        }

        // ─── Internal helpers ──────────────────────────────────────────────

        private static void Send(string evt, string keyField, string keyValue, IDictionary<string, object> extra)
        {
            var p = MergeExtra(extra);
            p[keyField] = keyValue;
            ReflectSDK.TrackEvent(evt, p);
        }

        private static Dictionary<string, object> MergeExtra(IDictionary<string, object> extra)
        {
            return extra != null ? new Dictionary<string, object>(extra) : new Dictionary<string, object>();
        }
    }
}
