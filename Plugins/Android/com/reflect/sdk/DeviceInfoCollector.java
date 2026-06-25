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

    static String collect(Context ctx, boolean adConsent, boolean collectImei, boolean collectOaid) throws Exception {
        JSONObject j = new JSONObject();

        // China-market identifiers (opt-in, consent-gated). Adjust parity: the
        // separate imei/oaid plugins. Best-effort — never fail collection on these.
        if (adConsent && collectImei) {
            try { TelephonyIdsUtil.collect(ctx, j); } catch (Throwable ignored) {}
        }
        if (adConsent && collectOaid) {
            try { OaidUtil.collect(ctx, j); } catch (Throwable ignored) {}
        }

        // ── Identifiers ────────────────────────────────────────────────
        j.put("ad_consent", adConsent);
        if (adConsent) {
            // GAID with bounded retry — Adjust parity: gps_adid_attempt + gps_adid_src.
            AdvertisingIdClient.Info info = null;
            int gaidAttempt = 0;
            while (gaidAttempt < 3 && info == null) {
                gaidAttempt++;
                try { info = AdvertisingIdClient.getAdvertisingIdInfo(ctx); }
                catch (Throwable t) { Log.w(TAG, "GAID attempt " + gaidAttempt + " failed: " + t.getMessage()); }
            }
            j.put("gaid_attempt", gaidAttempt);
            if (info != null && !info.isLimitAdTrackingEnabled() && info.getId() != null) {
                j.put("gaid", info.getId());
                j.put("gaid_source", "play_services");
            }
            j.put("lat_enabled", info != null && info.isLimitAdTrackingEnabled());

            // Google App Set ID (Android 12+) — privacy-friendly per-developer ID,
            // useful for matching/dedup as GAID availability declines. Requires the
            // play-services-appset dependency; guarded so a missing dep is harmless.
            try {
                com.google.android.gms.tasks.Task<com.google.android.gms.appset.AppSetIdInfo> appSetTask =
                        com.google.android.gms.appset.AppSet.getClient(ctx).getAppSetIdInfo();
                com.google.android.gms.appset.AppSetIdInfo appSet =
                        com.google.android.gms.tasks.Tasks.await(appSetTask, 2, java.util.concurrent.TimeUnit.SECONDS);
                if (appSet != null && appSet.getId() != null) {
                    j.put("app_set_id", appSet.getId());
                }
            } catch (Throwable ignored) { /* dep missing or unavailable */ }

            // Amazon Fire Advertising ID (Fire OS only) — Adjust parity: fire_adid.
            try {
                android.content.ContentResolver cr = ctx.getContentResolver();
                String fireAdid = Settings.Secure.getString(cr, "advertising_id");
                if (fireAdid != null && fireAdid.length() > 0) {
                    j.put("fire_adid", fireAdid);
                    j.put("fire_tracking_enabled", Settings.Secure.getInt(cr, "limit_ad_tracking", 0) == 0);
                }
            } catch (Throwable ignored) {}
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
        j.put("os_build", Build.ID);             // Adjust parity: os_build
        j.put("hardware_name", Build.DISPLAY);   // Adjust parity: hardware_name

        // Device taxonomy from Configuration (Adjust parity: device_type / ui_mode /
        // screen_size / screen_format). Guarded — never fail collection on these.
        try {
            android.content.res.Configuration cfg = ctx.getResources().getConfiguration();
            int sizeMask = cfg.screenLayout & android.content.res.Configuration.SCREENLAYOUT_SIZE_MASK;
            String screenSize =
                  sizeMask == android.content.res.Configuration.SCREENLAYOUT_SIZE_XLARGE ? "xlarge"
                : sizeMask == android.content.res.Configuration.SCREENLAYOUT_SIZE_LARGE  ? "large"
                : sizeMask == android.content.res.Configuration.SCREENLAYOUT_SIZE_NORMAL ? "normal"
                : sizeMask == android.content.res.Configuration.SCREENLAYOUT_SIZE_SMALL  ? "small"
                : "undefined";
            j.put("screen_size", screenSize);
            int longMask = cfg.screenLayout & android.content.res.Configuration.SCREENLAYOUT_LONG_MASK;
            j.put("screen_format", longMask == android.content.res.Configuration.SCREENLAYOUT_LONG_YES ? "long" : "normal");

            int uiType = cfg.uiMode & android.content.res.Configuration.UI_MODE_TYPE_MASK;
            String uiMode =
                  uiType == android.content.res.Configuration.UI_MODE_TYPE_TELEVISION ? "tv"
                : uiType == android.content.res.Configuration.UI_MODE_TYPE_WATCH      ? "watch"
                : uiType == android.content.res.Configuration.UI_MODE_TYPE_CAR        ? "car"
                : uiType == android.content.res.Configuration.UI_MODE_TYPE_DESK       ? "desk"
                : "normal";
            j.put("ui_mode", uiMode);

            String deviceType =
                  uiType == android.content.res.Configuration.UI_MODE_TYPE_TELEVISION ? "tv"
                : uiType == android.content.res.Configuration.UI_MODE_TYPE_WATCH      ? "watch"
                : sizeMask >= android.content.res.Configuration.SCREENLAYOUT_SIZE_LARGE ? "tablet"
                : "phone";
            j.put("device_type", deviceType);
        } catch (Throwable ignored) {}

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
            // Adjust parity: is_system_app — ROM-preloaded apps are an organic-hijack
            // fraud signal when combined with a non-Play install source.
            j.put("is_system_app",
                    (ctx.getApplicationInfo().flags & android.content.pm.ApplicationInfo.FLAG_SYSTEM) != 0);
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
