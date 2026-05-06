using System;
using Reflect.Internal;
using UnityEngine;

namespace Reflect
{
    /// <summary>
    /// Hidden MonoBehaviour that receives <c>UnitySendMessage</c> callbacks from
    /// Android / iOS native code, and drives tick / pause callbacks.
    /// </summary>
    internal sealed class ReflectCallbackReceiver : MonoBehaviour
    {
        internal const string GameObjectName = "__ReflectCallbackReceiver";

        internal Action<DeviceSnapshot>   OnDeviceInfoReadyHandler;
        internal Action<ReferralSnapshot> OnReferralReadyHandler;
        internal Action<IosTrackingStatus> OnAttStatusHandler;
        internal Action<bool>             OnPauseHandler;
        internal Action                   OnTickHandler;
        internal Action<string>           OnSkanCvUpdateHandler;

        internal bool PendingInstallEvent;
        internal Action<IosTrackingStatus> PendingAttCallback;
        internal Action<bool, string>     PendingSkanCvCallback;

        private static ReflectCallbackReceiver _instance;

        internal static ReflectCallbackReceiver Ensure()
        {
            if (_instance != null) return _instance;
            var go = GameObject.Find(GameObjectName);
            if (go == null)
            {
                go = new GameObject(GameObjectName);
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
            }
            _instance = go.GetComponent<ReflectCallbackReceiver>()
                        ?? go.AddComponent<ReflectCallbackReceiver>();
            return _instance;
        }

        // ─── Called from C# Update loop ─────────────────────────────────
        private void Update()   => OnTickHandler?.Invoke();

        private void OnApplicationPause(bool paused) => OnPauseHandler?.Invoke(paused);

        // ─── Called via UnitySendMessage from native code ───────────────
        // Android: UnityPlayer.UnitySendMessage("__ReflectCallbackReceiver", "OnDeviceInfoJson", json)
        // iOS:     UnitySendMessage("__ReflectCallbackReceiver", "OnDeviceInfoJson", json);

        public void OnDeviceInfoJson(string json)
        {
            try
            {
                var snap = DeviceSnapshot.FromJson(json);
                OnDeviceInfoReadyHandler?.Invoke(snap);
            }
            catch (Exception ex) { ReflectLogger.Error($"OnDeviceInfoJson parse failed: {ex}"); }
        }

        public void OnReferralJson(string json)
        {
            try
            {
                var snap = ReferralSnapshot.FromJson(json);
                OnReferralReadyHandler?.Invoke(snap);
            }
            catch (Exception ex) { ReflectLogger.Error($"OnReferralJson parse failed: {ex}"); }
        }

        public void OnAttStatusCode(string code)
        {
            int parsed;
            if (!int.TryParse(code, out parsed)) parsed = (int)IosTrackingStatus.Unavailable;
            var status = (IosTrackingStatus)parsed;
            OnAttStatusHandler?.Invoke(status);
        }

        public void OnSkanCvUpdateResult(string result)
        {
            bool ok = result == "ok";
            string error = ok ? null : (result.StartsWith("error:") ? result.Substring(6) : result);
            if (!ok) ReflectLogger.Warn($"SKAN CV update failed: {error}");
            else ReflectLogger.Info("SKAN CV update succeeded.");

            OnSkanCvUpdateHandler?.Invoke(result);

            var cb = PendingSkanCvCallback;
            PendingSkanCvCallback = null;
            cb?.Invoke(ok, error);
        }

        public void OnNativeError(string message)
        {
            ReflectLogger.Error($"[native] {message}");
        }
    }
}
