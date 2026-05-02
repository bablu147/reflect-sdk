// ────────────────────────────────────────────────────────────────────────────
//  DeepLinkData — payload passed to ReflectSDK.OnDeepLink subscribers.
//
//  Sources:
//    Cold     — app launched directly from a URI (Intent.getData on Android,
//               application:openURL: on iOS).
//    Warm     — app already running, OS dispatched a new URI (Intent in
//               onNewIntent / scene continueUserActivity).
//    Deferred — captured from the install referrer (Android) or AdServices
//               attribution token (iOS) on the very first launch — the user
//               clicked a link, the app wasn't installed, they were redirected
//               to the store, and now we're delivering the original intent.
// ────────────────────────────────────────────────────────────────────────────

namespace Reflect
{
    public enum DeepLinkSource
    {
        Cold,
        Warm,
        Deferred,
    }

    /// <summary>Data passed to <see cref="ReflectSDK.OnDeepLink"/> handlers.</summary>
    public sealed class DeepLinkData
    {
        /// <summary>Full URL the user landed on. Always present.</summary>
        public string Url;

        /// <summary>Just the path component, e.g. <c>/promo/spring2026</c>. Convenience helper.</summary>
        public string Path;

        /// <summary>How we received this link.</summary>
        public DeepLinkSource Source;

        /// <summary>Tracking partner slug if the link came through a tracking URL. May be null.</summary>
        public string PartnerSlug;
    }
}
