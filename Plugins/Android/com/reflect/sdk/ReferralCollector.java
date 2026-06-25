package com.reflect.sdk;

import android.content.Context;
import android.util.Log;

import com.android.installreferrer.api.InstallReferrerClient;
import com.android.installreferrer.api.InstallReferrerStateListener;
import com.android.installreferrer.api.ReferrerDetails;

import org.json.JSONObject;

/**
 * Reads the Play Install Referrer once per session and delivers the payload back
 * to Unity via the supplied {@link Callback}.
 */
final class ReferralCollector {

    private static final String TAG = "Reflect";

    interface Callback {
        void onResult(String json);
    }

    private ReferralCollector() {}

    static void collect(Context ctx, Callback cb) {
        final InstallReferrerClient client = InstallReferrerClient.newBuilder(ctx).build();
        client.startConnection(new InstallReferrerStateListener() {
            @Override public void onInstallReferrerSetupFinished(int code) {
                if (code == InstallReferrerClient.InstallReferrerResponse.OK) {
                    JSONObject j = new JSONObject();
                    try {
                        j.put("source", "play_install_referrer");
                        ReferrerDetails r = client.getInstallReferrer();
                        j.put("raw", r.getInstallReferrer());
                        j.put("click_ts", r.getReferrerClickTimestampSeconds());
                        j.put("install_ts", r.getInstallBeginTimestampSeconds());
                        try {
                            j.put("click_server_ts", r.getReferrerClickTimestampServerSeconds());
                            j.put("install_server_ts", r.getInstallBeginTimestampServerSeconds());
                            j.put("google_play_instant", r.getGooglePlayInstantParam());
                        } catch (Throwable ignored) { /* older lib */ }
                    } catch (Throwable t) {
                        Log.w(TAG, "getInstallReferrer failed: " + t.getMessage());
                    }
                    cb.onResult(j.toString());
                    try { client.endConnection(); } catch (Throwable ignored) {}
                } else {
                    // Google Play referrer unavailable (e.g. Huawei/AppGallery device,
                    // no Play Services) → fall back to the Huawei AppGallery referrer.
                    Log.w(TAG, "Play referrer code=" + code + " — trying Huawei AppGallery");
                    try { client.endConnection(); } catch (Throwable ignored) {}
                    tryHuawei(ctx, cb, code);
                }
            }
            @Override public void onInstallReferrerServiceDisconnected() { /* no-op */ }
        });
    }

    /** Huawei AppGallery install referrer (HMS Ads Kit). Mirrors the Play API.
     *  Guarded so the absence of the Huawei dependency is harmless. */
    private static void tryHuawei(Context ctx, Callback cb, int playCode) {
        try {
            final com.huawei.hms.ads.installreferrer.api.InstallReferrerClient hw =
                    com.huawei.hms.ads.installreferrer.api.InstallReferrerClient.newBuilder(ctx).build();
            hw.startConnection(new com.huawei.hms.ads.installreferrer.api.InstallReferrerStateListener() {
                @Override public void onInstallReferrerSetupFinished(int code) {
                    JSONObject j = new JSONObject();
                    try {
                        j.put("source", "huawei_install_referrer");
                        if (code == com.huawei.hms.ads.installreferrer.api.InstallReferrerClient.InstallReferrerResponse.OK) {
                            com.huawei.hms.ads.installreferrer.api.ReferrerDetails r = hw.getInstallReferrer();
                            j.put("raw", r.getInstallReferrer());
                            j.put("click_ts", r.getReferrerClickTimestampSeconds());
                            j.put("install_ts", r.getInstallBeginTimestampSeconds());
                        } else {
                            j.put("setup_code", code);
                        }
                    } catch (Throwable t) {
                        Log.w(TAG, "huawei getInstallReferrer failed: " + t.getMessage());
                    }
                    cb.onResult(j.toString());
                    try { hw.endConnection(); } catch (Throwable ignored) {}
                }
                @Override public void onInstallReferrerServiceDisconnected() { /* no-op */ }
            });
        } catch (Throwable t) {
            // Huawei SDK absent / unavailable — return the original Play outcome.
            Log.w(TAG, "huawei referrer unavailable: " + t.getMessage());
            cb.onResult("{\"source\":\"play_install_referrer\",\"setup_code\":" + playCode + "}");
        }
    }
}
