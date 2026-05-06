using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Reflect.Internal
{
    /// <summary>
    /// Thread-safe FIFO event queue with on-disk persistence.
    /// Survives app kill: events are flushed to a line-delimited JSON file on pause.
    /// </summary>
    internal sealed class EventQueue
    {
        private readonly object _lock = new object();
        private readonly Queue<string> _items = new Queue<string>();
        private readonly int _maxSize;
        private readonly string _filePath;
        private bool _loaded;

        public int Count { get { lock (_lock) return _items.Count; } }

        public EventQueue(int maxSize)
        {
            _maxSize = maxSize;
            _filePath = Path.Combine(Application.persistentDataPath, "reflect_queue.jsonl");
            LoadFromDisk();
        }

        public void Enqueue(ReflectEvent evt) => EnqueueRaw(evt.ToJson());

        /// <summary>
        /// Enqueue an already-serialized event JSON. Used when the caller needs
        /// the string for other purposes (e.g. the debug event log) to avoid
        /// serializing twice.
        /// </summary>
        public void EnqueueRaw(string json)
        {
            lock (_lock)
            {
                if (_items.Count >= _maxSize) _items.Dequeue(); // drop oldest
                _items.Enqueue(json);
            }
        }

        /// <summary>Drain up to <paramref name="max"/> events; caller owns the returned list.</summary>
        public List<string> DrainBatch(int max)
        {
            var batch = new List<string>(max);
            lock (_lock)
            {
                while (batch.Count < max && _items.Count > 0)
                    batch.Add(_items.Dequeue());
            }
            return batch;
        }

        /// <summary>Put a batch back at the head (used on transient send failures).</summary>
        public void Requeue(List<string> batch)
        {
            if (batch == null || batch.Count == 0) return;
            lock (_lock)
            {
                // Rebuild queue with requeued items first
                var existing = _items.ToArray();
                _items.Clear();
                for (int i = 0; i < batch.Count; i++) _items.Enqueue(batch[i]);
                for (int i = 0; i < existing.Length; i++) _items.Enqueue(existing[i]);
                // Cap at max size if we exceeded by requeuing
                while (_items.Count > _maxSize) _items.Dequeue();
            }
        }

        public void PersistToDisk()
        {
            try
            {
                lock (_lock)
                {
                    if (_items.Count == 0)
                    {
                        if (File.Exists(_filePath)) File.Delete(_filePath);
                        return;
                    }
                    // Write to a temp file then rename to avoid corruption if
                    // the app is killed mid-write.
                    var tmpPath = _filePath + ".tmp";
                    using (var w = new StreamWriter(tmpPath, false))
                        foreach (var s in _items) w.WriteLine(s);
                    if (File.Exists(_filePath)) File.Delete(_filePath);
                    File.Move(tmpPath, _filePath);
                }
                ReflectLogger.Info($"Persisted {Count} events to disk.");
            }
            catch (System.Exception ex) { ReflectLogger.Warn($"PersistToDisk failed: {ex.Message}"); }
        }

        /// <summary>GDPR/CCPA wipe — drop in-memory queue + on-disk persistence file.</summary>
        public void WipeAll()
        {
            lock (_lock) { _items.Clear(); }
            try { if (File.Exists(_filePath)) File.Delete(_filePath); }
            catch (System.Exception ex) { ReflectLogger.Warn($"WipeAll file delete failed: {ex.Message}"); }
        }

        public void LoadFromDisk()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(_filePath)) return;
                var lines = File.ReadAllLines(_filePath);
                lock (_lock)
                {
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (_items.Count >= _maxSize) break;
                        _items.Enqueue(line);
                    }
                }
                ReflectLogger.Info($"Restored {lines.Length} events from disk.");
                File.Delete(_filePath);
            }
            catch (System.Exception ex) { ReflectLogger.Warn($"LoadFromDisk failed: {ex.Message}"); }
        }
    }
}
