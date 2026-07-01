package com.reflect.sdk;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;

import com.reflect.core.ReflectCore;
import com.reflect.core.ReflectListener;
import com.reflect.core.ReflectResult;
import com.unity3d.player.UnityPlayer;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Map;

/**
 * Unity ↔ shared-core bridge (Android).
 *
 * This is the Unity analogue of the Flutter plugin's {@code ReflectPlugin.kt}: a
 * THIN translator that converts Unity's JNI + UnitySendMessage idioms onto the
 * wrapper-agnostic {@code com.reflect.core.ReflectCore.handle(method, args, result)}
 * surface (shipped in the reflect-android AAR). ALL SDK logic lives in the core;
 * this class owns nothing but marshaling.
 *
 * Direction of travel:
 *   C# → Java : static facade methods invoked via AndroidJavaClass.CallStatic.
 *               Args arrive as a single JSON string, parsed into a Map the core reads.
 *   Java → C# : UnityPlayer.UnitySendMessage(receiver, method, payload) — gives free
 *               main-thread marshaling (the receiver MonoBehaviour runs on Unity's
 *               player loop). Three channels:
 *                 OnCallResult  — async result of a handle() call, tagged by callbackId
 *                 OnDeepLink    — ReflectListener.onDeepLink stream (JSON)
 *                 OnAttribution — ReflectListener.onAttribution stream (JSON)
 */
public final class ReflectUnityBridge {

    private ReflectUnityBridge() {}

    private static ReflectCore sCore;
    private static String sReceiver;   // the Unity GameObject name to UnitySendMessage to

    // ── Java → C# chokepoint ────────────────────────────────────────────────
    private static void send(String method, String payload) {
        if (sReceiver == null) return;
        UnityPlayer.UnitySendMessage(sReceiver, method, payload == null ? "" : payload);
    }

    // ── C# → Java entry points ──────────────────────────────────────────────

    /**
     * Construct the core, wire the listener, and run initialize() through handle().
     * Must be called once before any {@link #call}.  configJson is the full
     * ReflectConfig.ToJson() (same key set the Flutter plugin sends).
     */
    public static void initialize(Activity activity, String unityReceiver, String configJson) {
        sReceiver = unityReceiver;
        Context ctx = activity != null ? activity.getApplicationContext() : UnityPlayer.currentActivity.getApplicationContext();
        sCore = new ReflectCore(ctx);
        sCore.setListener(new ReflectListener() {
            @Override public void onDeepLink(Object data) { send("OnDeepLink", jsonFromAny(data)); }
            @Override public void onAttribution(Object data) { send("OnAttribution", jsonFromAny(data)); }
        });
        sCore.start();
        if (activity != null) sCore.attachActivity(activity);
        // Run initialize through the same dispatch path; no callbackId (fire-and-forget).
        call("initialize", configJson, "");
    }

    /**
     * Generic dispatch. method = a core handle() method name; argsJson = a JSON
     * object of that method's args; callbackId = a non-empty correlation id when
     * C# wants the result back via OnCallResult, or "" for fire-and-forget.
     */
    public static void call(String method, String argsJson, final String callbackId) {
        if (sCore == null) { return; }
        Map<String, Object> args = parseObject(argsJson);
        sCore.handle(method, args, new ReflectResult() {
            @Override public void success(Object value) { reply(callbackId, true, value, null); }
            @Override public void error(String code, String message, Object details) {
                reply(callbackId, false, null, (code == null ? "error" : code) + (message != null ? (":" + message) : ""));
            }
            @Override public void notImplemented() { reply(callbackId, false, null, "not_implemented"); }
        });
    }

    /** Warm deep link from the host Activity's onNewIntent (C# forwards the Intent). */
    public static void onNewIntent(Intent intent) {
        if (sCore != null && intent != null && intent.getData() != null) {
            try { sCore.handleDeepLinkUri(intent.getData()); } catch (Throwable ignored) {}
        }
    }

    // ── result envelope (OnCallResult) ──────────────────────────────────────
    private static void reply(String callbackId, boolean ok, Object value, String error) {
        if (callbackId == null || callbackId.isEmpty()) return;   // fire-and-forget
        try {
            JSONObject o = new JSONObject();
            o.put("id", callbackId);
            o.put("ok", ok);
            if (error != null) o.put("error", error);
            if (value != null) o.put("value", wrap(value));
            send("OnCallResult", o.toString());
        } catch (Throwable t) {
            send("OnCallResult", "{\"id\":\"" + callbackId + "\",\"ok\":false,\"error\":\"serialize_failed\"}");
        }
    }

    // ── JSON helpers (org.json on Android) ──────────────────────────────────

    /** Wrap a core return value (String/Boolean/Number/Map/List/null) for JSON embedding. */
    @SuppressWarnings("unchecked")
    private static Object wrap(Object v) {
        if (v == null) return JSONObject.NULL;
        if (v instanceof Map) return new JSONObject((Map<String, Object>) v);
        if (v instanceof List) return new JSONArray((List<Object>) v);
        return v;   // String/Boolean/Number — org.json embeds directly
    }

    private static String jsonFromAny(Object data) {
        try { return wrap(data).toString(); } catch (Throwable t) { return "{}"; }
    }

    private static Map<String, Object> parseObject(String json) {
        if (json == null || json.isEmpty()) return new HashMap<String, Object>();
        try { return toMap(new JSONObject(json)); }
        catch (Throwable t) { return new HashMap<String, Object>(); }
    }

    /** Recursively convert a JSONObject → HashMap so the core's {@code as? T} casts
     *  (List<String>, Map, etc.) resolve to real Java collections. */
    private static Map<String, Object> toMap(JSONObject o) {
        Map<String, Object> m = new HashMap<String, Object>();
        Iterator<String> keys = o.keys();
        while (keys.hasNext()) {
            String k = keys.next();
            m.put(k, fromJson(o.opt(k)));
        }
        return m;
    }

    private static List<Object> toList(JSONArray a) {
        List<Object> l = new ArrayList<Object>(a.length());
        for (int i = 0; i < a.length(); i++) l.add(fromJson(a.opt(i)));
        return l;
    }

    private static Object fromJson(Object v) {
        if (v == null || v == JSONObject.NULL) return null;
        if (v instanceof JSONObject) return toMap((JSONObject) v);
        if (v instanceof JSONArray) return toList((JSONArray) v);
        return v;   // String/Boolean/Integer/Long/Double
    }
}
