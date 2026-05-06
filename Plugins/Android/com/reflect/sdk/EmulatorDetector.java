package com.reflect.sdk;

import android.os.Build;

/** Best-effort check for common Android emulators. Not foolproof — a signal, not ground truth. */
final class EmulatorDetector {
    private EmulatorDetector() {}

    static boolean isEmulator() {
        return (Build.FINGERPRINT != null &&
                    (Build.FINGERPRINT.startsWith("generic")
                  || Build.FINGERPRINT.startsWith("unknown")
                  || Build.FINGERPRINT.contains("emulator")
                  || Build.FINGERPRINT.contains("vbox")
                  || Build.FINGERPRINT.contains("test-keys")))
            || (Build.MODEL != null &&
                    (Build.MODEL.contains("google_sdk")
                  || Build.MODEL.contains("Emulator")
                  || Build.MODEL.contains("Android SDK built for")))
            || (Build.MANUFACTURER != null && Build.MANUFACTURER.contains("Genymotion"))
            || (Build.BRAND != null && Build.BRAND.startsWith("generic")
                && Build.DEVICE != null && Build.DEVICE.startsWith("generic"))
            || "google_sdk".equals(Build.PRODUCT)
            || (Build.HARDWARE != null &&
                    (Build.HARDWARE.contains("goldfish")
                  || Build.HARDWARE.contains("ranchu")));
    }
}
