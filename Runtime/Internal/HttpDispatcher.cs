using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Reflect.Internal.Debug;
using UnityEngine;
using UnityEngine.Networking;

namespace Reflect.Internal
{
    /// <summary>
    /// HTTP batch dispatcher. POSTs events to <c>{BaseUrl}/event</c> with optional HMAC-SHA256.
    /// Retries with exponential backoff on transient failures (5xx, network errors).
    /// </summary>
    internal sealed class HttpDispatcher
    {
        private readonly ReflectConfig _config;
        private readonly EventQueue _queue;
        private bool _sending;
        private bool _offline;          // Adjust: setOfflineMode — queue but don't dispatch
        private bool _consentBlocked;   // consent denied → hold events on-device, never send
        // Backoff is tracked in WALL-CLOCK epoch-ms and persisted, so an outage-induced
        // backoff survives an app restart instead of resetting to "send immediately"
        // (which would thundering-herd a struggling server). realtimeSinceStartup, which
        // resets to 0 every launch, must NOT be used for anything that outlives a process.
        private long _nextAllowedAtMs;
        private int _backoffStep;
        private static readonly float[] BackoffSeconds = { 1f, 4f, 15f, 60f, 300f, 900f };
        private const long MaxRetryAfterMs = 3600_000; // clamp a server Retry-After to 1h
        private static readonly System.Random _jitterRng = new System.Random();

        private const string PrefsBackoffStep = "reflect_backoff_step";
        private const string PrefsBackoffNextMs = "reflect_backoff_next_ms";

        public HttpDispatcher(ReflectConfig config, EventQueue queue)
        {
            _config = config;
            _queue = queue;
            // Restore persisted backoff so a server outage we backed off from is still
            // respected after a relaunch.
            _backoffStep = PlayerPrefs.GetInt(PrefsBackoffStep, 0);
            if (PlayerPrefs.HasKey(PrefsBackoffNextMs))
                long.TryParse(PlayerPrefs.GetString(PrefsBackoffNextMs, "0"), out _nextAllowedAtMs);
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private void PersistBackoff()
        {
            PlayerPrefs.SetInt(PrefsBackoffStep, _backoffStep);
            PlayerPrefs.SetString(PrefsBackoffNextMs, _nextAllowedAtMs.ToString());
            PlayerPrefs.Save();
        }

        public void RequestFlushSoon()
        {
            // Coalesced — actual flush happens on next tick if conditions met.
        }

        /// <summary>Adjust parity: when offline, events are queued but not sent.</summary>
        public void SetOffline(bool offline) { _offline = offline; }

        /// <summary>When consent is denied, hold events in the persistent queue and never
        /// dispatch — data does not leave the device until consent is (re)granted.</summary>
        public void SetConsentBlocked(bool blocked) { _consentBlocked = blocked; }

        public void Flush()
        {
            // Debug mode: no BaseUrl → never dispatch. Events stay in the queue
            // (capped at MaxQueueSize) and remain visible in the overlay.
            if (_config.IsDebugMode) return;
            // Consent denied: hold everything on-device, do not transmit.
            if (_consentBlocked) return;
            // Offline mode: hold everything in the persistent queue until back online.
            if (_offline) return;
            if (_sending) return;
            if (NowMs() < _nextAllowedAtMs) return;
            if (_queue.Count == 0) return;
            var batch = _queue.DrainBatch(_config.BatchSize);
            if (batch.Count == 0) return;

            var runner = ReflectCallbackReceiver.Ensure();
            runner.StartCoroutine(SendBatchCo(batch));
        }

        // Batches at or above this size get gzip-compressed before sending.
        // Below this it's not worth the CPU + a few extra header bytes.
        // Cuts typical batch bandwidth ~80% (2KB/event raw → ~400B compressed)
        // — direct save on Cloudflare Workers ingress + R2 audit storage.
        private const int GZIP_MIN_EVENTS = 10;

        private IEnumerator SendBatchCo(List<string> batch)
        {
            _sending = true;
            var body = BuildBody(batch, _backoffStep, _queue.Count, _queue.DroppedCount);
            var rawBytes = Encoding.UTF8.GetBytes(body);

            // Decide whether to compress. We HMAC over the wire bytes so the
            // server reads-bytes → verifies → decompresses (matching the order
            // the Worker does in routes/event.ts).
            var compress = batch.Count >= GZIP_MIN_EVENTS;
            var bytes = compress ? Gzip(rawBytes) : rawBytes;

            var url = _config.BaseUrl + "/event";

            // Build the header list once, use the same for the request AND the
            // network log entry so the overlay mirrors exactly what went out.
            var headerList = new List<KeyValuePair<string, string>>(6)
            {
                new KeyValuePair<string, string>("Content-Type", "application/json"),
                new KeyValuePair<string, string>("X-Reflect-Sdk", SdkVersion.Value),
            };
            if (compress)
                headerList.Add(new KeyValuePair<string, string>("Content-Encoding", "gzip"));
            // SDK v2 — company key identifies the tenant this app belongs to.
            // The server rejects requests where app_key and company_key don't
            // match with 401 app_company_mismatch.
            if (!string.IsNullOrEmpty(_config.CompanyKey))
                headerList.Add(new KeyValuePair<string, string>("X-Reflect-Company-Key", _config.CompanyKey));
            if (!string.IsNullOrEmpty(_config.AppKey))
                headerList.Add(new KeyValuePair<string, string>("X-Reflect-App-Key", _config.AppKey));
            // Device-integrity attestation token (Play Integrity / App Attest), if the
            // host app supplied a provider. A cryptographic root of trust the static
            // HMAC secret can't give; the server verifies it.
            if (_config.IntegrityTokenProvider != null)
            {
                try
                {
                    var token = _config.IntegrityTokenProvider();
                    if (!string.IsNullOrEmpty(token))
                        headerList.Add(new KeyValuePair<string, string>("X-Reflect-Integrity-Token", token));
                }
                catch (Exception ex) { ReflectLogger.Warn($"IntegrityTokenProvider threw: {ex.Message}"); }
            }
            string signature = null;
            if (!string.IsNullOrEmpty(_config.SigningSecret))
            {
                signature = Hmac(bytes, _config.SigningSecret);
                headerList.Add(new KeyValuePair<string, string>("X-Reflect-Signature", signature));
                headerList.Add(new KeyValuePair<string, string>("X-Reflect-Signature-Version", "1"));
            }

            // Network log shows the original JSON (readable) but the on-wire
            // size we report is post-compression so the dev sees the actual
            // bytes sent — useful for debugging "is gzip working?" questions.
            var netEntry = ReflectNetworkLog.BeginRequest(url, "POST", batch.Count, bytes.Length, body, headerList);
            float started = Time.realtimeSinceStartup;

            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(bytes) { contentType = "application/json" };
                req.downloadHandler = new DownloadHandlerBuffer();
                foreach (var h in headerList) req.SetRequestHeader(h.Key, h.Value);
                req.timeout = 30;

                ReflectLogger.Info($"POST {url} (batch={batch.Count}, bytes={bytes.Length})");
                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                bool isError = req.result != UnityWebRequest.Result.Success;
#else
                bool isError = req.isNetworkError || req.isHttpError;
#endif
                long code = req.responseCode;
                string respBody = req.downloadHandler != null ? req.downloadHandler.text : null;
                float durationMs = (Time.realtimeSinceStartup - started) * 1000f;

                if (!isError && code >= 200 && code < 300)
                {
                    ReflectLogger.Info($"OK {code} — {batch.Count} events accepted ({durationMs:F0}ms).");
                    // Confirmed delivered — drop the in-flight batch and re-persist so the
                    // on-disk file reflects only un-acked events.
                    _queue.AckInFlight();
                    _queue.PersistToDisk();
                    ReflectDebugEventLog.MarkBatchStatus(ExtractEventIds(batch),
                        ReflectDebugEventLog.Status.Sent, $"HTTP {code}");
                    ReflectNetworkLog.Complete(netEntry, ReflectNetworkLog.Status.Ok,
                        code, respBody, null, durationMs);
                    _backoffStep = 0;
                    _nextAllowedAtMs = 0L;
                    PersistBackoff();
                }
                else if (code >= 400 && code < 500 && code != 408 && code != 429)
                {
                    // 4xx (except 408/429) = bad request — drop batch rather than retry forever.
                    ReflectLogger.Warn($"Dropping {batch.Count} events after {code}: {req.error}");
                    _queue.AckInFlight();   // permanent drop — remove from durable storage
                    _queue.PersistToDisk();
                    ReflectDebugEventLog.MarkBatchStatus(ExtractEventIds(batch),
                        ReflectDebugEventLog.Status.Dropped, $"HTTP {code}: {req.error}");
                    ReflectNetworkLog.Complete(netEntry, ReflectNetworkLog.Status.ClientError,
                        code, respBody, req.error, durationMs);
                    _backoffStep = 0;
                    _nextAllowedAtMs = 0L;
                    PersistBackoff();
                }
                else
                {
                    // Transient — return the batch to the head of the queue + back off with
                    // jitter to avoid a thundering herd. The batch stays in durable storage
                    // the whole time, so a kill before retry does not lose it.
                    _queue.NackInFlight();
                    _queue.PersistToDisk();
                    _backoffStep = Math.Min(_backoffStep + 1, BackoffSeconds.Length - 1);
                    // Honor a server-issued Retry-After (seconds) on 429/503 over our own
                    // schedule, so a struggling/rate-limiting backend can pace us.
                    long retryAfterMs = ParseRetryAfterMs(req.GetResponseHeader("Retry-After"));
                    long delayMs;
                    if (retryAfterMs > 0)
                    {
                        delayMs = retryAfterMs;
                    }
                    else
                    {
                        float baseDelay = BackoffSeconds[_backoffStep];
                        float jitter;
                        lock (_jitterRng) { jitter = (float)(0.5 + _jitterRng.NextDouble() * 0.5); }
                        delayMs = (long)(baseDelay * jitter * 1000f);
                    }
                    _nextAllowedAtMs = NowMs() + delayMs;
                    PersistBackoff();
                    ReflectLogger.Warn($"Send failed ({code} / {req.error}) — retry in {delayMs / 1000f:F1}s");
                    ReflectDebugEventLog.MarkBatchStatus(ExtractEventIds(batch),
                        ReflectDebugEventLog.Status.Failed,
                        $"HTTP {code}: {req.error} — retrying in {delayMs / 1000f:F1}s");

                    // code == 0 typically means no response at all (DNS / TLS / offline);
                    // anything ≥500 is a proper server error.
                    var netStatus = code >= 500
                        ? ReflectNetworkLog.Status.ServerError
                        : ReflectNetworkLog.Status.NetworkError;
                    ReflectNetworkLog.Complete(netEntry, netStatus,
                        code, respBody, req.error, durationMs);
                }
            }

            _sending = false;
        }

        /// <summary>
        /// Parse an HTTP <c>Retry-After</c> header (delta-seconds form only) into a
        /// millisecond delay, clamped to a sane maximum. Returns 0 if absent/unparseable
        /// or an HTTP-date form (we fall back to our own backoff in that case).
        /// </summary>
        private static long ParseRetryAfterMs(string header)
        {
            if (string.IsNullOrEmpty(header)) return 0;
            if (int.TryParse(header.Trim(), out var seconds) && seconds > 0)
                return Math.Min((long)seconds * 1000L, MaxRetryAfterMs);
            return 0;
        }

        private static string BuildBody(List<string> batch, int retryCount, int queueSize, long droppedCount)
        {
            var sb = new StringBuilder(batch.Count * 300 + 128);
            sb.Append("{\"events\":[");
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(batch[i]);
            }
            sb.Append("],\"sent_at_ms\":");
            sb.Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            // SDK self-telemetry (Adjust parity: retry_count / queue_size) — lets the
            // backend spot anomalies (a client stuck retrying, a backlog draining, silent
            // drops from overflow) and weight fraud/quality signals.
            sb.Append(",\"sdk_telemetry\":{\"retry_count\":");
            sb.Append(retryCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"queue_size\":");
            sb.Append(queueSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"dropped\":");
            sb.Append(droppedCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('}');
            // Deterministic batch idempotency key (content fingerprint of the event_ids).
            // It is STABLE across retries of the same batch, so the server can reject a
            // replayed/duplicated batch (anti-replay) — and, because it rides inside the
            // HMAC-signed body, an attacker can't forge it without the secret. event_ts_ms
            // (per event) already distinguishes "created" from this batch's sent_at_ms.
            sb.Append(",\"batch_id\":\"");
            sb.Append(BatchId(batch));
            sb.Append("\"}");
            return sb.ToString();
        }

        /// <summary>SHA-256 (128-bit, hex) over the sorted event_ids — a stable content
        /// fingerprint for the batch, identical across retries of the same events.</summary>
        private static string BatchId(List<string> batch)
        {
            var ids = ExtractEventIds(batch);
            ids.Sort(StringComparer.Ordinal);
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join(",", ids)));
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Pulls the <c>event_id</c> value out of each queued JSON string so the
        /// debug overlay can update the corresponding event entries. Avoids a
        /// full JSON parse — we only need the one leading field.
        /// </summary>
        private static List<string> ExtractEventIds(List<string> batch)
        {
            var ids = new List<string>(batch.Count);
            const string key = "\"event_id\":\"";
            for (int i = 0; i < batch.Count; i++)
            {
                var s = batch[i];
                var idx = s.IndexOf(key, StringComparison.Ordinal);
                if (idx < 0) continue;
                var start = idx + key.Length;
                var end = s.IndexOf('"', start);
                if (end > start) ids.Add(s.Substring(start, end - start));
            }
            return ids;
        }

        private static string Hmac(byte[] data, string secret)
        {
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            {
                var sig = h.ComputeHash(data);
                var sb = new StringBuilder(sig.Length * 2);
                for (int i = 0; i < sig.Length; i++) sb.Append(sig[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  GDPR / CCPA right-to-be-forgotten — POST /privacy/delete
        // ─────────────────────────────────────────────────────────────────────

        public IEnumerator SendPrivacyDelete(string installUuid, Action<bool> onComplete)
        {
            // Skip if no BaseUrl — the local wipe was sufficient.
            if (_config.IsDebugMode || string.IsNullOrEmpty(_config.BaseUrl))
            {
                onComplete?.Invoke(true);
                yield break;
            }

            var body = "{\"install_uuid\":\"" + installUuid + "\"}";
            var bytes = Encoding.UTF8.GetBytes(body);
            var url = _config.BaseUrl + "/privacy/delete";

            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler   = new UploadHandlerRaw(bytes) { contentType = "application/json" };
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type",   "application/json");
                req.SetRequestHeader("X-Reflect-Sdk",  SdkVersion.Value);
                if (!string.IsNullOrEmpty(_config.CompanyKey)) req.SetRequestHeader("X-Reflect-Company-Key", _config.CompanyKey);
                if (!string.IsNullOrEmpty(_config.AppKey))     req.SetRequestHeader("X-Reflect-App-Key",     _config.AppKey);
                if (!string.IsNullOrEmpty(_config.SigningSecret))
                    req.SetRequestHeader("X-Reflect-Signature", Hmac(bytes, _config.SigningSecret));
                req.timeout = 30;

                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !req.isNetworkError && !req.isHttpError;
#endif
                if (ok && req.responseCode >= 200 && req.responseCode < 300)
                {
                    ReflectLogger.Info("Privacy deletion request queued on server.");
                    onComplete?.Invoke(true);
                }
                else
                {
                    ReflectLogger.Warn($"Privacy deletion server call failed ({req.responseCode}): {req.error}");
                    onComplete?.Invoke(false);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Push token registration — POST /push-token
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// POST the push notification token to <c>{BaseUrl}/push-token</c>.
        /// Fire-and-forget — failures are logged but not retried (the token
        /// is also recorded as a <c>_push_token</c> event via the normal queue).
        /// </summary>
        public IEnumerator SendPushToken(string appKey, string installUuid,
                                          string platform, string token, string provider)
        {
            if (_config.IsDebugMode || string.IsNullOrEmpty(_config.BaseUrl))
                yield break;

            var body = "{" +
                "\"app_key\":\""      + EscapeJsonString(appKey)      + "\"," +
                "\"install_uuid\":\"" + EscapeJsonString(installUuid) + "\"," +
                "\"platform\":\""     + EscapeJsonString(platform)    + "\"," +
                "\"token\":\""        + EscapeJsonString(token)       + "\"," +
                "\"provider\":\""     + EscapeJsonString(provider)    + "\"" +
            "}";
            var bytes = Encoding.UTF8.GetBytes(body);
            var url = _config.BaseUrl + "/push-token";

            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler   = new UploadHandlerRaw(bytes) { contentType = "application/json" };
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type",   "application/json");
                req.SetRequestHeader("X-Reflect-Sdk",  SdkVersion.Value);
                if (!string.IsNullOrEmpty(_config.CompanyKey)) req.SetRequestHeader("X-Reflect-Company-Key", _config.CompanyKey);
                if (!string.IsNullOrEmpty(_config.AppKey))     req.SetRequestHeader("X-Reflect-App-Key",     _config.AppKey);
                if (!string.IsNullOrEmpty(_config.SigningSecret))
                    req.SetRequestHeader("X-Reflect-Signature", Hmac(bytes, _config.SigningSecret));
                req.timeout = 30;

                ReflectLogger.Info($"POST {url} (push-token registration)");
                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !req.isNetworkError && !req.isHttpError;
#endif
                if (ok && req.responseCode >= 200 && req.responseCode < 300)
                {
                    ReflectLogger.Info("Push token registered on server.");
                }
                else
                {
                    ReflectLogger.Warn($"Push token registration failed ({req.responseCode}): {req.error}");
                }
            }
        }

        /// <summary>
        /// Resolve a deferred deep link via <c>{BaseUrl}/deeplink/resolve</c>. The
        /// server matches this install (by fingerprint) to a recent click carrying a
        /// deep_link_path and returns it — covering iOS / fingerprint / referrer-less
        /// installs that the Play-referrer `dl` param can't. Invokes onPath with the
        /// resolved path (or null).
        /// </summary>
        public IEnumerator ResolveDeferredDeepLink(string appKey, string installUuid, Action<string> onPath)
        {
            if (_config.IsDebugMode || string.IsNullOrEmpty(_config.BaseUrl)) { onPath?.Invoke(null); yield break; }

            var body = "{" +
                "\"app_key\":\""      + EscapeJsonString(appKey)      + "\"," +
                "\"install_uuid\":\"" + EscapeJsonString(installUuid) + "\"" +
            "}";
            var bytes = Encoding.UTF8.GetBytes(body);
            var url = _config.BaseUrl + "/deeplink/resolve";

            string resolvedPath = null;
            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler   = new UploadHandlerRaw(bytes) { contentType = "application/json" };
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type",  "application/json");
                req.SetRequestHeader("X-Reflect-Sdk", SdkVersion.Value);
                if (!string.IsNullOrEmpty(_config.AppKey))        req.SetRequestHeader("X-Reflect-App-Key", _config.AppKey);
                if (!string.IsNullOrEmpty(_config.SigningSecret)) req.SetRequestHeader("X-Reflect-Signature", Hmac(bytes, _config.SigningSecret));
                req.timeout = 15;

                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !req.isNetworkError && !req.isHttpError;
#endif
                if (ok && req.responseCode >= 200 && req.responseCode < 300)
                {
                    var parsed = MiniJson.Deserialize(req.downloadHandler.text) as IDictionary<string, object>;
                    if (parsed != null && parsed.TryGetValue("deep_link_path", out var p))
                        resolvedPath = p as string;
                }
            }
            onPath?.Invoke(resolvedPath);
        }

        /// <summary>
        /// Resolve / unshorten a tracking or branded short link via
        /// <c>{BaseUrl}/deeplink/resolve</c>. POSTs the short URL and returns the
        /// expanded target (server field <c>resolved_url</c>, falling back to
        /// <c>deep_link_path</c>) — or null. Invokes <paramref name="onResolved"/>.
        /// </summary>
        public IEnumerator ResolveLink(string appKey, string installUuid, string shortUrl, Action<string> onResolved)
        {
            if (_config.IsDebugMode || string.IsNullOrEmpty(_config.BaseUrl)) { onResolved?.Invoke(shortUrl); yield break; }

            var body = "{" +
                "\"app_key\":\""      + EscapeJsonString(appKey)      + "\"," +
                "\"install_uuid\":\"" + EscapeJsonString(installUuid) + "\"," +
                "\"url\":\""          + EscapeJsonString(shortUrl)    + "\"" +
            "}";
            var bytes = Encoding.UTF8.GetBytes(body);
            var url = _config.BaseUrl + "/deeplink/resolve";

            string resolved = null;
            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler   = new UploadHandlerRaw(bytes) { contentType = "application/json" };
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type",  "application/json");
                req.SetRequestHeader("X-Reflect-Sdk", SdkVersion.Value);
                if (!string.IsNullOrEmpty(_config.AppKey))        req.SetRequestHeader("X-Reflect-App-Key", _config.AppKey);
                if (!string.IsNullOrEmpty(_config.SigningSecret)) req.SetRequestHeader("X-Reflect-Signature", Hmac(bytes, _config.SigningSecret));
                req.timeout = 15;

                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !req.isNetworkError && !req.isHttpError;
#endif
                if (ok && req.responseCode >= 200 && req.responseCode < 300)
                {
                    var parsed = MiniJson.Deserialize(req.downloadHandler.text) as IDictionary<string, object>;
                    if (parsed != null)
                    {
                        if (parsed.TryGetValue("resolved_url", out var ru) && ru is string rus) resolved = rus;
                        else if (parsed.TryGetValue("deep_link_path", out var p) && p is string ps) resolved = ps;
                    }
                }
            }
            onResolved?.Invoke(resolved);
        }

        /// <summary>
        /// Verify a purchase receipt server-side via <c>{BaseUrl}/purchase/verify</c>.
        /// The server validates against Apple/Google and returns
        /// <c>{status, code, message}</c>, mapped to a <see cref="ReflectVerificationResult"/>.
        /// </summary>
        public IEnumerator VerifyPurchase(string appKey, string installUuid, string productId,
            string transactionId, string purchaseToken, string receiptData,
            Action<ReflectVerificationResult> callback)
        {
            if (_config.IsDebugMode || string.IsNullOrEmpty(_config.BaseUrl))
            {
                callback?.Invoke(new ReflectVerificationResult(ReflectVerificationStatus.Unknown, 0, "debug_mode"));
                yield break;
            }

            var body = "{" +
                "\"app_key\":\""        + EscapeJsonString(appKey)        + "\"," +
                "\"install_uuid\":\""   + EscapeJsonString(installUuid)   + "\"," +
                "\"product_id\":\""     + EscapeJsonString(productId)     + "\"," +
                "\"transaction_id\":\"" + EscapeJsonString(transactionId) + "\"," +
                "\"purchase_token\":\"" + EscapeJsonString(purchaseToken) + "\"," +
                "\"receipt_data\":\""   + EscapeJsonString(receiptData)   + "\"" +
            "}";
            var bytes = Encoding.UTF8.GetBytes(body);
            var url = _config.BaseUrl + "/purchase/verify";

            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler   = new UploadHandlerRaw(bytes) { contentType = "application/json" };
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type",  "application/json");
                req.SetRequestHeader("X-Reflect-Sdk", SdkVersion.Value);
                if (!string.IsNullOrEmpty(_config.CompanyKey)) req.SetRequestHeader("X-Reflect-Company-Key", _config.CompanyKey);
                if (!string.IsNullOrEmpty(_config.AppKey))     req.SetRequestHeader("X-Reflect-App-Key",     _config.AppKey);
                if (!string.IsNullOrEmpty(_config.SigningSecret)) req.SetRequestHeader("X-Reflect-Signature", Hmac(bytes, _config.SigningSecret));
                req.timeout = 20;

                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !req.isNetworkError && !req.isHttpError;
#endif
                if (!ok || req.responseCode < 200 || req.responseCode >= 300)
                {
                    callback?.Invoke(new ReflectVerificationResult(
                        ReflectVerificationStatus.Failed, (int)req.responseCode, req.error ?? "request_failed"));
                    yield break;
                }

                var parsed = MiniJson.Deserialize(req.downloadHandler.text) as IDictionary<string, object>;
                var statusStr = parsed != null && parsed.TryGetValue("status", out var s) ? (s as string) : null;
                int code = 0;
                if (parsed != null && parsed.TryGetValue("code", out var c))
                    code = c is long cl ? (int)cl : (c is double cd ? (int)cd : 0);
                var message = parsed != null && parsed.TryGetValue("message", out var m) ? (m as string) : null;

                var status = ReflectVerificationStatus.Unknown;
                if (statusStr == "verified")      status = ReflectVerificationStatus.Verified;
                else if (statusStr == "not_verified") status = ReflectVerificationStatus.NotVerified;
                else if (statusStr == "failed")   status = ReflectVerificationStatus.Failed;

                callback?.Invoke(new ReflectVerificationResult(status, code, message));
            }
        }

        /// <summary>Minimal JSON string escaping for hand-built JSON payloads.</summary>
        private static string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        /// <summary>Gzip-compress a byte array. Used for batch bodies ≥10 events.</summary>
        private static byte[] Gzip(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                {
                    gz.Write(data, 0, data.Length);
                }
                return ms.ToArray();
            }
        }
    }
}
