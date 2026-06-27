using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Reflect.Internal
{
    /// <summary>
    /// Thread-safe FIFO event queue with durable on-disk persistence.
    ///
    /// Durability model (Adjust-parity at-least-once delivery):
    ///   • A batch is <b>moved</b> into an in-flight holding slot on <see cref="DrainBatch"/>
    ///     (not discarded). It stays there until the send is confirmed.
    ///   • <see cref="AckInFlight"/> drops it (2xx success or permanent 4xx).
    ///   • <see cref="NackInFlight"/> returns it to the HEAD of the queue (transient failure).
    ///   • <see cref="PersistToDisk"/> writes the in-flight slot AND the pending queue, so a
    ///     crash/kill mid-send never loses the batch — it is restored on next launch.
    ///   • <see cref="LoadFromDisk"/> does NOT delete the file; it stays as durable truth and
    ///     is overwritten by the next persist. An early crash after load therefore loses nothing.
    /// On overflow we drop the NEWEST event (and count it), preserving the oldest
    /// attribution-critical events (install / first-session / earliest purchases).
    /// </summary>
    internal sealed class EventQueue
    {
        private readonly object _lock = new object();
        private readonly Queue<string> _items = new Queue<string>();
        private List<string> _inFlight = new List<string>();   // drained, awaiting ack/nack
        private readonly int _maxSize;
        private readonly string _filePath;
        private readonly string _tmpPath;
        private bool _loaded;
        private long _droppedCount;   // telemetry: events dropped due to overflow

        /// <summary>Pending + in-flight events currently held by the queue.</summary>
        public int Count { get { lock (_lock) return _items.Count + _inFlight.Count; } }

        /// <summary>Total events dropped due to overflow since process start (telemetry).</summary>
        public long DroppedCount { get { lock (_lock) return _droppedCount; } }

        public EventQueue(int maxSize)
        {
            _maxSize = maxSize;
            _filePath = Path.Combine(Application.persistentDataPath, "reflect_queue.jsonl");
            _tmpPath  = _filePath + ".tmp";
            LoadFromDisk();
        }

        public void Enqueue(ReflectEvent evt) => EnqueueRaw(evt.ToJson());

        /// <summary>
        /// Enqueue an already-serialized event JSON. On overflow the NEWEST event is
        /// dropped (this one) so the oldest attribution-critical events survive. The
        /// drop is counted and logged (never silent).
        /// </summary>
        public void EnqueueRaw(string json)
        {
            lock (_lock)
            {
                if (_items.Count + _inFlight.Count >= _maxSize)
                {
                    _droppedCount++;
                    // Log on the first drop and then sparsely so a sustained backlog
                    // can't flood the console, but the loss is always visible.
                    if (_droppedCount == 1 || (_droppedCount & 0x3F) == 0)
                        ReflectLogger.Warn($"Event queue full ({_maxSize}) — dropped newest event " +
                                           $"(total dropped this run={_droppedCount}).");
                    return;
                }
                _items.Enqueue(json);
            }
        }

        /// <summary>
        /// Move up to <paramref name="max"/> events into the in-flight slot and return a
        /// copy for sending. If a previous in-flight batch was never acked/nacked (e.g.
        /// it was restored from disk after a crash mid-send), that batch is returned
        /// again so it is retried rather than skipped.
        /// </summary>
        public List<string> DrainBatch(int max)
        {
            lock (_lock)
            {
                if (_inFlight.Count > 0) return new List<string>(_inFlight);
                var batch = new List<string>(max);
                while (batch.Count < max && _items.Count > 0)
                    batch.Add(_items.Dequeue());
                _inFlight = batch;
                return new List<string>(batch);
            }
        }

        /// <summary>Confirmed delivered (2xx) or permanently dropped (4xx): discard the in-flight batch.</summary>
        public void AckInFlight()
        {
            lock (_lock) { _inFlight.Clear(); }
        }

        /// <summary>
        /// Transient failure: return the in-flight batch to the HEAD of the queue for
        /// retry. If the queue is over capacity afterwards, drop from the TAIL (newest)
        /// so the just-restored batch is never the thing evicted.
        /// </summary>
        public void NackInFlight()
        {
            lock (_lock)
            {
                if (_inFlight.Count == 0) return;
                var existing = _items.ToArray();
                _items.Clear();
                foreach (var s in _inFlight) _items.Enqueue(s);   // requeued batch first (head)
                _inFlight.Clear();
                int dropped = 0;
                foreach (var s in existing)
                {
                    if (_items.Count >= _maxSize) { dropped++; continue; }  // drop newest
                    _items.Enqueue(s);
                }
                if (dropped > 0)
                {
                    _droppedCount += dropped;
                    ReflectLogger.Warn($"Requeue overflow — dropped {dropped} newest event(s) to preserve " +
                                       $"the retried batch (total dropped this run={_droppedCount}).");
                }
            }
        }

        public void PersistToDisk()
        {
            try
            {
                lock (_lock)
                {
                    if (_items.Count == 0 && _inFlight.Count == 0)
                    {
                        if (File.Exists(_filePath)) File.Delete(_filePath);
                        return;
                    }
                    // Write to a temp file then rename to avoid corruption if the app is
                    // killed mid-write. In-flight (un-acked) events go first since they
                    // are logically at the head of the queue.
                    using (var w = new StreamWriter(_tmpPath, false))
                    {
                        foreach (var s in _inFlight) w.WriteLine(s);
                        foreach (var s in _items)    w.WriteLine(s);
                    }
                    if (File.Exists(_filePath)) File.Delete(_filePath);
                    File.Move(_tmpPath, _filePath);
                }
                ReflectLogger.Info($"Persisted {Count} events to disk.");
            }
            catch (System.Exception ex) { ReflectLogger.Warn($"PersistToDisk failed: {ex.Message}"); }
        }

        /// <summary>GDPR/CCPA wipe — drop in-memory queue + in-flight slot + on-disk persistence file.</summary>
        public void WipeAll()
        {
            lock (_lock) { _items.Clear(); _inFlight.Clear(); }
            try { if (File.Exists(_filePath)) File.Delete(_filePath); }
            catch (System.Exception ex) { ReflectLogger.Warn($"WipeAll file delete failed: {ex.Message}"); }
            try { if (File.Exists(_tmpPath)) File.Delete(_tmpPath); } catch { /* best effort */ }
        }

        public void LoadFromDisk()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                // Clean up any orphaned temp file from an interrupted atomic write so it
                // can't mask the real queue or accumulate on disk.
                if (File.Exists(_tmpPath)) { try { File.Delete(_tmpPath); } catch { /* best effort */ } }

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
                // IMPORTANT: do NOT delete the file here. It remains the durable record
                // until the next PersistToDisk() overwrites it (after a confirmed send or
                // on pause). Deleting on load would mean an early crash loses everything
                // restored from the previous run. Re-sent duplicates are de-duped server
                // side by event_id, so keeping the file is strictly safer.
                ReflectLogger.Info($"Restored {_items.Count} events from disk.");
            }
            catch (System.Exception ex) { ReflectLogger.Warn($"LoadFromDisk failed: {ex.Message}"); }
        }
    }
}
