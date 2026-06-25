package com.reflect.sdk;

import android.content.Context;
import android.util.Log;

import org.json.JSONObject;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import com.bun.miitmdid.core.MdidSdkHelper;
import com.bun.miitmdid.interfaces.IIdentifierListener;
import com.bun.miitmdid.interfaces.IdSupplier;

/**
 * China OAID (Open Anonymous Device ID) collection (OPT-IN, gated by
 * ReflectConfig.CollectOaid) via the MSA SDK. The MSA SDK delivers the OAID
 * asynchronously; we wait briefly for the callback. Requires the China OAID SDK
 * (com.bun.miitmdid) to be present — guarded so its absence is harmless.
 * Adjust parity: the AdjustOaid plugin.
 */
final class OaidUtil {

    private static final String TAG = "Reflect";

    private OaidUtil() {}

    static void collect(final Context ctx, final JSONObject j) {
        final CountDownLatch latch = new CountDownLatch(1);
        final String[] oaid = new String[1];
        try {
            MdidSdkHelper.InitSdk(ctx, true, new IIdentifierListener() {
                @Override
                public void OnSupport(IdSupplier supplier) {
                    try {
                        if (supplier != null) oaid[0] = supplier.getOAID();
                    } finally {
                        latch.countDown();
                    }
                }
            });
            latch.await(2, TimeUnit.SECONDS);
            if (oaid[0] != null && oaid[0].length() > 0) {
                j.put("oaid", oaid[0]);
                j.put("oaid_src", "msa");
            }
        } catch (Throwable t) {
            Log.w(TAG, "OAID unavailable: " + t.getMessage());
        }
    }
}
