package com.reflect.sdk;

import android.content.Context;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.net.ConnectivityManager;
import android.net.NetworkCapabilities;
import android.net.NetworkInfo;
import android.os.Build;
import android.provider.Settings;
import android.telephony.TelephonyManager;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.WindowManager;

import com.google.android.gms.ads.identifier.AdvertisingIdClient;

import org.json.JSONObject;

import java.util.Locale;
import java.util.TimeZone;

/** Gathers device information into a JSON object. Runs off the main thread. */
final class DeviceInfoCollector {

    private static final String TAG = "Reflect";

    private DeviceInfoCollector() {}

    static String collect(Context ctx, boolean adConsent) throws Exception {
        JSONObject j = new JSONObject();

        // ── Identifiers ────────────────────────────────────────────────
        j.put("ad_consent", adConsent);
        if (adConsent) {
            try {
                AdvertisingIdClient.Info info = AdvertisingIdClient.getAdvertisingIdInfo(ctx);
                if (info != null && !info.isLimitAdTrackingEnabled() && info.getId() != null) {
                    j.put("gaid", info.getId());
                }
                j.put("lat_enabled", info != null && info.isLimitAdTrackingEnabled());
            } catch (Throwable t) {
                Log.w(TAG, "GAID unavailable: " + t.getMessage());
                j.put("lat_enabled", false);
            }
        } else {
            // Consent not granted — we cannot query LAT status without consent,
            // so omit lat_enabled rather than reporting a misleading value.
        }
        try {
            String ssaid = Settings.Secure.getString(ctx.getContentResolver(),
                    Settings.Secure.ANDROID_ID);
            j.put("android_id", ssaid);
        } catch (Throwable ignored) {}

        // ── OS / device ────────────────────────────────────────────────
        j.put("os", "Android");
        j.put("os_version", Build.VERSION.RELEASE);
        j.put("api_level", Build.VERSION.SDK_INT);
        j.put("device_model", Build.MODEL);
        j.put("device_manufacturer", Build.MANUFACTURER);
        j.put("device_brand", Build.BRAND);
        j.put("cpu_arch", Build.SUPPORTED_ABIS != null && Build.SUPPORTED_ABIS.length > 0
                ? Build.SUPPORTED_ABIS[0] : "unknown");

        try {
            WindowManager wm = (WindowManager) ctx.getSystemService(Context.WINDOW_SERVICE);
            DisplayMetrics m = new DisplayMetrics();
            if (wm != null && wm.getDefaultDisplay() != null) {
                wm.getDefaultDisplay().getRealMetrics(m);
                j.put("screen_width", m.widthPixels);
                j.put("screen_height", m.heightPixels);
                j.put("screen_density", m.densityDpi);
            }
        } catch (Throwable ignored) {}

        try {
            long total = Runtime.getRuntime().maxMemory();
            j.put("total_ram_mb", total / (1024 * 1024));
        } catch (Throwable ignored) {}

        // ── App ───────────────────────────────────────────────────────
        try {
            PackageManager pm = ctx.getPackageManager();
            PackageInfo pi = pm.getPackageInfo(ctx.getPackageName(), 0);
            j.put("app_bundle_id", ctx.getPackageName());
            j.put("app_version", pi.versionName);
            long code;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) code = pi.getLongVersionCode();
            else                                                 code = (long) pi.versionCode;
            j.put("app_version_code", code);
            j.put("first_install_time", pi.firstInstallTime);
            j.put("last_update_time", pi.lastUpdateTime);
            try {
                String installer = pm.getInstallerPackageName(ctx.getPackageName());
                if (installer != null) j.put("install_source", installer);
            } catch (Throwable ignored) {}
        } catch (Throwable ignored) {}

        // ── Locale ────────────────────────────────────────────────────
        Locale loc = Locale.getDefault();
        j.put("language", loc.getLanguage());
        j.put("locale", loc.toString());
        TimeZone tz = TimeZone.getDefault();
        j.put("timezone", tz.getID());
        j.put("tz_offset_min", tz.getOffset(System.currentTimeMillis()) / 60000);

        // ── Network / carrier ─────────────────────────────────────────
        try {
            ConnectivityManager cm = (ConnectivityManager) ctx.getSystemService(Context.CONNECTIVITY_SERVICE);
            if (cm != null) {
                String ct = "none";
                boolean vpn = false;
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                    NetworkCapabilities nc = cm.getNetworkCapabilities(cm.getActiveNetwork());
                    if (nc != null) {
                        if (nc.hasTransport(NetworkCapabilities.TRANSPORT_WIFI))       ct = "wifi";
                        else if (nc.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)) ct = "cellular";
                        else if (nc.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) ct = "ethernet";
                        vpn = nc.hasTransport(NetworkCapabilities.TRANSPORT_VPN);
                    }
                } else {
                    NetworkInfo ni = cm.getActiveNetworkInfo();
                    if (ni != null && ni.isConnected()) {
                        ct = ni.getType() == ConnectivityManager.TYPE_WIFI ? "wifi" : "cellular";
                    }
                }
                j.put("connection_type", ct);
                j.put("vpn_detected", vpn);
            }
        } catch (Throwable ignored) {}

        try {
            TelephonyManager tm = (TelephonyManager) ctx.getSystemService(Context.TELEPHONY_SERVICE);
            if (tm != null) {
                j.put("carrier", tm.getNetworkOperatorName());
                String op = tm.getNetworkOperator();
                if (op != null && op.length() >= 5) {
                    j.put("carrier_mcc", op.substring(0, 3));
                    j.put("carrier_mnc", op.substring(3));
                }
            }
        } catch (Throwable ignored) {}

        // ── Fraud signals ─────────────────────────────────────────────
        j.put("is_emulator", EmulatorDetector.isEmulator());
        j.put("is_rooted", RootDetector.isRooted());
        try {
            int mock = Settings.Secure.getInt(ctx.getContentResolver(), "mock_location", 0);
            j.put("mock_location_enabled", mock != 0);
        } catch (Throwable ignored) {}

        return j.toString();
    }
}
