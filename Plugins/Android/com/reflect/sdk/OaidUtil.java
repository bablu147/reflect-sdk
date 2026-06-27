package com.reflect.sdk;

import android.content.Context;
import android.util.Log;

import org.json.JSONObject;

import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/**
 * China OAID (Open Anonymous Device ID) collection (OPT-IN, gated by
 * ReflectConfig.CollectOaid) via the MSA SDK.
 *
 * <p>The MSA SDK ({@code com.bun.miitmdid}) is NOT a public Maven artifact and is
 * absent from a standard Google-Play build, so it is loaded entirely by REFLECTION
 * here. That keeps the core SDK compiling and shipping <b>without</b> the MSA SDK on
 * the classpath; if a China build has integrated the MSA plugin, the OAID is
 * collected at runtime, otherwise this is a silent no-op. (Referencing the MSA
 * classes directly — as before — made the whole {@code com.reflect.sdk} package fail
 * to compile on every non-China build.) Adjust parity: the AdjustOaid plugin.
 */
final class OaidUtil {

    private static final String TAG = "Reflect";

    private OaidUtil() {}

    static void collect(final Context ctx, final JSONObject j) {
        try {
            final Class<?> helper      = Class.forName("com.bun.miitmdid.core.MdidSdkHelper");
            final Class<?> listenerItf = Class.forName("com.bun.miitmdid.interfaces.IIdentifierListener");
            final Class<?> supplierItf = Class.forName("com.bun.miitmdid.interfaces.IdSupplier");

            final CountDownLatch latch = new CountDownLatch(1);
            final String[] oaid = new String[1];

            // IIdentifierListener has a single callback (OnSupport(IdSupplier)); we
            // implement it via a dynamic proxy so there is no compile-time reference.
            Object listener = Proxy.newProxyInstance(
                listenerItf.getClassLoader(),
                new Class<?>[]{ listenerItf },
                new InvocationHandler() {
                    @Override public Object invoke(Object proxy, Method method, Object[] args) {
                        try {
                            Object supplier = (args != null && args.length > 0) ? args[args.length - 1] : null;
                            if (supplier != null && supplierItf.isInstance(supplier)) {
                                Method getOaid = supplierItf.getMethod("getOAID");
                                Object val = getOaid.invoke(supplier);
                                if (val != null) oaid[0] = val.toString();
                            }
                        } catch (Throwable ignored) {
                        } finally {
                            latch.countDown();
                        }
                        return null;
                    }
                });

            // MdidSdkHelper.InitSdk(Context, boolean, IIdentifierListener)
            Method initSdk = helper.getMethod("InitSdk", Context.class, boolean.class, listenerItf);
            initSdk.invoke(null, ctx, true, listener);

            latch.await(2, TimeUnit.SECONDS);
            if (oaid[0] != null && oaid[0].length() > 0) {
                j.put("oaid", oaid[0]);
                j.put("oaid_src", "msa");
            }
        } catch (ClassNotFoundException notIntegrated) {
            // MSA OAID SDK not present — expected on non-China builds. No-op.
        } catch (Throwable t) {
            Log.w(TAG, "OAID unavailable: " + t.getMessage());
        }
    }
}
