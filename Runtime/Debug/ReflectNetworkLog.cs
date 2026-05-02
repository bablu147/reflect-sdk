using System;
using System.Collections.Generic;

namespace Reflect.Internal.Debug
{
    /// <summary>
    /// Ring buffer of HTTP request/response pairs produced by
    /// <see cref="HttpDispatcher"/>. Powers the overlay's <b>Network</b> tab so
    /// developers can see exactly what hits the Worker and what comes back.
    /// Bounded at 30 entries + per-body size caps so long debug sessions
    /// don't balloon memory.
    /// </summary>
    internal static class ReflectNetworkLog
    {
        /// <summary>Outcome of a single HTTP round-trip.</summary>
        public enum Status
        {
            /// <summary>Request in flight.</summary>
            Pending,
            /// <summary>2xx — events accepted.</summary>
            Ok,
            /// <summary>4xx — Worker rejected the payload; events dropped.</summary>
            ClientError,
            /// <summary>5xx — transient server fault; batch is requeued.</summary>
            ServerError,
            /// <summary>Transport failure — no HTTP code (DNS, timeout, TLS).</summary>
            NetworkError,
        }

        public sealed class Entry
        {
            public int      Seq;
            public DateTime StartedUtc;
            public float    DurationMs;
            public string   Url;
            public string   Method;
            public int      BatchSize;
            public int      RequestBytes;
            public string   RequestBodyPreview;   // truncated to 50 KB
            public List<KeyValuePair<string, string>> RequestHeaders;
            public Status   Status;
            public long     ResponseCode;
            public string   ResponseBodyPreview;  // truncated to 5 KB
            public string   ErrorDetail;          // transport-level error text, if any
        }

        private const int Capacity    = 30;
        private const int MaxReqBody  = 50 * 1024;
        private const int MaxRespBody =  5 * 1024;

        private static readonly object _lock = new object();
        private static readonly Entry[] _ring = new Entry[Capacity];
        private static int _head;
        private static int _count;
        private static int _nextSeq;

        public static int Count { get { lock (_lock) return _count; } }

        /// <summary>
        /// Called by <see cref="HttpDispatcher"/> the moment a POST leaves —
        /// the returned entry is updated in-place by <see cref="Complete"/>
        /// once the response arrives.
        /// </summary>
        public static Entry BeginRequest(string url, string method, int batchSize, int requestBytes,
                                          string requestBody,
                                          List<KeyValuePair<string, string>> headers)
        {
            var e = new Entry
            {
                StartedUtc         = DateTime.UtcNow,
                Url                = url,
                Method             = method,
                BatchSize          = batchSize,
                RequestBytes       = requestBytes,
                RequestBodyPreview = Truncate(requestBody, MaxReqBody),
                RequestHeaders     = headers,
                Status             = Status.Pending,
            };
            lock (_lock)
            {
                e.Seq = ++_nextSeq;
                _ring[_head] = e;
                _head = (_head + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
            return e;
        }

        public static void Complete(Entry e, Status status, long code,
                                     string responseBody, string errorDetail, float durationMs)
        {
            if (e == null) return;
            lock (_lock)
            {
                e.Status              = status;
                e.ResponseCode        = code;
                e.ResponseBodyPreview = Truncate(responseBody, MaxRespBody);
                e.ErrorDetail         = errorDetail;
                e.DurationMs          = durationMs;
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_ring, 0, _ring.Length);
                _head = _count = 0;
                _nextSeq = 0;
            }
        }

        public static List<Entry> Snapshot()
        {
            lock (_lock)
            {
                var list = new List<Entry>(_count);
                int start = (_head - _count + Capacity) % Capacity;
                for (int i = 0; i < _count; i++) list.Add(_ring[(start + i) % Capacity]);
                return list;
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "\n…[truncated " + (s.Length - max) + " chars]";
        }
    }
}
