using System;
using System.Collections.Generic;

namespace Reflect.Internal.Debug
{
    /// <summary>
    /// Records a summary of every enqueued event (plus dispatch outcome when known)
    /// so the debug overlay's "Events" tab can show a scrolling, expandable feed.
    /// Ring-bounded at ~100 entries to cap memory in long debug sessions.
    /// </summary>
    internal static class ReflectDebugEventLog
    {
        public enum Status { Enqueued, Sent, Failed, Dropped, DebugOnly }

        public sealed class Entry
        {
            public DateTime TimeUtc;
            public string   EventId;
            public string   EventName;
            public Status   Status;
            public string   Json;       // full serialized event (may be large)
            public string   Note;       // e.g. "no BaseUrl — not dispatched"
        }

        private const int Capacity = 100;
        private static readonly object _lock = new object();
        private static readonly Entry[] _ring = new Entry[Capacity];
        private static int _head;
        private static int _count;

        // Session-wide counters (not ring-bounded).
        private static long _totalEnqueued;
        private static long _totalSent;
        private static long _totalFailed;
        private static long _totalDropped;

        public static long TotalEnqueued { get { lock (_lock) return _totalEnqueued; } }
        public static long TotalSent     { get { lock (_lock) return _totalSent; } }
        public static long TotalFailed   { get { lock (_lock) return _totalFailed; } }
        public static long TotalDropped  { get { lock (_lock) return _totalDropped; } }

        public static int Count { get { lock (_lock) return _count; } }

        public static void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_ring, 0, _ring.Length);
                _head = 0;
                _count = 0;
                _totalEnqueued = _totalSent = _totalFailed = _totalDropped = 0;
            }
        }

        public static void RecordEnqueued(string eventId, string eventName, string json, string note = null)
        {
            var e = new Entry
            {
                TimeUtc   = DateTime.UtcNow,
                EventId   = eventId,
                EventName = eventName,
                Status    = string.IsNullOrEmpty(note) ? Status.Enqueued : Status.DebugOnly,
                Json      = json,
                Note      = note
            };
            lock (_lock)
            {
                _ring[_head] = e;
                _head = (_head + 1) % Capacity;
                if (_count < Capacity) _count++;
                _totalEnqueued++;
            }
        }

        /// <summary>
        /// Updates the most recent batch of events with a dispatch status. Called by
        /// HttpDispatcher after a successful or failed POST. Matches by event_id set.
        /// </summary>
        public static void MarkBatchStatus(IEnumerable<string> eventIds, Status status, string note = null)
        {
            if (eventIds == null) return;
            var ids = new HashSet<string>(eventIds);
            lock (_lock)
            {
                int start = (_head - _count + Capacity) % Capacity;
                for (int i = 0; i < _count; i++)
                {
                    var idx = (start + i) % Capacity;
                    var e = _ring[idx];
                    if (e != null && ids.Contains(e.EventId))
                    {
                        e.Status = status;
                        if (!string.IsNullOrEmpty(note)) e.Note = note;
                    }
                }
                if      (status == Status.Sent)    _totalSent    += ids.Count;
                else if (status == Status.Failed)  _totalFailed  += ids.Count;
                else if (status == Status.Dropped) _totalDropped += ids.Count;
            }
        }

        public static List<Entry> Snapshot()
        {
            lock (_lock)
            {
                var list = new List<Entry>(_count);
                int start = (_head - _count + Capacity) % Capacity;
                for (int i = 0; i < _count; i++)
                    list.Add(_ring[(start + i) % Capacity]);
                return list;
            }
        }
    }
}
