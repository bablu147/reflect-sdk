using System;
using UnityEngine;

namespace Reflect.Internal
{
    /// <summary>
    /// Result of an app-foreground transition.
    /// </summary>
    internal struct ForegroundResult
    {
        /// <summary>True when a genuinely new session started (gap since last activity
        /// exceeded the session threshold, or this is the very first launch).</summary>
        public bool NewSession;
        /// <summary>True when a NEW session started AND a previous session existed that
        /// must now be closed (its <see cref="PriorSessionLengthMs"/> / id are valid).
        /// Lets the caller emit a deferred <c>session_end</c> — including the case where
        /// the prior session ended by an app kill that never delivered a pause.</summary>
        public bool ClosedPriorSession;
        public long   PriorSessionLengthMs;
        public string PriorSessionId;
        public int    PriorSessionCount;
        /// <summary>Gap since the previous activity (ms). 0 on first launch.</summary>
        public long LastIntervalMs;
    }

    /// <summary>
    /// Adjust-parity session bookkeeping, persisted across launches.
    ///
    /// A new session begins only when the gap since the last activity exceeds the
    /// configurable threshold (default 30 min). Briefer background bounces (a
    /// notification peek, app switch, biometric prompt) are <i>subsessions</i> of the
    /// same session, so session counts are not inflated. Tracks
    /// <c>session_count</c>, <c>subsession_count</c>, cumulative <c>session_length</c>,
    /// <c>last_interval</c>, and a per-session <c>session_id</c>. State is flushed to
    /// PlayerPrefs on every transition + heartbeat, so a kill loses at most one
    /// heartbeat interval of foreground time and the prior session is finalised on the
    /// next launch.
    /// </summary>
    internal sealed class ReflectSession
    {
        private const string KCount      = "reflect_session_count";
        private const string KSub        = "reflect_subsession_count";
        private const string KLengthMs   = "reflect_session_length_ms";
        private const string KLastAct    = "reflect_last_activity_ms";
        private const string KSessionId  = "reflect_session_id";

        private readonly long _thresholdMs;

        private int    _sessionCount;
        private int    _subsessionCount;
        private long   _sessionLengthMs;   // cumulative foreground time in current session
        private long   _lastActivityMs;    // epoch ms of last recorded activity (persisted)
        private long   _lastIntervalMs;    // gap measured at the most recent foreground
        private string _sessionId;

        // Runtime-only (reset on every Foreground; never persisted).
        private bool _inForeground;
        private long _foregroundStartMs;

        public int    SessionCount    => _sessionCount;
        public int    SubsessionCount => _subsessionCount;
        public long   SessionLengthMs => _sessionLengthMs;
        public long   LastIntervalMs  => _lastIntervalMs;
        public string SessionId       => _sessionId;

        public ReflectSession(long thresholdMs)
        {
            _thresholdMs = thresholdMs > 0 ? thresholdMs : 1_800_000L;
            Load();
        }

        /// <summary>
        /// Record that the app came to the foreground (cold start or resume) at
        /// <paramref name="nowMs"/>. Advances the session state machine and returns
        /// what the caller should emit.
        /// </summary>
        public ForegroundResult Foreground(long nowMs)
        {
            var r = new ForegroundResult();
            _lastIntervalMs = _lastActivityMs > 0 ? Math.Max(0, nowMs - _lastActivityMs) : 0;
            r.LastIntervalMs = _lastIntervalMs;

            bool firstEver = _lastActivityMs == 0 && _sessionCount == 0;
            bool isNew = firstEver || _lastIntervalMs > _thresholdMs;

            if (isNew)
            {
                // A prior session (if any) has now ended — surface it so the caller can
                // emit its session_end, even if the app was killed without a pause.
                if (_sessionCount > 0)
                {
                    r.ClosedPriorSession   = true;
                    r.PriorSessionLengthMs = _sessionLengthMs;
                    r.PriorSessionId       = _sessionId;
                    r.PriorSessionCount    = _sessionCount;
                }
                _sessionCount++;
                _subsessionCount = 1;
                _sessionLengthMs = 0;
                _sessionId = Guid.NewGuid().ToString("N");
                r.NewSession = true;
            }
            else
            {
                _subsessionCount++;
            }

            _inForeground = true;
            _foregroundStartMs = nowMs;
            _lastActivityMs = nowMs;
            Save();
            return r;
        }

        /// <summary>Record that the app went to the background at <paramref name="nowMs"/>.
        /// Accumulates the just-ended foreground interval into the session length.</summary>
        public void Background(long nowMs)
        {
            Advance(nowMs);
            _inForeground = false;
            _lastActivityMs = nowMs;
            Save();
        }

        /// <summary>Periodic keep-alive while foregrounded so a crash loses at most one
        /// interval of session time and <c>last_activity</c> stays fresh.</summary>
        public void Heartbeat(long nowMs)
        {
            if (!_inForeground) return;
            Advance(nowMs);
            _lastActivityMs = nowMs;
            Save();
        }

        private void Advance(long nowMs)
        {
            if (_inForeground)
            {
                _sessionLengthMs += Math.Max(0, nowMs - _foregroundStartMs);
                _foregroundStartMs = nowMs;
            }
        }

        /// <summary>GDPR/CCPA wipe — reset all session counters to a brand-new identity.</summary>
        public void WipeAll()
        {
            _sessionCount = 0; _subsessionCount = 0; _sessionLengthMs = 0;
            _lastActivityMs = 0; _lastIntervalMs = 0; _sessionId = null;
            _inForeground = false; _foregroundStartMs = 0;
            PlayerPrefs.DeleteKey(KCount);
            PlayerPrefs.DeleteKey(KSub);
            PlayerPrefs.DeleteKey(KLengthMs);
            PlayerPrefs.DeleteKey(KLastAct);
            PlayerPrefs.DeleteKey(KSessionId);
            PlayerPrefs.Save();
        }

        private void Load()
        {
            _sessionCount    = PlayerPrefs.GetInt(KCount, 0);
            _subsessionCount = PlayerPrefs.GetInt(KSub, 0);
            _sessionId       = PlayerPrefs.GetString(KSessionId, null);
            long.TryParse(PlayerPrefs.GetString(KLengthMs, "0"), out _sessionLengthMs);
            long.TryParse(PlayerPrefs.GetString(KLastAct, "0"), out _lastActivityMs);
        }

        private void Save()
        {
            PlayerPrefs.SetInt(KCount, _sessionCount);
            PlayerPrefs.SetInt(KSub, _subsessionCount);
            PlayerPrefs.SetString(KLengthMs, _sessionLengthMs.ToString());
            PlayerPrefs.SetString(KLastAct, _lastActivityMs.ToString());
            if (!string.IsNullOrEmpty(_sessionId)) PlayerPrefs.SetString(KSessionId, _sessionId);
            PlayerPrefs.Save();
        }
    }
}
