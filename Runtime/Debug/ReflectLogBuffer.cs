using System;
using System.Collections.Generic;
using System.Text;

namespace Reflect.Internal.Debug
{
    /// <summary>
    /// In-memory ring buffer that captures every SDK log line so the debug overlay
    /// can render a scrollable, color-coded log view. Thread-safe for callers from
    /// UnitySendMessage / background coroutines.
    /// </summary>
    internal static class ReflectLogBuffer
    {
        public enum Level { Info, Warn, Error }

        public struct Entry
        {
            public DateTime TimeUtc;
            public Level    Level;
            public string   Message;
        }

        private const int Capacity = 500;
        private static readonly object _lock = new object();
        private static readonly Entry[] _ring = new Entry[Capacity];
        private static int _head;   // next write slot
        private static int _count;

        /// <summary>Current number of buffered entries (≤ Capacity).</summary>
        public static int Count { get { lock (_lock) return _count; } }

        /// <summary>Drop every entry — called when the overlay user taps "Clear".</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_ring, 0, _ring.Length);
                _head = 0;
                _count = 0;
            }
        }

        public static void Append(Level level, string message)
        {
            var e = new Entry
            {
                TimeUtc = DateTime.UtcNow,
                Level   = level,
                Message = message ?? string.Empty
            };
            lock (_lock)
            {
                _ring[_head] = e;
                _head = (_head + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }

        /// <summary>
        /// Snapshot the current entries in chronological order (oldest → newest).
        /// Caller owns the returned list; safe to iterate outside the lock.
        /// </summary>
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

        /// <summary>Concatenates the buffer for export (long-press "Copy" in overlay).</summary>
        public static string Dump()
        {
            var snap = Snapshot();
            var sb = new StringBuilder(snap.Count * 80);
            for (int i = 0; i < snap.Count; i++)
            {
                var e = snap[i];
                sb.Append(e.TimeUtc.ToString("HH:mm:ss.fff"));
                sb.Append(' ');
                sb.Append(LevelTag(e.Level));
                sb.Append(' ');
                sb.Append(e.Message);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string LevelTag(Level l)
        {
            switch (l)
            {
                case Level.Warn:  return "W";
                case Level.Error: return "E";
                default:          return "I";
            }
        }
    }
}
