#if UNITY_ANDROID
using System.IO;
using System.Text;
using UnityEditor.Android;
using UnityEngine;

namespace Reflect.Editor
{
    /// <summary>
    /// Runs AFTER Unity generates the Gradle project and AUTO-APPLIES, with zero
    /// manual setup:
    ///   1. the Reflect ProGuard keep-rules (via consumerProguardFiles on the
    ///      unityLibrary module — so they reach the app's R8 even when the dev
    ///      never ticked "Custom Proguard File"), and
    ///   2. the required Google dependencies (ads-identifier, install-referrer,
    ///      app-set).
    ///
    /// This makes the "works in debug, installs vanish in the minified Play Store
    /// build" failure — R8 stripping <c>com.reflect.sdk.**</c> (the native bridge
    /// for device + referrer collection) — impossible to misconfigure.
    ///
    /// Idempotent (a marker prevents duplicate injection) and defensive (a missing
    /// gradle block is logged + skipped, never throws). Opt out with the scripting
    /// define <c>REFLECT_SKIP_ANDROID_GRADLE</c>.
    /// </summary>
    public class ReflectAndroidGradlePostProcessor : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 999;

        private const string Marker = "// ReflectSDK-auto";

        private static readonly string[] KeepRules =
        {
            "-keep class com.reflect.sdk.** { *; }",
            "-keepclassmembers class com.reflect.sdk.** { *; }",
            "-keep class com.unity3d.player.UnityPlayer { *; }",
            "-keep class com.android.installreferrer.** { *; }",
            "-keepclasseswithmembernames class * { @com.android.installreferrer.** *; }",
            "-keep class com.google.android.gms.ads.identifier.** { *; }",
            "-keep class com.google.android.gms.appset.** { *; }",
            // Reflection-loaded optional SDKs (China OAID / Huawei referrer) — kept so
            // the Class.forName lookups resolve under R8 when the host bundles them.
            "-keep class com.bun.miitmdid.** { *; }",
            "-keep class com.huawei.hms.ads.installreferrer.** { *; }",
        };

        private static readonly string[] Deps =
        {
            "com.google.android.gms:play-services-ads-identifier:18.0.1",
            "com.android.installreferrer:installreferrer:2.2",
            "com.google.android.gms:play-services-appset:16.0.2",
        };

        public void OnPostGenerateGradleAndroidProject(string path)
        {
#if REFLECT_SKIP_ANDROID_GRADLE
            return;
#else
            try
            {
                // `path` is the generated unityLibrary Gradle module directory.
                WriteProguardFile(path);
                InjectGradle(Path.Combine(path, "build.gradle"));
#if REFLECT_COPPA
                // Kids/COPPA build: strip the AD_ID permission the SDK ships so a
                // children's app can never declare/collect the advertising ID. Pair
                // this define with ReflectConfig.CoppaCompliant=true (which also stops
                // the SDK reading GAID/IDFA at runtime).
                StripAdIdPermission(Path.Combine(path, "src", "main", "AndroidManifest.xml"));
#endif
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[Reflect] Android Gradle auto-config skipped (" + ex.Message
                    + "). Enable 'Custom Proguard File' + add the deps manually — see "
                    + "Plugins/Android/REFLECT_GRADLE_SETUP.md.");
            }
#endif
        }

#if REFLECT_COPPA
        // Remove every <uses-permission ... AD_ID ... /> declaration from the merged
        // manifest. Defensive: a missing manifest is logged + skipped, never throws.
        private static void StripAdIdPermission(string manifestPath)
        {
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning("[Reflect] COPPA: merged AndroidManifest.xml not found at "
                    + manifestPath + " — could not strip AD_ID permission. Remove it manually for kids apps.");
                return;
            }
            var lines = File.ReadAllLines(manifestPath);
            var kept = new System.Collections.Generic.List<string>(lines.Length);
            int removed = 0;
            foreach (var line in lines)
            {
                if (line.Contains("uses-permission") && line.Contains("com.google.android.gms.permission.AD_ID"))
                {
                    removed++;
                    continue;
                }
                kept.Add(line);
            }
            if (removed > 0)
            {
                File.WriteAllText(manifestPath, string.Join("\n", kept) + "\n");
                Debug.Log("[Reflect] COPPA build — stripped " + removed + " AD_ID permission declaration(s) from the manifest.");
            }
        }
#endif

        private static void WriteProguardFile(string moduleDir)
        {
            var file = Path.Combine(moduleDir, "reflect-proguard.txt");
            // ProGuard/R8 comments use '#', NOT '//'. The shared Marker is "// ..."
            // (valid in the build.gradle Groovy context) — emitting it as the first line
            // of a ProGuard file makes R8 fail with "Expected char '-'", breaking every
            // minified release build. Use a '#' comment header here instead.
            File.WriteAllText(file, "# ReflectSDK-auto (do not edit)\n"
                + string.Join("\n", KeepRules) + "\n");
        }

        private static void InjectGradle(string buildGradle)
        {
            if (!File.Exists(buildGradle))
            {
                Debug.LogWarning("[Reflect] unityLibrary/build.gradle not found — skipping auto-config.");
                return;
            }

            var text = File.ReadAllText(buildGradle);
            if (text.Contains(Marker)) return;   // already injected this build

            // 1) Apply the keep-rules to the consuming app's R8. consumerProguardFiles
            //    on this library module propagates to the launcher/app minification.
            text = InsertAfter(text, "defaultConfig {",
                "\n            consumerProguardFiles 'reflect-proguard.txt'  " + Marker);

            // 2) Add the Google runtime dependencies.
            var deps = new StringBuilder();
            foreach (var d in Deps)
                deps.Append("\n    implementation '" + d + "'  " + Marker);
            text = InsertAfter(text, "dependencies {", deps.ToString());

            File.WriteAllText(buildGradle, text);
            Debug.Log("[Reflect] Auto-applied ProGuard keep-rules + Google deps to the Android build — "
                + "no manual setup required.");
        }

        /// <summary>Insert <paramref name="insertion"/> right after the first
        /// occurrence of <paramref name="anchor"/> (a block's opening brace).
        /// Returns the text unchanged if the anchor isn't present (custom template).</summary>
        private static string InsertAfter(string text, string anchor, string insertion)
        {
            var i = text.IndexOf(anchor, System.StringComparison.Ordinal);
            if (i < 0) return text;
            var at = i + anchor.Length;
            return text.Substring(0, at) + insertion + text.Substring(at);
        }
    }
}
#endif
