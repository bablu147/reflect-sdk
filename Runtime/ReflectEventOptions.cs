using System.Collections.Generic;

namespace Reflect
{
    /// <summary>
    /// Rich per-event options for <see cref="ReflectSDK.TrackEvent(string, ReflectEventOptions)"/>.
    /// Lets a single event carry, in addition to analytics <see cref="Properties"/>:
    ///   • per-event <see cref="PartnerParams"/> (forwarded to ad-network partners,
    ///     merged over the global partner params),
    ///   • <see cref="CallbackParams"/> (forwarded on server-to-server callback URLs),
    ///   • a <see cref="DeduplicationId"/> (suppressed client-side within a bounded
    ///     window so double-fires / retries don't inflate counts), and
    ///   • a <see cref="CallbackId"/> for correlating server callbacks.
    /// Adjust parity: per-event partner/callback parameters + deduplication id + callback id.
    /// </summary>
    public sealed class ReflectEventOptions
    {
        /// <summary>Analytics properties (same as the dictionary passed to the basic TrackEvent).</summary>
        public IDictionary<string, object> Properties;

        /// <summary>Per-event partner parameters, merged over the global ones (per-event wins).</summary>
        public IDictionary<string, object> PartnerParams;

        /// <summary>Per-event callback parameters (server-to-server callback URLs).</summary>
        public IDictionary<string, object> CallbackParams;

        /// <summary>Optional dedup id — a repeat within the configured window is dropped.</summary>
        public string DeduplicationId;

        /// <summary>Optional callback correlation id (sent as <c>callback_id</c>).</summary>
        public string CallbackId;

        /// <summary>Optional revenue for revenue-bearing custom events (envelope field).</summary>
        public double? Revenue;

        /// <summary>Optional ISO-4217 currency paired with <see cref="Revenue"/>.</summary>
        public string Currency;
    }
}
