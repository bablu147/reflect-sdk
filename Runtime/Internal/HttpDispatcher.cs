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
        private float _nextAllowedAt;
        private int _backoffStep;
        private static readonly float[] BackoffSeconds = { 1f, 4f, 15f, 60f, 300f, 900f };
        private static readonly System.Random _jitterRng = new System.Random();

        public HttpDispatcher(ReflectConfig config, EventQueue queue)
        {
            _config = config;
            _queue = queue;
        }

        public void RequestFlushSoon()
        {
            // Coalesced — actual flush happens on next tick if conditions met.
        }

        public void Flush()
        {
            // Debug mode: no BaseUrl → never dispatch. Events stay in the queue
            // (capped at MaxQueueSize) and remain visible in the overlay.
            if (_config.IsDebugMode) return;
            if (_sending) return;
            if (Time.realtimeSinceStartup < _nextAllowedAt) return;
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
            var body = BuildBody(batch);
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
                    ReflectDebugEventLog.MarkBatchStatus(ExtractEventIds(batch),
                        ReflectDebugEventLog.Status.Sent, $"HTTP {code}");
                    ReflectNetworkLog.Complete(netEntry, ReflectNetworkLog.Status.Ok,
                        code, respBody, null, durationMs);
                    _backoffStep = 0;
                    _nextAllowedAt = 0f;
                }
                else if (code >= 400 && code < 500 && code != 408 && code != 429)
                {
                    // 4xx (except 408/429) = bad request — drop batch rather than retry forever.
                    ReflectLogger.Warn($"Dropping {batch.Count} events after {code}: {req.error}");
                    ReflectDebugEventLog.MarkBatchStatus(ExtractEventIds(batch),
                        ReflectDebugEventLog.Status.Dropped, $"HTTP {code}: {req.error}");
                    ReflectNetworkLog.Complete(netEntry, ReflectNetworkLog.Status.ClientError,
                        code, respBody, req.error, durationMs);
                    _backoffStep = 0;
                }
                else
                {
                    // Transient — requeue + back off with jitter to avoid thundering herd.
                    _queue.Requeue(batch);
                    _backoffStep = Math.Min(_backoffStep + 1, BackoffSeconds.Length - 1);
                    float baseDelay = BackoffSeconds[_backoffStep];
                    float jitter;
                    lock (_jitterRng) { jitter = (float)(0.5 + _jitterRng.NextDouble() * 0.5); }
                    float delay = baseDelay * jitter;
                    _nextAllowedAt = Time.realtimeSinceStartup + delay;
                    ReflectLogger.Warn($"Send failed ({code} / {req.error}) — retry in {delay:F1}s");
                    ReflectDebugEventLog.MarkBatchStatus(ExtractEventIds(batch),
                        ReflectDebugEventLog.Status.Failed,
                        $"HTTP {code}: {req.error} — retrying in {delay:F1}s");

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

        private static string BuildBody(List<string> batch)
        {
            var sb = new StringBuilder(batch.Count * 300 + 64);
            sb.Append("{\"events\":[");
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(batch[i]);
            }
            sb.Append("],\"sent_at_ms\":");
            sb.Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            sb.Append('}');
            return sb.ToString();
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

        /// <summary>Gzip-compress a byte array. Used for batch bodies ≥10 events.</summary>
        private static byte[] Gzip(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                {
                    gz.Write(data, 0, data.Length);
                }
                return ms.ToArray();
            }
        }
    }
}
