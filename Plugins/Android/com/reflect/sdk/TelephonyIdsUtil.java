package com.reflect.sdk;

import android.content.Context;
import android.os.Build;
import android.telephony.TelephonyManager;
import android.util.Log;

import org.json.JSONObject;

/**
 * China-market IMEI/MEID collection (OPT-IN, gated by ReflectConfig.CollectImei).
 * Requires the READ_PHONE_STATE permission and is blocked by the OS on Android 10+
 * (API 29) for normal apps — best-effort only. Adjust parity: the AdjustImei plugin.
 */
final class TelephonyIdsUtil {

    private static final String TAG = "Reflect";

    private TelephonyIdsUtil() {}

    @SuppressWarnings({"deprecation", "MissingPermission"})
    static void collect(Context ctx, JSONObject j) throws Exception {
        TelephonyManager tm = (TelephonyManager) ctx.getSystemService(Context.TELEPHONY_SERVICE);
        if (tm == null) return;
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                String imei = tm.getImei();
                if (imei != null) j.put("imei", imei);
                String meid = tm.getMeid();
                if (meid != null) j.put("meid", meid);
            } else {
                String deviceId = tm.getDeviceId();
                if (deviceId != null) j.put("device_id", deviceId);
            }
        } catch (Throwable t) {
            // SecurityException on Android 10+ / missing permission — expected.
            Log.w(TAG, "IMEI/MEID unavailable: " + t.getMessage());
        }
    }
}
