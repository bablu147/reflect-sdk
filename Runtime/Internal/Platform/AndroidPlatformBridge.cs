#if UNITY_ANDROID && !UNITY_EDITOR
namespace Reflect.Internal.Platform
{
    /// <summary>
    /// Android transport: JNI onto the Unity bridge in the reflect-android AAR
    /// (<c>com.reflect.sdk.ReflectUnityBridge</c>), a static facade over
    /// <c>com.reflect.core.ReflectCore.handle()</c>. Results return via
    /// UnitySendMessage (no JNI return values), so every call is fire-and-forget at
    /// the JNI boundary; the result rides <c>OnCallResult</c> tagged by callbackId.
    /// </summary>
    internal sealed class AndroidPlatformBridge : IPlatformBridge
    {
        private const string JavaBridge = "com.reflect.sdk.ReflectUnityBridge";
        private UnityEngine.AndroidJavaClass _bridge;

        private UnityEngine.AndroidJavaClass Bridge
        {
            get
            {
                if (_bridge == null) _bridge = new UnityEngine.AndroidJavaClass(JavaBridge);
                return _bridge;
            }
        }

        public void Initialize(string unityReceiverName, string configJson)
        {
            try
            {
                using (var unityPlayer = new UnityEngine.AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<UnityEngine.AndroidJavaObject>("currentActivity"))
                {
                    Bridge.CallStatic("initialize", activity, unityReceiverName, configJson);
                }
            }
            catch (System.Exception ex) { ReflectLogger.Error($"Android initialize failed: {ex}"); }
        }

        public void Call(string method, string argsJson, string callbackId)
        {
            try { Bridge.CallStatic("call", method, argsJson ?? "{}", callbackId ?? ""); }
            catch (System.Exception ex) { ReflectLogger.Error($"Android call '{method}' failed: {ex}"); }
        }
    }
}
#endif
