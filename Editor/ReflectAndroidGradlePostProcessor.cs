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
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[Reflect] Android Gradle auto-config skipped (" + ex.Message
                    + "). Enable 'Custom Proguard File' + add the deps manually — see "
                    + "Plugins/Android/REFLECT_GRADLE_SETUP.md.");
            }
#endif
        }

        private static void WriteProguardFile(string moduleDir)
        {
            var file = Path.Combine(moduleDir, "reflect-proguard.txt");
            File.WriteAllText(file, Marker + "\n" + string.Join("\n", KeepRules) + "\n");
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
