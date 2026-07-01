using UnityEngine;

namespace Reflect.Internal.Platform
{
    /// <summary>
    /// Editor + unsupported-platform transport. There is no native core in the
    /// Editor, so this is inert (like the SDK's local debug mode): nothing is sent.
    /// Calls carrying a callbackId are immediately resolved with a null result so
    /// callback-based getters return their default instead of hanging.
    /// </summary>
    internal sealed class EditorPlatformBridge : IPlatformBridge
    {
        private string _receiver;

        public void Initialize(string unityReceiverName, string configJson)
        {
            _receiver = unityReceiverName;
            ReflectLogger.Info("Editor platform bridge — no native core; the SDK is inert in the Editor.");
        }

        public void Call(string method, string argsJson, string callbackId)
        {
            ReflectLogger.Info("Editor (no native core): " + method);
            if (string.IsNullOrEmpty(callbackId)) return;
            var go = GameObject.Find(_receiver);
            if (go != null)
                go.SendMessage("OnCallResult", "{\"id\":\"" + callbackId + "\",\"ok\":true}");
        }
    }
}
