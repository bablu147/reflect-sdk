using UnityEngine;

namespace Reflect
{
    /// <summary>
    /// Hidden MonoBehaviour that receives <c>UnitySendMessage</c> callbacks from the
    /// shared native core (via the Android/iOS Unity bridges). Three channels:
    ///   <c>OnCallResult</c>  — async result of a <c>handle()</c> call, tagged by callbackId
    ///   <c>OnDeepLink</c>    — the core's deep-link stream
    ///   <c>OnAttribution</c> — the core's attribution stream
    /// App lifecycle (foreground/background → sessions) is observed natively by the
    /// core (Android ActivityLifecycleCallbacks / iOS NotificationCenter), so this no
    /// longer forwards pause/tick. The GameObject name + these method names are the
    /// native↔C# bridge contract — do not rename without updating the bridges.
    /// </summary>
    internal sealed class ReflectCallbackReceiver : MonoBehaviour
    {
        internal const string GameObjectName = "__ReflectCallbackReceiver";

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

        // ─── UnitySendMessage targets (called by native code, single string arg) ───

        public void OnCallResult(string json)  => ReflectSDK.HandleCallResult(json);
        public void OnDeepLink(string json)     => ReflectSDK.HandleDeepLinkPayload(json);
        public void OnAttribution(string json)  => ReflectSDK.HandleAttributionPayload(json);
    }
}
