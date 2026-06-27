package com.reflect.sdk;

import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.os.IBinder;
import android.os.Parcel;
import android.os.RemoteException;
import android.util.Log;

import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;

/**
 * Reads the Google Advertising ID (GAID).
 *
 * <p>Adjust parity: tries the Google Play Services advertising-id <b>service</b>
 * first (a direct bind + binder transaction, independent of any library), then falls
 * back to the {@code play-services-ads-identifier} <b>library</b>. The previous
 * implementation used only the library and always reported
 * {@code gaid_source=play_services}; binding the service directly means the GAID is
 * still obtained when the library is missing/broken, and the real source
 * ({@code service} | {@code library}) is recorded for backend match-quality scoring.
 * Each path is time-bounded so a wedged Play Services can't hang collection.
 */
final class GaidReader {

    private static final String TAG = "Reflect";
    private static final String GMS_PACKAGE = "com.google.android.gms";
    private static final String GMS_ACTION  = "com.google.android.gms.ads.identifier.service.START";
    private static final String IFACE = "com.google.android.gms.ads.identifier.internal.IAdvertisingIdService";

    private GaidReader() {}

    static final class Result {
        String  id;
        boolean lat;       // limit-ad-tracking enabled
        String  source;    // "service" | "library" | null
        int     attempt;   // how many mechanisms were tried
    }

    static Result read(Context ctx) {
        Result r = new Result();

        // 1) GMS service binding (primary) — bounded to 5s.
        try {
            r.attempt++;
            Conn conn = new Conn();
            Intent intent = new Intent(GMS_ACTION);
            intent.setPackage(GMS_PACKAGE);
            boolean bound = false;
            try {
                bound = ctx.bindService(intent, conn, Context.BIND_AUTO_CREATE);
                if (bound) {
                    IBinder binder = conn.take(5, TimeUnit.SECONDS);
                    if (binder != null) {
                        String id = transactGetId(binder);
                        if (id != null && id.length() > 0) {
                            r.id     = id;
                            r.lat    = transactIsLat(binder);
                            r.source = "service";
                            return r;
                        }
                    }
                }
            } finally {
                if (bound) { try { ctx.unbindService(conn); } catch (Throwable ignored) {} }
            }
        } catch (Throwable t) {
            Log.w(TAG, "GAID service path failed: " + t.getMessage());
        }

        // 2) play-services-ads-identifier library (fallback).
        try {
            r.attempt++;
            com.google.android.gms.ads.identifier.AdvertisingIdClient.Info info =
                    com.google.android.gms.ads.identifier.AdvertisingIdClient.getAdvertisingIdInfo(ctx);
            if (info != null) {
                r.lat = info.isLimitAdTrackingEnabled();
                if (!info.isLimitAdTrackingEnabled() && info.getId() != null) {
                    r.id     = info.getId();
                    r.source = "library";
                }
            }
        } catch (Throwable t) {
            Log.w(TAG, "GAID library path failed: " + t.getMessage());
        }
        return r;
    }

    private static String transactGetId(IBinder binder) throws RemoteException {
        Parcel data = Parcel.obtain(), reply = Parcel.obtain();
        try {
            data.writeInterfaceToken(IFACE);
            binder.transact(1 /* GET_AD_ID */, data, reply, 0);
            reply.readException();
            return reply.readString();
        } finally { reply.recycle(); data.recycle(); }
    }

    private static boolean transactIsLat(IBinder binder) throws RemoteException {
        Parcel data = Parcel.obtain(), reply = Parcel.obtain();
        try {
            data.writeInterfaceToken(IFACE);
            data.writeInt(1);
            binder.transact(2 /* IS_LAT_ENABLED */, data, reply, 0);
            reply.readException();
            return reply.readInt() != 0;
        } finally { reply.recycle(); data.recycle(); }
    }

    /** One-shot service connection that hands the binder to a waiting thread. */
    private static final class Conn implements ServiceConnection {
        private final LinkedBlockingQueue<IBinder> q = new LinkedBlockingQueue<>(1);
        private boolean taken;
        @Override public void onServiceConnected(ComponentName name, IBinder service) {
            try { q.put(service); } catch (InterruptedException e) { Thread.currentThread().interrupt(); }
        }
        @Override public void onServiceDisconnected(ComponentName name) {}
        IBinder take(long t, TimeUnit u) throws InterruptedException {
            if (taken) throw new IllegalStateException("binder already taken");
            taken = true;
            return q.poll(t, u);
        }
    }
}
