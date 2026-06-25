package com.reflect.sdk;

import android.app.Activity;
import android.content.Context;
import android.util.Log;

import com.unity3d.player.UnityPlayer;

/**
 * Entry point called from Unity C# via AndroidJavaClass.
 * All methods are static and non-blocking — results are delivered back to Unity
 * via {@link UnityPlayer#UnitySendMessage(String, String, String)}.
 */
public final class ReflectBridge {

    private static final String TAG = "Reflect";

    private static Context appCtx;
    private static String unityReceiver;
    private static volatile boolean adConsent = true;
    private static volatile boolean collectImei = false;   // China-market opt-in (Adjust: imei plugin)
    private static volatile boolean collectOaid = false;   // China-market opt-in (Adjust: oaid plugin)

    private ReflectBridge() {}

    /** Initialize with the Unity activity + GameObject name to send callbacks to. */
    public static void initialize(Activity activity, String receiverName, boolean advertisingConsent,
                                  boolean imei, boolean oaid) {
        appCtx = activity != null ? activity.getApplicationContext() : null;
        unityReceiver = receiverName;
        adConsent = advertisingConsent;
        collectImei = imei;
        collectOaid = oaid;
        Log.i(TAG, "Initialized. receiver=" + receiverName + " adConsent=" + advertisingConsent
                + " imei=" + imei + " oaid=" + oaid);
    }

    public static void setAdvertisingConsent(boolean granted) {
        adConsent = granted;
    }

    /** Collect device info in a background thread. */
    public static void collectDeviceInfo() {
        if (appCtx == null || unityReceiver == null) {
            Log.w(TAG, "collectDeviceInfo called before initialize");
            return;
        }
        new Thread(() -> {
            try {
                String json = DeviceInfoCollector.collect(appCtx, adConsent, collectImei, collectOaid);
                send("OnDeviceInfoJson", json);
            } catch (Throwable t) {
                Log.e(TAG, "collectDeviceInfo failed", t);
                send("OnNativeError", "device-info: " + t.getMessage());
            }
        }, "reflect-device-info").start();
    }

    /** Collect install referrer via Play Install Referrer API. */
    public static void collectReferral() {
        if (appCtx == null || unityReceiver == null) {
            Log.w(TAG, "collectReferral called before initialize");
            return;
        }
        try {
            ReferralCollector.collect(appCtx, json -> send("OnReferralJson", json));
        } catch (Throwable t) {
            Log.e(TAG, "collectReferral failed", t);
            send("OnNativeError", "referral: " + t.getMessage());
        }
    }

    static void send(String method, String payload) {
        if (unityReceiver == null) return;
        try {
            UnityPlayer.UnitySendMessage(unityReceiver, method, payload == null ? "" : payload);
        } catch (Throwable t) {
            Log.e(TAG, "UnitySendMessage failed for " + method, t);
        }
    }

    static boolean isAdConsentGranted() { return adConsent; }
}
