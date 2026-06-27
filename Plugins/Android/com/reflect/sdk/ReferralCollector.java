package com.reflect.sdk;

import android.content.Context;
import android.util.Log;

import com.android.installreferrer.api.InstallReferrerClient;
import com.android.installreferrer.api.InstallReferrerStateListener;
import com.android.installreferrer.api.ReferrerDetails;

import org.json.JSONObject;

import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;

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
                    // Google Play referrer unavailable (non-Play install, no Play
                    // Services) → try the other store referrers (Meta/Samsung/Xiaomi
                    // content providers), then Huawei AppGallery.
                    Log.w(TAG, "Play referrer code=" + code + " — trying store referrers");
                    try { client.endConnection(); } catch (Throwable ignored) {}
                    JSONObject cp = tryContentProviderReferrers(ctx);
                    if (cp != null) cb.onResult(cp.toString());
                    else tryHuawei(ctx, cb, code);
                }
            }
            @Override public void onInstallReferrerServiceDisconnected() { /* no-op */ }
        });
    }

    /**
     * Huawei AppGallery install referrer (HMS Ads Kit), loaded entirely by REFLECTION
     * so the core SDK compiles and ships WITHOUT the Huawei HMS dependency on the
     * classpath. (Referencing the Huawei classes directly — as before — made the whole
     * {@code com.reflect.sdk} package fail to compile on every non-Huawei build.) If
     * HMS is present at runtime (an AppGallery device with the dep), the Huawei
     * referrer is collected; otherwise we return the original Play outcome.
     */
    private static void tryHuawei(final Context ctx, final Callback cb, final int playCode) {
        try {
            final Class<?> clientCls   = Class.forName("com.huawei.hms.ads.installreferrer.api.InstallReferrerClient");
            final Class<?> listenerItf = Class.forName("com.huawei.hms.ads.installreferrer.api.InstallReferrerStateListener");

            // InstallReferrerClient.newBuilder(ctx).build()
            Object builder = clientCls.getMethod("newBuilder", Context.class).invoke(null, ctx);
            final Object client = builder.getClass().getMethod("build").invoke(builder);

            Object listener = Proxy.newProxyInstance(
                listenerItf.getClassLoader(),
                new Class<?>[]{ listenerItf },
                new InvocationHandler() {
                    @Override public Object invoke(Object proxy, Method method, Object[] args) {
                        if (!"onInstallReferrerSetupFinished".equals(method.getName())) return null; // ignore disconnect
                        int code = (args != null && args.length > 0 && args[0] instanceof Integer) ? (Integer) args[0] : -1;
                        JSONObject j = new JSONObject();
                        try {
                            j.put("source", "huawei_install_referrer");
                            if (code == 0) {   // InstallReferrerResponse.OK
                                Object details = clientCls.getMethod("getInstallReferrer").invoke(client);
                                Class<?> dCls = details.getClass();
                                j.put("raw", reflectStr(dCls, details, "getInstallReferrer"));
                                j.put("click_ts", reflectLong(dCls, details, "getReferrerClickTimestampSeconds"));
                                j.put("install_ts", reflectLong(dCls, details, "getInstallBeginTimestampSeconds"));
                            } else {
                                j.put("setup_code", code);
                            }
                        } catch (Throwable t) {
                            Log.w(TAG, "huawei getInstallReferrer failed: " + t.getMessage());
                        }
                        cb.onResult(j.toString());
                        try { clientCls.getMethod("endConnection").invoke(client); } catch (Throwable ignored) {}
                        return null;
                    }
                });

            clientCls.getMethod("startConnection", listenerItf).invoke(client, listener);
        } catch (ClassNotFoundException notIntegrated) {
            // Huawei HMS not present — expected on non-Huawei builds. Return Play outcome.
            cb.onResult("{\"source\":\"play_install_referrer\",\"setup_code\":" + playCode + "}");
        } catch (Throwable t) {
            Log.w(TAG, "huawei referrer unavailable: " + t.getMessage());
            cb.onResult("{\"source\":\"play_install_referrer\",\"setup_code\":" + playCode + "}");
        }
    }

    /**
     * Best-effort install referrer from non-Play stores via their content providers
     * (no extra dependencies): Meta (Facebook + Instagram), Samsung Galaxy Store, and
     * Xiaomi GetApps. Closes the gap where paid installs from these large channels
     * were previously mis-attributed as organic. Each query is guarded — an absent
     * provider (or one not declared in <queries>) simply returns nothing. Adjust
     * parity: REFERRER_API_META / _SAMSUNG / _XIAOMI tagged via {@code referrer_api}.
     */
    private static JSONObject tryContentProviderReferrers(Context ctx) {
        final String[][] providers = {
            { "meta",    "content://com.facebook.katana.provider.InstallReferrerProvider/" },
            { "meta",    "content://com.instagram.android.provider.InstallReferrerProvider/" },
            { "samsung", "content://com.sec.android.app.samsungapps.installreferrer/" },
            { "xiaomi",  "content://com.xiaomi.market.installreferrer/" },
        };
        for (String[] p : providers) {
            android.database.Cursor c = null;
            try {
                android.net.Uri uri = android.net.Uri.parse(p[1] + ctx.getPackageName());
                c = ctx.getContentResolver().query(uri, null, null, null, null);
                if (c != null && c.moveToFirst()) {
                    String ref = cursorStr(c, "install_referrer");
                    if (ref == null && c.getColumnCount() > 0) ref = c.getString(0);
                    if (ref != null && ref.length() > 0) {
                        JSONObject j = new JSONObject();
                        j.put("source", p[0] + "_install_referrer");
                        j.put("referrer_api", p[0]);
                        j.put("raw", ref);
                        long ts = cursorLong(c, "actual_timestamp");
                        if (ts > 0) j.put("click_ts", ts);
                        Log.i(TAG, "Store referrer found via " + p[0]);
                        return j;
                    }
                }
            } catch (Throwable ignored) {
                /* provider absent / not visible / not permitted — try the next */
            } finally {
                if (c != null) { try { c.close(); } catch (Throwable ignored) {} }
            }
        }
        return null;
    }

    private static String cursorStr(android.database.Cursor c, String col) {
        try { int i = c.getColumnIndex(col); return i >= 0 ? c.getString(i) : null; }
        catch (Throwable t) { return null; }
    }
    private static long cursorLong(android.database.Cursor c, String col) {
        try { int i = c.getColumnIndex(col); return i >= 0 ? c.getLong(i) : 0L; }
        catch (Throwable t) { return 0L; }
    }

    private static String reflectStr(Class<?> c, Object o, String m) {
        try { Object v = c.getMethod(m).invoke(o); return v == null ? null : v.toString(); }
        catch (Throwable t) { return null; }
    }
    private static long reflectLong(Class<?> c, Object o, String m) {
        try { Object v = c.getMethod(m).invoke(o); return v instanceof Number ? ((Number) v).longValue() : 0L; }
        catch (Throwable t) { return 0L; }
    }
}
