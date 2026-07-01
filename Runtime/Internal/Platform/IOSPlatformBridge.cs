#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;

namespace Reflect.Internal.Platform
{
    /// <summary>
    /// iOS transport: P/Invoke onto the Swift bridge (<c>ReflectUnityBridge.swift</c>,
    /// shipped in Plugins/iOS) whose <c>@_cdecl</c> entry points forward to
    /// <c>ReflectCore.handle()</c>. Results return via UnitySendMessage on
    /// <c>OnCallResult</c> (tagged by callbackId), so the C entry points are void.
    /// </summary>
    internal sealed class IOSPlatformBridge : IPlatformBridge
    {
        [DllImport("__Internal")] private static extern void _reflect_core_initialize(string receiver, string configJson);
        [DllImport("__Internal")] private static extern void _reflect_core_call(string method, string argsJson, string callbackId);

        public void Initialize(string unityReceiverName, string configJson)
            => _reflect_core_initialize(unityReceiverName, configJson ?? "{}");

        public void Call(string method, string argsJson, string callbackId)
            => _reflect_core_call(method, argsJson ?? "{}", callbackId ?? "");
    }
}
#endif
