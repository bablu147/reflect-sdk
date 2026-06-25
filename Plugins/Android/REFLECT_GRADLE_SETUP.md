# Android Gradle setup for Reflect

## ✅ Automatic (default — nothing to do)

As of v2.1 the SDK **auto-configures the Android build for you.** A Unity Editor
post-processor (`Editor/ReflectAndroidGradlePostProcessor.cs`,
`IPostGenerateGradleAndroidProject`) runs after Unity generates the Gradle project
and injects, idempotently, on **every** build:

- the **ProGuard keep-rules** (via `consumerProguardFiles`) — so R8 can never strip
  `com.reflect.sdk.**` in a minified release, even if you never tick *Custom Proguard
  File*. This is the failure that used to make installs/attribution vanish in Play
  Store builds; it is now impossible to misconfigure.
- the three Google dependencies (`play-services-ads-identifier`, `installreferrer`,
  `play-services-appset`).

If you use the **External Dependency Manager (EDM4U)**, the bundled
`Editor/ReflectDependencies.xml` resolves the same deps via *Android Resolver →
Resolve*. Using both is harmless (Gradle dedupes).

To opt out of the auto post-processor (e.g. you manage Gradle yourself), add the
scripting define `REFLECT_SKIP_ANDROID_GRADLE` and follow the manual steps below.

---

## Manual setup (fallback / opt-out only)

The Java sources in this folder depend on two Google libraries that must be
added to Unity's generated Gradle project.

## Option A — enable Unity's `mainTemplate.gradle` (recommended)

1. In Unity: **Edit → Project Settings → Player → Android → Publishing Settings**
   → tick **Custom Main Gradle Template**.
2. Unity creates `Assets/Plugins/Android/mainTemplate.gradle`.
3. Inside the `dependencies { ... }` block add:

```gradle
dependencies {
    implementation 'com.google.android.gms:play-services-ads-identifier:18.0.1'
    implementation 'com.android.installreferrer:installreferrer:2.2'
    implementation 'com.google.android.gms:play-services-appset:16.0.2'   // Google App Set ID (optional)
    // ... keep any existing lines e.g. **DEPS**
}
```

(`play-services-appset` is optional — the SDK collects the App Set ID when present and
silently skips it otherwise.)

## Option B — use the Unity Resolver (External Dependency Manager)

If you have **External Dependency Manager for Unity (EDM4U)** installed:

1. Create `Assets/Reflect/Editor/ReflectDependencies.xml` with:

```xml
<dependencies>
    <androidPackages>
        <androidPackage spec="com.google.android.gms:play-services-ads-identifier:18.0.1"/>
        <androidPackage spec="com.android.installreferrer:installreferrer:2.2"/>
    </androidPackages>
</dependencies>
```

2. Run **Assets → External Dependency Manager → Android Resolver → Resolve**.

## Release builds (R8 / minification) — REQUIRED, or attribution breaks

**This is the #1 cause of "installs/attribution work in a debug APK but disappear from
the Play Store build."** Release builds run **R8 code-shrinking**, which strips
`com.reflect.sdk.**` — the native bridge for **both** device-info and install-referrer
collection. When it's stripped, `app_install` never fires and installs are recorded as
organic (no `click_id`) or not at all. Debug APKs don't run R8, so the problem only
appears once you ship to the Play Store.

The plugin ships as Java **source** (not an `.aar`), so its `consumer-rules.pro` is **not**
auto-applied. Do **one** of the following:

1. **Recommended:** In Unity → **Player → Android → Publishing Settings** → tick
   **Custom Proguard File**. Unity then appends `Assets/Plugins/Android/proguard-user.txt`
   (shipped with this SDK) to R8. If your project already has a `proguard-user.txt`, merge
   the SDK's rules into it.
2. Or add the rules to your `mainTemplate.gradle` (the one from Option A):

   ```gradle
   android {
       buildTypes {
           release {
               // keep the SDK's native bridge + callback classes
               proguardFiles 'proguard-user.txt'
           }
       }
   }
   ```

**Verify** with a minified Release build: `adb logcat | grep -i reflect` should show
`Device info collected.` and an `app_install` event. If you instead see
`Android collectDeviceInfo failed: …ClassNotFoundException com.reflect.sdk.ReflectBridge`,
the keep-rules are not being applied.

## Minimum settings

- `minSdkVersion` **21** or higher
- `compileSdkVersion` **33** or higher
- `targetSdkVersion` **33+** (required by Play Store in 2024+)
- Enable **AndroidX** (Player Settings → Android → Publishing Settings).

## Permissions & Play Console

- The **AD_ID permission** (`com.google.android.gms.permission.AD_ID`, needed for the GAID
  on Android 13+) is already declared in the SDK's `AndroidManifest.xml` and merges into
  your app automatically — **you do not add it**. (Remove it only for COPPA / kids apps.)
- In **Google Play Console** you must still declare advertising-ID usage:
  **App content → Advertising ID** (declare that the app uses it), and **Data safety**
  (collects *Device or other IDs*, plus App activity / Purchase history as applicable).
  These are policy declarations the SDK cannot make for you.
