// SLQuestSetup.cs — Unity Editor menu: Tools → SLQuest → Create Bootstrap Scene
// Run this once after opening the project for the first time.
// It creates Scenes/Bootstrap.unity with all subsystems wired and configured.
#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;
using SLQuest.Core;
using SLQuest.Network;
using SLQuest.World;
using SLQuest.Avatar;
using SLQuest.Assets;
using SLQuest.Chat;
using SLQuest.Inventory;
using SLQuest.Building;
using SLQuest.Voice;
using SLQuest.VR;
using SLQuest.UI;
using SLQuest.Rendering;
using SLQuest.Scripting;

namespace SLQuest.Editor
{
    public static class SLQuestSetup
    {
        private const string SCENE_PATH = "Assets/Scenes/Bootstrap.unity";

        [MenuItem("Tools/SLQuest/Create Bootstrap Scene", priority = 1)]
        public static void CreateBootstrapScene()
        {
            // Ensure Scenes directory exists
            Directory.CreateDirectory("Assets/Scenes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ── URP pipeline asset ─────────────────────────────────────────────
            EnsureURPPipeline();

            // ── Root: SLApplication ───────────────────────────────────────────
            var root = new GameObject("[SLQuest]");
            var app  = root.AddComponent<SLApplication>();

            // ── Core: MainThreadDispatcher ────────────────────────────────────
            new GameObject("[MainThreadDispatcher]").AddComponent<MainThreadDispatcher>()
                .transform.SetParent(root.transform);

            // ── OVR (Meta XR) ─────────────────────────────────────────────────
            var ovrGo = new GameObject("[OVRManager]");
            var ovrMgr = ovrGo.AddComponent<OVRManager>();
            ovrMgr.trackingOriginType           = OVRManager.TrackingOrigin.FloorLevel;
            ovrMgr.usePositionTracking          = true;
            ovrMgr.useRotationTracking          = true;
            ovrMgr.handTrackingSupport          = OVRManager.HandTrackingSupport.ControllersAndHands;
            ovrMgr.suggestedCpuPerfLevel        = OVRManager.ProcessorPerformanceLevel.SustainedHigh;
            ovrMgr.suggestedGpuPerfLevel        = OVRManager.ProcessorPerformanceLevel.SustainedHigh;
            ovrMgr.enableDynamicResolution      = false;
            ovrGo.transform.SetParent(root.transform);

            // ── OVR Camera Rig ────────────────────────────────────────────────
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab");
            GameObject rigGo;
            if (rigPrefab != null)
            {
                rigGo = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab);
                rigGo.transform.SetParent(root.transform);
            }
            else
            {
                Debug.LogWarning("[SLSetup] OVRCameraRig prefab not found — create it manually from the Meta XR SDK.");
                rigGo = new GameObject("[OVRCameraRig]");
                rigGo.transform.SetParent(root.transform);
                rigGo.AddComponent<OVRCameraRig>();
            }

            // ── VR Rig ────────────────────────────────────────────────────────
            var vrRig = rigGo.AddComponent<VRRig>();
            var locomotion = rigGo.AddComponent<LocomotionSystem>();

            // Passthrough
            var passthrough = rigGo.AddComponent<OVRPassthroughLayer>();
            passthrough.projectionSurfaceType = OVRPassthroughLayer.ProjectionSurfaceType.Reconstructed;

            // Hand controllers (left)
            var leftHand = new GameObject("[LeftHand]");
            leftHand.transform.SetParent(rigGo.transform);
            var lhc = leftHand.AddComponent<HandController>();
            SetPrivateField(lhc, "side", HandSide.Left);
            var lRay = leftHand.AddComponent<LineRenderer>();
            lRay.startWidth = 0.005f; lRay.endWidth = 0.002f;
            lRay.material   = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            lRay.material.color = new Color(0.5f, 0.8f, 1f, 0.6f);

            // Hand controllers (right)
            var rightHand = new GameObject("[RightHand]");
            rightHand.transform.SetParent(rigGo.transform);
            var rhc = rightHand.AddComponent<HandController>();
            SetPrivateField(rhc, "side", HandSide.Right);
            var rRay = rightHand.AddComponent<LineRenderer>();
            rRay.startWidth = 0.005f; rRay.endWidth = 0.002f;
            rRay.material   = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            rRay.material.color = new Color(0.5f, 0.8f, 1f, 0.6f);

            // Teleport arc on right hand
            var arc = rightHand.AddComponent<TeleportArc>();
            var arcLine = rightHand.GetComponent<LineRenderer>() ?? rightHand.AddComponent<LineRenderer>();

            // Vignette controller
            var vigGo = new GameObject("[VignetteController]");
            vigGo.transform.SetParent(rigGo.transform);
            var vig = vigGo.AddComponent<VignetteController>();

            // Wire VRRig serialized fields via SerializedObject
            var rigSO = new SerializedObject(vrRig);
            rigSO.FindProperty("leftController").objectReferenceValue  = lhc;
            rigSO.FindProperty("rightController").objectReferenceValue = rhc;
            rigSO.FindProperty("locomotion").objectReferenceValue      = locomotion;
            rigSO.ApplyModifiedProperties();

            var locSO = new SerializedObject(locomotion);
            locSO.FindProperty("vignette").objectReferenceValue = vig;
            locSO.ApplyModifiedProperties();

            // ── Network subsystems ────────────────────────────────────────────
            var netGo  = AddChild(root, "[Network]");
            var net    = netGo.AddComponent<SLNetworkManager>();
            var login  = netGo.AddComponent<LoginManager>();
            var caps   = netGo.AddComponent<Network.CapabilityHandler>();

            // ── World subsystems ──────────────────────────────────────────────
            var worldGo  = AddChild(root, "[World]");
            var region   = worldGo.AddComponent<RegionManager>();
            var objects  = worldGo.AddComponent<ObjectManager>();
            var terrain  = worldGo.AddComponent<TerrainManager>();

            // ── Assets ────────────────────────────────────────────────────────
            var assetsGo = AddChild(root, "[Assets]");
            var assets   = assetsGo.AddComponent<AssetManager>();

            // ── Avatars ───────────────────────────────────────────────────────
            var avatarRoot = AddChild(root, "[Avatars]");
            var avatarMgr  = avatarRoot.AddComponent<AvatarManager>();
            var localAv    = avatarRoot.AddComponent<LocalAvatar>();
            var cc         = avatarRoot.AddComponent<CharacterController>();
            cc.center = new Vector3(0, 0.9f, 0);
            cc.height = 1.8f;
            cc.radius = 0.25f;
            var appearance = avatarRoot.AddComponent<AvatarAppearance>();

            // ── Chat / Inventory / Building ───────────────────────────────────
            var serviceGo  = AddChild(root, "[Services]");
            var chat       = serviceGo.AddComponent<ChatManager>();
            var inventory  = serviceGo.AddComponent<InventoryManager>();
            var building   = serviceGo.AddComponent<BuildingManager>();
            var lslBridge  = serviceGo.AddComponent<LSLBridge>();

            // ── Voice ─────────────────────────────────────────────────────────
            var voiceGo = AddChild(root, "[Voice]");
            var voice   = voiceGo.AddComponent<VoiceManager>();

            // ── UI ────────────────────────────────────────────────────────────
            var uiGo = AddChild(root, "[UI]");
            var uiMgr = uiGo.AddComponent<VRUIManager>();
            // Panel root anchored in front of camera
            var panelRoot = new GameObject("[PanelRoot]");
            panelRoot.transform.SetParent(uiGo.transform);
            panelRoot.transform.localPosition = new Vector3(0, 1.6f, 1.5f);
            var uiSO = new SerializedObject(uiMgr);
            uiSO.FindProperty("panelRoot").objectReferenceValue = panelRoot.transform;
            uiSO.ApplyModifiedProperties();

            // ── Rendering ─────────────────────────────────────────────────────
            var renderGo = AddChild(root, "[Rendering]");
            var matConv  = renderGo.AddComponent<MaterialConverter>();

            // ── Wire SLApplication ────────────────────────────────────────────
            var appSO = new SerializedObject(app);
            appSO.FindProperty("networkManager").objectReferenceValue  = net;
            appSO.FindProperty("loginManager").objectReferenceValue    = login;
            appSO.FindProperty("regionManager").objectReferenceValue   = region;
            appSO.FindProperty("objectManager").objectReferenceValue   = objects;
            appSO.FindProperty("terrainManager").objectReferenceValue  = terrain;
            appSO.FindProperty("avatarManager").objectReferenceValue   = avatarMgr;
            appSO.FindProperty("localAvatar").objectReferenceValue     = localAv;
            appSO.FindProperty("assetManager").objectReferenceValue    = assets;
            appSO.FindProperty("chatManager").objectReferenceValue     = chat;
            appSO.FindProperty("inventoryManager").objectReferenceValue = inventory;
            appSO.FindProperty("buildingManager").objectReferenceValue = building;
            appSO.FindProperty("voiceManager").objectReferenceValue    = voice;
            appSO.FindProperty("vrRig").objectReferenceValue           = vrRig;
            appSO.FindProperty("uiManager").objectReferenceValue       = uiMgr;
            appSO.FindProperty("materialConverter").objectReferenceValue = matConv;
            appSO.ApplyModifiedProperties();

            // ── Directional light ──────────────────────────────────────────────
            var lightGo = new GameObject("Directional Light");
            var light   = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.color = new Color(1f, 0.97f, 0.88f);
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // ── Save scene ────────────────────────────────────────────────────
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, SCENE_PATH);
            AssetDatabase.Refresh();

            Debug.Log($"[SLSetup] Bootstrap scene saved to {SCENE_PATH}");
            EditorUtility.DisplayDialog("SLQuest Setup",
                $"Bootstrap scene created at {SCENE_PATH}\n\n" +
                "Remaining manual steps:\n" +
                "1. Set your Meta App ID in Assets/Plugins/Oculus/OculusProjectConfig\n" +
                "2. File → Build Settings → Switch Platform → Android\n" +
                "3. Add Bootstrap.unity to Build Settings → Scenes\n" +
                "4. Build And Run", "OK");
        }

        [MenuItem("Tools/SLQuest/Configure Android Build Settings", priority = 2)]
        public static void ConfigureAndroidSettings()
        {
            PlayerSettings.companyName   = "SLQuestDev";
            PlayerSettings.productName   = "SLQuest";
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.slquest.viewer");
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;

            // IL2CPP + ARM64 — required for Quest 3
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.Android, Il2CppCompilerConfiguration.Release);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            // Texture compression
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;

            // Linear colour space for VR
            PlayerSettings.colorSpace = ColorSpace.Linear;

            // Multiview stereo rendering
            PlayerSettings.stereoRenderingPath = StereoRenderingPath.SinglePass;

            // Internet permission for SL network
            PlayerSettings.Android.forceInternetPermission = true;

            Debug.Log("[SLSetup] Android build settings configured for Quest 3");
            EditorUtility.DisplayDialog("SLQuest Setup",
                "Android build settings applied!\n\n" +
                "IL2CPP + ARM64 + ASTC + Linear colour space enabled.", "OK");
        }

        [MenuItem("Tools/SLQuest/Validate Setup", priority = 3)]
        public static void ValidateSetup()
        {
            var issues = new System.Text.StringBuilder();

            // Check DLLs
            if (!File.Exists("Assets/Plugins/LibreMetaverse/LibreMetaverse.dll"))
                issues.AppendLine("✗ LibreMetaverse.dll not found — run tools/fetch_packages.py");
            else
                issues.AppendLine("✓ LibreMetaverse.dll present");

            if (!File.Exists("Assets/Plugins/Android/libs/arm64-v8a/libSkiaSharp.so"))
                issues.AppendLine("✗ libSkiaSharp.so not found — run tools/fetch_packages.py");
            else
                issues.AppendLine("✓ libSkiaSharp.so (arm64) present");

            // Check scene
            if (!File.Exists(SCENE_PATH))
                issues.AppendLine("✗ Bootstrap.unity not found — run Tools → SLQuest → Create Bootstrap Scene");
            else
                issues.AppendLine("✓ Bootstrap.unity present");

            // Check Android settings
            if (PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android) != ScriptingImplementation.IL2CPP)
                issues.AppendLine("✗ Scripting backend is not IL2CPP for Android");
            else
                issues.AppendLine("✓ IL2CPP scripting backend");

            if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                issues.AppendLine("✗ Target architecture is not ARM64 — Quest 3 requires ARM64");
            else
                issues.AppendLine("✓ ARM64 target architecture");

            Debug.Log(issues.ToString());
            EditorUtility.DisplayDialog("SLQuest Validation", issues.ToString(), "OK");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static GameObject AddChild(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static void SetPrivateField(object obj, string field, object value)
        {
            var f = obj.GetType().GetField(field,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            f?.SetValue(obj, value);
        }

        private static void EnsureURPPipeline()
        {
            // Find or create URP pipeline asset and set it as the active renderer
            var pipeline = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset");
            if (pipeline.Length > 0)
            {
                var path  = AssetDatabase.GUIDToAssetPath(pipeline[0]);
                var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (asset != null)
                {
                    UnityEngine.QualitySettings.renderPipeline = asset;
                    Debug.Log($"[SLSetup] URP asset set: {path}");
                }
            }
            else
            {
                Debug.LogWarning("[SLSetup] No URP pipeline asset found — create one via Assets → Create → Rendering → URP Asset");
            }
        }
    }
}
#endif
