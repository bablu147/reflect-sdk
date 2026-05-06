package com.reflect.sdk;

import android.os.Build;

import java.io.File;

/** Lightweight root detection. Covers the obvious cases, not a replacement for RootBeer. */
final class RootDetector {
    private RootDetector() {}

    private static final String[] KNOWN_SU_PATHS = {
            "/system/bin/su", "/system/xbin/su", "/sbin/su",
            "/system/sd/xbin/su", "/system/bin/failsafe/su",
            "/data/local/xbin/su", "/data/local/bin/su", "/data/local/su",
            "/su/bin/su", "/system/app/Superuser.apk"
    };

    static boolean isRooted() {
        if (Build.TAGS != null && Build.TAGS.contains("test-keys")) return true;
        for (String path : KNOWN_SU_PATHS) {
            if (new File(path).exists()) return true;
        }
        try {
            Process p = Runtime.getRuntime().exec(new String[]{"which", "su"});
            try (java.io.BufferedReader r = new java.io.BufferedReader(
                    new java.io.InputStreamReader(p.getInputStream()))) {
                return r.readLine() != null;
            }
        } catch (Throwable ignored) {}
        return false;
    }
}
