using System.Runtime.InteropServices;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.FB;
using Silk.NET.OpenXR.Extensions.META;
using SLQuest.Core;
using SLQuest.Rendering;

namespace SLQuest.XR
{
    public readonly record struct XRView(Posef Pose, Fovf Fov, ulong Swapchain, int Width, int Height);

    public sealed unsafe class XRSession : IDisposable
    {
        // ── Handle state ──────────────────────────────────────────────────────
        public OpenXR XrApi           { get; private set; } = null!;
        public Instance XrInstance    { get; private set; }
        public Session  XrSessionHandle { get; private set; }
        public SystemId SystemId      { get; private set; }
        public Space    LocalSpace    { get; private set; }
        public Space    ViewSpace     { get; private set; }

        public bool IsRunning     { get; private set; }
        public bool SessionActive { get; private set; }
        public float DeltaTime    { get; private set; }

        // Extensions
        public FBPassthrough? Passthrough { get; private set; }
        public METAPassthroughColorLut? PassthroughLut { get; private set; }

        private SessionState _state = SessionState.Unknown;
        private long _lastTime;
        private bool _disposed;

        // Injected from Android entry point
        private nint _javaVm;
        private nint _activity;
        private VulkanContext _vulkan = null!;

        public XRSession(nint javaVm, nint activity)
        {
            _javaVm   = javaVm;
            _activity = activity;
        }

        // ── Init ──────────────────────────────────────────────────────────────

        public async Task InitAsync()
        {
            XrApi = OpenXR.GetApi();
            await Task.Run(CreateInstance);
            GetSystem();
            // Vulkan context created here so it can use XR graphics requirements
        }

        public void BindVulkan(VulkanContext vulkan)
        {
            _vulkan = vulkan;
            CreateSession();
            CreateSpaces();
            IsRunning = true;
        }

        private void CreateInstance()
        {
            var extensions = new[]
            {
                "XR_KHR_vulkan_enable2",
                "XR_KHR_android_create_instance",
                "XR_FB_passthrough",
                "XR_META_passthrough_color_lut",
                "XR_FB_hand_tracking_mesh",
                "XR_EXT_hand_tracking",
                "XR_FB_eye_tracking_social",
                "XR_META_foveation_eye_tracked",
            };

            var extPtrs = extensions.Select(e =>
            {
                fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(e + "\0"))
                    return (byte*)Marshal.AllocHGlobal(e.Length + 1);
            }).ToArray();

            // Android create instance next chain
            var androidInfo = new InstanceCreateInfoAndroidKHR
            {
                Type             = StructureType.InstanceCreateInfoAndroidKhr,
                ApplicationVM    = (void*)_javaVm,
                ApplicationActivity = (void*)_activity,
            };

            fixed (byte** extPtr = extPtrs)
            fixed (byte* appName = "SLQuest\0"u8)
            fixed (byte* engName = "SLQuestNative\0"u8)
            {
                var appInfo = new ApplicationInfo
                {
                    ApiVersion    = new Version64(1, 0, 0),
                    ApplicationVersion = 1,
                    EngineVersion = 1,
                };
                Buffer.MemoryCopy(appName, appInfo.ApplicationName, 128, 8);
                Buffer.MemoryCopy(engName, appInfo.EngineName,      128, 14);

                var createInfo = new InstanceCreateInfo
                {
                    Type                    = StructureType.InstanceCreateInfo,
                    Next                    = &androidInfo,
                    CreateFlags             = 0,
                    ApplicationInfo         = appInfo,
                    EnabledExtensionCount   = (uint)extensions.Length,
                    EnabledExtensionNames   = extPtr,
                    EnabledApiLayerCount    = 0,
                    EnabledApiLayerNames    = null,
                };

                Instance inst;
                Check(XrApi.CreateInstance(&createInfo, &inst), "xrCreateInstance");
                XrInstance = inst;
            }

            // Try loading extensions
            if (XrApi.TryGetInstanceExtension<FBPassthrough>(null, XrInstance, out var pt))
                Passthrough = pt;
        }

        private void GetSystem()
        {
            var getInfo = new SystemGetInfo
            {
                Type     = StructureType.SystemGetInfo,
                FormFactor = FormFactor.HeadMountedDisplay,
            };
            SystemId sysId;
            Check(XrApi.GetSystem(XrInstance, &getInfo, &sysId), "xrGetSystem");
            SystemId = sysId;
        }

        public void GetVulkanRequirements(out ulong minVersion, out ulong maxVersion)
        {
            var req = new GraphicsRequirementsVulkan2KHR
            {
                Type = StructureType.GraphicsRequirementsVulkanKhr,
            };
            // xrGetVulkanGraphicsRequirements2KHR
            // Silk.NET wraps this via extension; fall back to hardcoded Quest 3 minimum
            minVersion = Silk.NET.Vulkan.Vk.MakeVersion(1, 1, 0);
            maxVersion = Silk.NET.Vulkan.Vk.MakeVersion(1, 3, 0);
        }

        private void CreateSession()
        {
            var vkBinding = new GraphicsBindingVulkan2KHR
            {
                Type           = StructureType.GraphicsBindingVulkanKhr,
                Instance       = _vulkan.VkInstance,
                PhysicalDevice = _vulkan.PhysicalDevice,
                Device         = _vulkan.Device,
                QueueFamilyIndex = _vulkan.GraphicsQueueFamily,
                QueueIndex     = 0,
            };

            var sessionInfo = new SessionCreateInfo
            {
                Type     = StructureType.SessionCreateInfo,
                Next     = &vkBinding,
                SystemId = SystemId,
            };
            Session sess;
            Check(XrApi.CreateSession(XrInstance, &sessionInfo, &sess), "xrCreateSession");
            XrSessionHandle = sess;
        }

        private void CreateSpaces()
        {
            var localRef = new ReferenceSpaceCreateInfo
            {
                Type                 = StructureType.ReferenceSpaceCreateInfo,
                ReferenceSpaceType   = ReferenceSpaceType.Local,
                PoseInReferenceSpace = Pose(),
            };
            Space localSp;
            Check(XrApi.CreateReferenceSpace(XrSessionHandle, &localRef, &localSp), "local space");
            LocalSpace = localSp;

            var viewRef = new ReferenceSpaceCreateInfo
            {
                Type                 = StructureType.ReferenceSpaceCreateInfo,
                ReferenceSpaceType   = ReferenceSpaceType.View,
                PoseInReferenceSpace = Pose(),
            };
            Space viewSp;
            Check(XrApi.CreateReferenceSpace(XrSessionHandle, &viewRef, &viewSp), "view space");
            ViewSpace = viewSp;
        }

        // ── Frame loop ────────────────────────────────────────────────────────

        public void PollEvents()
        {
            Span<byte> buf = stackalloc byte[4096];
            fixed (byte* p = buf)
            {
                var ev = (EventDataBuffer*)p;
                ev->Type = StructureType.EventDataBuffer;
                ev->Next = null;
                while (XrApi.PollEvent(XrInstance, ev) == Result.Success)
                {
                    HandleEvent(ev);
                    ev->Type = StructureType.EventDataBuffer;
                }
            }
        }

        private void HandleEvent(EventDataBuffer* ev)
        {
            switch (ev->Type)
            {
                case StructureType.EventDataSessionStateChanged:
                    var stateEv = (EventDataSessionStateChanged*)ev;
                    _state = stateEv->State;
                    HandleStateChange(_state);
                    break;
                case StructureType.EventDataInstanceLossPending:
                    IsRunning = false;
                    break;
            }
        }

        private void HandleStateChange(SessionState state)
        {
            switch (state)
            {
                case SessionState.Ready:
                    var beginInfo = new SessionBeginInfo
                    {
                        Type                         = StructureType.SessionBeginInfo,
                        PrimaryViewConfigurationType = ViewConfigurationType.PrimaryStereo,
                    };
                    Check(XrApi.BeginSession(XrSessionHandle, &beginInfo), "xrBeginSession");
                    SessionActive = true;
                    break;
                case SessionState.Stopping:
                    Check(XrApi.EndSession(XrSessionHandle), "xrEndSession");
                    SessionActive = false;
                    break;
                case SessionState.Exiting:
                case SessionState.LossPending:
                    IsRunning = false;
                    SessionActive = false;
                    break;
            }
        }

        public (bool ok, FrameState state) WaitFrame()
        {
            var fs = new FrameState { Type = StructureType.FrameState };
            var wi = new FrameWaitInfo { Type = StructureType.FrameWaitInfo };
            var result = XrApi.WaitFrame(XrSessionHandle, &wi, &fs);
            if (result != Result.Success) return (false, default);

            long now = fs.PredictedDisplayTime;
            DeltaTime = _lastTime == 0 ? 0f : (now - _lastTime) / 1_000_000_000f;
            _lastTime = now;
            return (true, fs);
        }

        public void BeginFrame()
        {
            var bi = new FrameBeginInfo { Type = StructureType.FrameBeginInfo };
            XrApi.BeginFrame(XrSessionHandle, &bi);
        }

        public void EndFrame(in FrameState fs, Span<CompositionLayerProjection> layers)
        {
            fixed (CompositionLayerProjection* lp = layers)
            {
                var ptrs = stackalloc CompositionLayerBaseHeader*[layers.Length];
                for (int i = 0; i < layers.Length; i++)
                    ptrs[i] = (CompositionLayerBaseHeader*)&lp[i];

                var fi = new FrameEndInfo
                {
                    Type                   = StructureType.FrameEndInfo,
                    DisplayTime            = fs.PredictedDisplayTime,
                    EnvironmentBlendMode   = EnvironmentBlendMode.Opaque,
                    LayerCount             = (uint)layers.Length,
                    Layers                 = ptrs,
                };
                XrApi.EndFrame(XrSessionHandle, &fi);
            }
        }

        public Span<View> LocateViews()
        {
            var locInfo = new ViewLocateInfo
            {
                Type              = StructureType.ViewLocateInfo,
                ViewConfigurationType = ViewConfigurationType.PrimaryStereo,
                DisplayTime       = _lastTime,
                Space             = LocalSpace,
            };
            uint count = 2;
            var views = new View[2];
            views[0].Type = views[1].Type = StructureType.View;
            ViewState vs = new() { Type = StructureType.ViewState };
            fixed (View* vp = views)
                XrApi.LocateViews(XrSessionHandle, &locInfo, &vs, count, &count, vp);
            return views;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static Posef Pose() => new()
        {
            Orientation = new Quaternionf { X = 0, Y = 0, Z = 0, W = 1 },
            Position    = new Vector3f    { X = 0, Y = 0, Z = 0 },
        };

        private static void Check(Result r, string op)
        {
            if (r != Result.Success)
                throw new InvalidOperationException($"OpenXR {op} failed: {r}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (LocalSpace.Handle != 0) XrApi.DestroySpace(LocalSpace);
            if (ViewSpace.Handle  != 0) XrApi.DestroySpace(ViewSpace);
            if (XrSessionHandle.Handle != 0) XrApi.DestroySession(XrSessionHandle);
            if (XrInstance.Handle != 0)  XrApi.DestroyInstance(XrInstance);
            XrApi.Dispose();
        }
    }
}
