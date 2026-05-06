#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace Reflect.Editor
{
    /// <summary>
    /// Post-build step for iOS: adds required frameworks + Info.plist keys.
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

        private static void AddFrameworks(string projectPath)
        {
            var pbx = PBXProject.GetPBXProjectPath(projectPath);
            var proj = new PBXProject();
            proj.ReadFromFile(pbx);

#if UNITY_2019_3_OR_NEWER
            var targetGuid = proj.GetUnityFrameworkTargetGuid();
#else
            var targetGuid = proj.TargetGuidByName(PBXProject.GetUnityTargetName());
#endif
            proj.AddFrameworkToProject(targetGuid, "AdSupport.framework", false);
            proj.AddFrameworkToProject(targetGuid, "AppTrackingTransparency.framework", /*weak*/ true);
            proj.AddFrameworkToProject(targetGuid, "AdServices.framework", /*weak*/ true);

            proj.WriteToFile(pbx);
        }

        private static void AddInfoPlistKeys(string projectPath)
        {
            var plistPath = Path.Combine(projectPath, "Info.plist");
            if (!File.Exists(plistPath)) return;
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);

            if (!plist.root.values.ContainsKey("NSUserTrackingUsageDescription"))
                plist.root.SetString("NSUserTrackingUsageDescription", AttUsageDescription);

            plist.WriteToFile(plistPath);
        }

        /// <summary>
        /// Copies the SDK's PrivacyInfo.xcprivacy into the generated Xcode project
        /// and adds it to the Unity target's Copy Bundle Resources phase.
        /// Required by Apple (App Store) since 2024-05-01 for SDKs that access
        /// identifiers, file timestamps, or user defaults.
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
    }
}
#endif
