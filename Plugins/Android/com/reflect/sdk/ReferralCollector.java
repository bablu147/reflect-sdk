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
                try {
                    JSONObject j = new JSONObject();
                    j.put("source", "play_install_referrer");
                    if (code == InstallReferrerClient.InstallReferrerResponse.OK) {
                        try {
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
                    } else {
                        Log.w(TAG, "InstallReferrer setup returned code=" + code);
                        j.put("setup_code", code);
                    }
                    cb.onResult(j.toString());
                } catch (Throwable t) {
                    Log.e(TAG, "referral callback failed", t);
                    cb.onResult("{\"source\":\"play_install_referrer\",\"error\":\"" + t.getMessage() + "\"}");
                } finally {
                    try { client.endConnection(); } catch (Throwable ignored) {}
                }
            }
            @Override public void onInstallReferrerServiceDisconnected() { /* no-op */ }
        });
    }
}
