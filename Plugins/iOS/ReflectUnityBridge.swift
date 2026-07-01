import Foundation

// ─────────────────────────────────────────────────────────────────────────────
//  Unity ↔ shared-core bridge (iOS).
//
//  The Unity analogue of the Flutter plugin's ReflectPlugin.swift: a THIN
//  translator onto ReflectCore.handle(method:args:result:). ALL SDK logic lives
//  in ReflectCore.swift (shipped alongside as loose Swift sources in Plugins/iOS,
//  compiled into the UnityFramework target). This file owns only marshaling.
//
//  C# → Swift : @_cdecl C entry points P/Invoked from C# (`[DllImport("__Internal")]`).
//  Swift → C# : UnitySendMessage (reached via @_silgen_name — no bridging header
//               needed), three channels: OnCallResult / OnDeepLink / OnAttribution.
//
//  Symbol names are deliberately distinct (`_reflect_core_*`) from the legacy
//  collection bridge (`_reflect_*` in ReflectBridge.mm) so both can coexist during
//  the migration; the legacy bridge is removed in the C# rewrite slice.
// ─────────────────────────────────────────────────────────────────────────────

/// Relay defined in ReflectUnitySend.mm (in-framework) that forwards to Unity's
/// UnitySendMessage. Routing through the .mm makes the linker force-link
/// UnitySendMessage from its static lib; a direct @_silgen_name("UnitySendMessage")
/// from Swift becomes a dynamic-lookup undefined dyld can't bind → startup abort.
@_silgen_name("ReflectUnitySend")
func ReflectUnitySend(_ obj: UnsafePointer<CChar>, _ method: UnsafePointer<CChar>, _ msg: UnsafePointer<CChar>)

final class ReflectUnityBridge: ReflectListener {
    static let shared = ReflectUnityBridge()
    private var core: ReflectCore?
    private var receiver: String = ""

    // ── Swift → C# chokepoint ───────────────────────────────────────────────
    func send(_ method: String, _ payload: String) {
        if receiver.isEmpty { return }
        receiver.withCString { o in
            method.withCString { m in
                payload.withCString { p in ReflectUnitySend(o, m, p) }
            }
        }
    }

    // ReflectListener — the deep-link + attribution streams.
    func onDeepLink(_ data: Any) { send("OnDeepLink", ReflectUnityBridge.jsonString(data)) }
    func onAttribution(_ data: Any) { send("OnAttribution", ReflectUnityBridge.jsonString(data)) }

    // ── C# → Swift entry points ─────────────────────────────────────────────
    func initialize(_ receiver: String, _ configJson: String) {
        self.receiver = receiver
        let c = ReflectCore()
        c.setListener(self)
        self.core = c
        call("initialize", configJson, "")   // run init through the same dispatch path
    }

    func call(_ method: String, _ argsJson: String, _ callbackId: String) {
        guard let core = core else { return }
        let args = ReflectUnityBridge.parseObject(argsJson)
        core.handle(method: method, args: args) { [weak self] value in
            self?.reply(callbackId, value)
        }
    }

    func handleUrl(_ url: String) {
        if let u = URL(string: url) { core?.handleIncomingURL(u) }
    }

    // ── result envelope (OnCallResult) ──────────────────────────────────────
    private func reply(_ callbackId: String, _ value: Any?) {
        if callbackId.isEmpty { return }   // fire-and-forget
        var ok = true
        var err: String? = nil
        var out: Any? = value
        if value is ReflectNotImplemented {
            ok = false; err = "not_implemented"; out = nil
        } else if let e = value as? ReflectError {
            ok = false; err = "\(e.code):\(e.message ?? "")"; out = nil
        }
        var o: [String: Any] = ["id": callbackId, "ok": ok]
        if let err = err { o["error"] = err }
        if let out = out { o["value"] = out }
        send("OnCallResult", ReflectUnityBridge.jsonString(o))
    }

    // ── JSON helpers ────────────────────────────────────────────────────────
    static func jsonString(_ v: Any) -> String {
        if JSONSerialization.isValidJSONObject(v),
           let d = try? JSONSerialization.data(withJSONObject: v),
           let s = String(data: d, encoding: .utf8) {
            return s
        }
        return "{}"
    }

    static func parseObject(_ json: String) -> [String: Any] {
        guard !json.isEmpty, let d = json.data(using: .utf8),
              let o = (try? JSONSerialization.jsonObject(with: d)) as? [String: Any] else {
            return [:]
        }
        return o
    }
}

// ── C-ABI exports (P/Invoked from C# IOSPlatformBridge) ──────────────────────

@_cdecl("_reflect_core_initialize")
public func _reflect_core_initialize(_ receiver: UnsafePointer<CChar>, _ configJson: UnsafePointer<CChar>) {
    ReflectUnityBridge.shared.initialize(String(cString: receiver), String(cString: configJson))
}

@_cdecl("_reflect_core_call")
public func _reflect_core_call(_ method: UnsafePointer<CChar>, _ argsJson: UnsafePointer<CChar>, _ callbackId: UnsafePointer<CChar>) {
    ReflectUnityBridge.shared.call(String(cString: method), String(cString: argsJson), String(cString: callbackId))
}

@_cdecl("_reflect_core_handle_url")
public func _reflect_core_handle_url(_ url: UnsafePointer<CChar>) {
    ReflectUnityBridge.shared.handleUrl(String(cString: url))
}
