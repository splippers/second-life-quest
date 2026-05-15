// SLQuestBuild.cs — headless build entry point for CI or local batch-mode builds.
//
// Usage (from this project root):
//   unity -batchmode -nographics -quit \
//     -projectPath . \
//     -buildTarget Android \
//     -executeMethod SLQuest.Editor.SLQuestBuild.BuildAPK \
//     -logFile build.log
//
// Environment variables:
//   SLQUEST_KEYSTORE_PATH   — path to .keystore file (optional; debug key used if absent)
//   SLQUEST_KEYSTORE_PASS   — keystore password
//   SLQUEST_KEY_ALIAS       — key alias
//   SLQUEST_KEY_ALIAS_PASS  — key alias password
//   SLQUEST_OUTPUT          — output APK path (default: Build/SLQuest.apk)
#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SLQuest.Editor
{
    public static class SLQuestBuild
    {
        public static void BuildAPK()
        {
            string output = Env("SLQUEST_OUTPUT", "Build/SLQuest.apk");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            ApplyAndroidSettings();
            ApplySigning();

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes      = new[] { "Assets/Scenes/Bootstrap.unity" },
                locationPathName = output,
                target      = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options     = BuildOptions.None,
            });

            if (report.summary.result == BuildResult.Succeeded)
            {
                Console.WriteLine($"[Build] SUCCESS — {output} ({report.summary.totalSize / 1_048_576:F1} MB)");
                EditorApplication.Exit(0);
            }
            else
            {
                Console.Error.WriteLine($"[Build] FAILED: {report.summary.totalErrors} errors");
                foreach (var step in report.steps)
                    foreach (var msg in step.messages)
                        if (msg.type == LogType.Error)
                            Console.Error.WriteLine($"  {step.name}: {msg.content}");
                EditorApplication.Exit(1);
            }
        }

        private static void ApplyAndroidSettings()
        {
            PlayerSettings.companyName   = "SLQuestDev";
            PlayerSettings.productName   = "SLQuest";
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.slquest.viewer");
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.Android, Il2CppCompilerConfiguration.Release);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.stereoRenderingPath = StereoRenderingPath.SinglePass;
            PlayerSettings.Android.forceInternetPermission = true;
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
            EditorUserBuildSettings.buildAppBundle = false;
        }

        private static void ApplySigning()
        {
            string ksPath = Env("SLQUEST_KEYSTORE_PATH", "");
            if (string.IsNullOrEmpty(ksPath)) return; // use Unity debug key

            PlayerSettings.Android.keystoreName   = ksPath;
            PlayerSettings.Android.keystorePass   = Env("SLQUEST_KEYSTORE_PASS", "");
            PlayerSettings.Android.keyaliasName   = Env("SLQUEST_KEY_ALIAS", "");
            PlayerSettings.Android.keyaliasPass   = Env("SLQUEST_KEY_ALIAS_PASS", "");
        }

        private static string Env(string key, string fallback)
            => Environment.GetEnvironmentVariable(key) ?? fallback;
    }
}
#endif
