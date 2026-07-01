#if UNITY_IOS
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace Reflect.Editor
{
    /// <summary>
    /// Post-build step for iOS: adds required frameworks + Info.plist keys + the
    /// SKAdNetwork id list + the attribution report endpoint + privacy tracking domains.
    ///
    /// If a developer prefers to manage these manually, set the scripting define
    /// <c>REFLECT_SKIP_IOS_POSTPROCESS</c> in Player Settings.
    /// </summary>
    public static class ReflectBuildPostProcessor
    {
        /// <summary>
        /// Override this from your own editor script (before the build) if you want
        /// a custom NSUserTrackingUsageDescription string shown in the ATT prompt.
        /// </summary>
        public static string AttUsageDescription =
            "We use your device identifier to attribute your install to the "
          + "publisher that referred you, so creators get paid fairly.";

        /// <summary>
        /// Hostname of your Reflect ingestion server (the same host as
        /// <c>ReflectConfig.BaseUrl</c>), e.g. <c>api.reflect.yourdomain.workers.dev</c>.
        /// Because the runtime config is code-only, it isn't known at build time —
        /// set this from an editor script before building (or via the
        /// <c>REFLECT_SERVER_HOST</c> environment variable in CI). When set, the build
        /// injects <c>NSAdvertisingAttributionReportEndpoint</c> (so Apple delivers a
        /// copy of every SKAN postback to Reflect) and adds the host to
        /// <c>NSPrivacyTrackingDomains</c> (so iOS 17+ correctly blocks it under ATT
        /// denial and App Review doesn't flag undeclared tracking traffic). If left
        /// null the build logs a warning and these are skipped.
        /// </summary>
        public static string ServerHost;

        // Curated set of common SKAdNetworkIdentifier values (lowercase, ".skadnetwork").
        // Keep this updated from the consolidated MMP/network lists. Not exhaustive.
        // NOTE: must be declared BEFORE SkAdNetworkIds — C# runs static field
        // initializers in textual order, so SkAdNetworkIds = new List<>(DefaultSkAdNetworkIds)
        // would read a null array (TypeInitializationException) if this came after it.
        private static readonly string[] DefaultSkAdNetworkIds =
        {
            "cstr6suwn9.skadnetwork", // Google / AdMob
            "su67r6k2v3.skadnetwork", // ironSource
            "ludvb6z3bs.skadnetwork", // AppLovin
            "4dzt52r2t5.skadnetwork", // Unity Ads
            "4pfyvq9l8r.skadnetwork", // AdColony
            "v9wttpbfk9.skadnetwork", // Meta / Facebook
            "n38lu8286q.skadnetwork", // Meta / Facebook
            "gta9lk7p23.skadnetwork", // Vungle / Liftoff
            "7ug5zh24hu.skadnetwork", // Liftoff
            "f38h382jlk.skadnetwork", // Chartboost
            "wzmmz9fp6z.skadnetwork", // InMobi
            "kbd757ywx3.skadnetwork", // Mintegral
            "238da6jt44.skadnetwork", // Pangle (non-CN)
            "22mmun2rn5.skadnetwork", // Pangle (CN)
            "n6fk4nfna4.skadnetwork", // Fyber / DigitalTurbine
            "ecpz2srf59.skadnetwork", // Tapjoy
            "2u9pt9hc89.skadnetwork", // Moloco
            "3qy4746246.skadnetwork", // Smadex / Bidmachine
            "424m5254lk.skadnetwork", // Maticoo
            "5lm9lj6jb7.skadnetwork", // Yandex
            "578prtvx9j.skadnetwork", // Adikteev
            "9rd848q2bz.skadnetwork", // Aarki
            "hs6bdukanm.skadnetwork", // Criteo
            "mlmmfzh3r3.skadnetwork", // Sift
            "prcb7njmu6.skadnetwork", // Smaato
            "t38b2kh725.skadnetwork", // RTBHouse
            "tl55sbb4fm.skadnetwork", // Verve
            "x8uqf25wch.skadnetwork", // Verizon / Yahoo
            "zq492l623r.skadnetwork", // Yahoo
            "c6k4g5qg8m.skadnetwork", // Beeswax
        };

        /// <summary>
        /// SKAdNetwork identifiers injected into <c>Info.plist</c>'s
        /// <c>SKAdNetworkItems</c>. Without this list, mediated ad networks generate
        /// no SKAN postbacks and post-ATT iOS install attribution is silently lost.
        /// This is a maintained default covering the major networks; override or
        /// extend it from an editor script for networks not listed. IDs already
        /// present in the project's Info.plist (added by the host or an ad SDK) are
        /// not duplicated. (Declared after <see cref="DefaultSkAdNetworkIds"/> on purpose.)
        /// </summary>
        public static List<string> SkAdNetworkIds = new List<string>(DefaultSkAdNetworkIds);

        [PostProcessBuild(45)]
        public static void OnPostprocessBuild(BuildTarget target, string path)
        {
#if REFLECT_SKIP_IOS_POSTPROCESS
            return;
#else
            if (target != BuildTarget.iOS) return;
            AddFrameworks(path);
            AddInfoPlistKeys(path);
            AddPrivacyManifest(path);
#endif
        }

        private static string ResolveServerHost()
        {
            if (!string.IsNullOrEmpty(ServerHost)) return ServerHost;
            var env = System.Environment.GetEnvironmentVariable("REFLECT_SERVER_HOST");
            return string.IsNullOrEmpty(env) ? null : env;
        }

        private static void AddFrameworks(string projectPath)
        {
            var pbx = PBXProject.GetPBXProjectPath(projectPath);
            var proj = new PBXProject();
            proj.ReadFromFile(pbx);

#if UNITY_2019_3_OR_NEWER
            var targetGuid = proj.GetUnityFrameworkTargetGuid();
            var mainGuid = proj.GetUnityMainTargetGuid();
#else
            var targetGuid = proj.TargetGuidByName(PBXProject.GetUnityTargetName());
            var mainGuid = targetGuid;
#endif
            proj.AddFrameworkToProject(targetGuid, "AdSupport.framework", false);
            proj.AddFrameworkToProject(targetGuid, "AppTrackingTransparency.framework", /*weak*/ true);
            proj.AddFrameworkToProject(targetGuid, "AdServices.framework", /*weak*/ true);
            proj.AddFrameworkToProject(targetGuid, "StoreKit.framework", /*weak*/ true);
            // Frameworks the shared Swift core links that the legacy ObjC bridge did NOT
            // (so Unity never linked them): CryptoKit (HMAC-SHA256 signing), Network
            // (NWPathMonitor connectivity), CoreTelephony (carrier). Without these the
            // Swift symbols are undefined and the app aborts at load ("missing symbol").
            proj.AddFrameworkToProject(targetGuid, "CryptoKit.framework", false);
            proj.AddFrameworkToProject(targetGuid, "Network.framework", false);
            proj.AddFrameworkToProject(targetGuid, "CoreTelephony.framework", false);

            ConfigureSwift(proj, targetGuid, mainGuid);

            proj.WriteToFile(pbx);
        }

        /// <summary>
        /// The Reflect shared core ships as loose Swift (Plugins/iOS/ReflectCore.swift,
        /// ReflectCoreTypes.swift, ReflectUnityBridge.swift) and compiles into the
        /// UnityFramework target. Unity configures NO Swift build settings, so without
        /// these the Swift files fail to compile ("SWIFT_VERSION not set") and the app
        /// won't link the Swift runtime. The @_cdecl entry points the C# layer P/Invokes
        /// are C-linked, so no bridging header / -Swift.h import is needed.
        /// </summary>
        private static void ConfigureSwift(PBXProject proj, string frameworkGuid, string mainGuid)
        {
            // UnityFramework target — where the Plugins/iOS Swift compiles.
            proj.SetBuildProperty(frameworkGuid, "SWIFT_VERSION", "5.0");
            proj.SetBuildProperty(frameworkGuid, "CLANG_ENABLE_MODULES", "YES");
            proj.AddBuildProperty(frameworkGuid, "LD_RUNPATH_SEARCH_PATHS",
                "@executable_path/Frameworks @loader_path/Frameworks");

            // Export the Swift @_cdecl entry points the C# layer P/Invokes via
            // DllImport("__Internal"). Unity restricts the framework's exported symbols
            // to _il2cpp_* and dead-strips the rest; a Swift @_cdecl symbol is LOCAL by
            // default, so IL2CPP's __Internal resolver can't find it at startup and
            // aborts with "missing symbol called" (an ObjC++ extern "C" symbol is
            // exported by default, which is why the old bridge didn't need this). The
            // mach-o names carry a doubled leading underscore (C '_reflect_core_x' →
            // '__reflect_core_x'). Exporting also protects them from -dead_strip.
            proj.AddBuildProperty(frameworkGuid, "OTHER_LDFLAGS",
                "-Wl,-exported_symbol,__reflect_core_initialize " +
                "-Wl,-exported_symbol,__reflect_core_call " +
                "-Wl,-exported_symbol,__reflect_core_handle_url");

            // Main app target — embed the Swift standard libraries so the runtime the
            // core depends on ships in the .app bundle (required since the host project
            // has no Swift of its own).
            proj.SetBuildProperty(mainGuid, "ALWAYS_EMBED_SWIFT_STANDARD_LIBRARIES", "YES");
            proj.AddBuildProperty(mainGuid, "LD_RUNPATH_SEARCH_PATHS", "@executable_path/Frameworks");

            UnityEngine.Debug.Log("[Reflect] Configured Swift build settings (SWIFT_VERSION 5.0 + embedded "
                + "Swift stdlib) so the shared core compiles + links in the generated Xcode project.");
        }

        private static void AddInfoPlistKeys(string projectPath)
        {
            var plistPath = Path.Combine(projectPath, "Info.plist");
            if (!File.Exists(plistPath)) return;
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);

            if (!plist.root.values.ContainsKey("NSUserTrackingUsageDescription"))
                plist.root.SetString("NSUserTrackingUsageDescription", AttUsageDescription);

            AddSkAdNetworkIds(plist);

            // NSAdvertisingAttributionReportEndpoint — Apple sends a COPY of each
            // SKAN postback to this endpoint. Without it, Reflect's backend receives
            // zero SKAN data even when SKAdNetworkItems is present.
            var host = ResolveServerHost();
            if (!string.IsNullOrEmpty(host))
            {
                if (!plist.root.values.ContainsKey("NSAdvertisingAttributionReportEndpoint"))
                    plist.root.SetString("NSAdvertisingAttributionReportEndpoint", "https://" + host);
            }
            else
            {
                UnityEngine.Debug.LogWarning(
                    "[Reflect] ServerHost not set — skipped NSAdvertisingAttributionReportEndpoint and "
                    + "NSPrivacyTrackingDomains. SKAN postbacks will NOT reach your server and tracking "
                    + "traffic is undeclared. Set ReflectBuildPostProcessor.ServerHost (or the "
                    + "REFLECT_SERVER_HOST env var) to your ingestion host before building.");
            }

            plist.WriteToFile(plistPath);
        }

        private static void AddSkAdNetworkIds(PlistDocument plist)
        {
            if (SkAdNetworkIds == null || SkAdNetworkIds.Count == 0) return;

            PlistElementArray items;
            if (plist.root.values.TryGetValue("SKAdNetworkItems", out var existing)
                && existing is PlistElementArray existingArr)
                items = existingArr;
            else
                items = plist.root.CreateArray("SKAdNetworkItems");

            // Collect IDs already declared so we don't duplicate (case-insensitive).
            var present = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var el in items.values)
            {
                if (el is PlistElementDict dict
                    && dict.values.TryGetValue("SKAdNetworkIdentifier", out var idEl)
                    && idEl is PlistElementString idStr)
                    present.Add(idStr.value);
            }

            int added = 0;
            foreach (var id in SkAdNetworkIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                var norm = id.Trim().ToLowerInvariant();
                if (present.Contains(norm)) continue;
                present.Add(norm);
                var d = items.AddDict();
                d.SetString("SKAdNetworkIdentifier", norm);
                added++;
            }
            if (added > 0)
                UnityEngine.Debug.Log($"[Reflect] Injected {added} SKAdNetworkItems id(s) into Info.plist.");
        }

        /// <summary>
        /// Copies the SDK's PrivacyInfo.xcprivacy into the generated Xcode project,
        /// injects the tracking domain, and adds it to the Unity target's Copy Bundle
        /// Resources phase. Required by Apple (App Store) since 2024-05-01.
        /// </summary>
        private static void AddPrivacyManifest(string projectPath)
        {
            // Source file ships inside this package at Plugins/iOS/PrivacyInfo.xcprivacy.
            var sourceGuids = AssetDatabase.FindAssets("PrivacyInfo t:DefaultAsset");
            string source = null;
            foreach (var guid in sourceGuids)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (p.EndsWith("/Plugins/iOS/PrivacyInfo.xcprivacy"))
                {
                    source = p;
                    break;
                }
            }
            if (source == null)
            {
                UnityEngine.Debug.LogWarning(
                    "[Reflect] PrivacyInfo.xcprivacy not found in package — skipping privacy manifest step.");
                return;
            }

            // Place at: <proj>/Libraries/Reflect/PrivacyInfo.xcprivacy
            var destDir = Path.Combine(projectPath, "Libraries", "Reflect");
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, "PrivacyInfo.xcprivacy");
            File.Copy(source, dest, overwrite: true);

            // Inject the tracking domain so iOS 17+ blocks Reflect traffic under ATT
            // denial and App Review doesn't flag it as undeclared tracking.
            AddTrackingDomain(dest);

            var pbx = PBXProject.GetPBXProjectPath(projectPath);
            var proj = new PBXProject();
            proj.ReadFromFile(pbx);
#if UNITY_2019_3_OR_NEWER
            var targetGuid = proj.GetUnityMainTargetGuid();
#else
            var targetGuid = proj.TargetGuidByName(PBXProject.GetUnityTargetName());
#endif
            var fileGuid = proj.AddFile("Libraries/Reflect/PrivacyInfo.xcprivacy",
                                        "Libraries/Reflect/PrivacyInfo.xcprivacy");
            proj.AddFileToBuild(targetGuid, fileGuid);
            proj.WriteToFile(pbx);
        }

        private static void AddTrackingDomain(string privacyManifestPath)
        {
            var host = ResolveServerHost();
            if (string.IsNullOrEmpty(host)) return;   // warning already emitted in AddInfoPlistKeys
            try
            {
                var plist = new PlistDocument();
                plist.ReadFromFile(privacyManifestPath);

                PlistElementArray domains;
                if (plist.root.values.TryGetValue("NSPrivacyTrackingDomains", out var existing)
                    && existing is PlistElementArray arr)
                    domains = arr;
                else
                    domains = plist.root.CreateArray("NSPrivacyTrackingDomains");

                bool already = false;
                foreach (var el in domains.values)
                    if (el is PlistElementString s && s.value == host) { already = true; break; }
                if (!already)
                {
                    domains.AddString(host);
                    plist.WriteToFile(privacyManifestPath);
                    UnityEngine.Debug.Log($"[Reflect] Added '{host}' to NSPrivacyTrackingDomains.");
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Reflect] Could not inject NSPrivacyTrackingDomains: " + ex.Message);
            }
        }
    }
}
#endif
