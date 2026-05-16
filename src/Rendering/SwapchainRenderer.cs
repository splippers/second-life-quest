using Silk.NET.OpenXR;
using Silk.NET.Vulkan;
using SLQuest.XR;
using Format = Silk.NET.Vulkan.Format;
using ImageLayout = Silk.NET.Vulkan.ImageLayout;

namespace SLQuest.Rendering
{
    public readonly record struct RenderContext(
        int    EyeIndex,           // 0=left, 1=right
        Matrix4x4  ViewMatrix,
        Matrix4x4  ProjectionMatrix,
        uint   Width,
        uint   Height,
        CommandBuffer Cmd
    );

    /// <summary>
    /// Allocates one OpenXR swapchain per eye, creates render passes and framebuffers
    /// against the swapchain images, and drives the per-eye render loop.
    /// </summary>
    public sealed unsafe class SwapchainRenderer : IDisposable
    {
        private readonly XRSession     _xr;
        private readonly VulkanContext _vk;

        private readonly SwapchainData[] _eyes = new SwapchainData[2];
        public CompositionLayerProjection[] ProjectionLayers { get; private set; } = [];

        public  RenderPass RenderPass { get; private set; }
        private bool _disposed;

        public SwapchainRenderer(XRSession xr, VulkanContext vk)
        {
            _xr = xr;
            _vk = vk;
        }

        public async Task InitAsync()
        {
            await Task.Run(() =>
            {
                CreateRenderPass();
                CreateSwapchains();
            });
        }

        private void CreateRenderPass()
        {
            var colorAttach = new AttachmentDescription
            {
                Format         = VulkanContext.ColorFormat,
                Samples        = SampleCountFlags.Count1Bit,
                LoadOp         = AttachmentLoadOp.Clear,
                StoreOp        = AttachmentStoreOp.Store,
                InitialLayout  = ImageLayout.Undefined,
                FinalLayout    = ImageLayout.ColorAttachmentOptimal,
            };
            var depthAttach = new AttachmentDescription
            {
                Format         = VulkanContext.DepthFormat,
                Samples        = SampleCountFlags.Count1Bit,
                LoadOp         = AttachmentLoadOp.Clear,
                StoreOp        = AttachmentStoreOp.DontCare,
                InitialLayout  = ImageLayout.Undefined,
                FinalLayout    = ImageLayout.DepthStencilAttachmentOptimal,
            };

            var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
            var depthRef = new AttachmentReference { Attachment = 1, Layout = ImageLayout.DepthStencilAttachmentOptimal };

            var subpass = new SubpassDescription
            {
                PipelineBindPoint       = PipelineBindPoint.Graphics,
                ColorAttachmentCount    = 1,
                PColorAttachments       = &colorRef,
                PDepthStencilAttachment = &depthRef,
            };

            var attachments = stackalloc AttachmentDescription[] { colorAttach, depthAttach };
            var rpci = new RenderPassCreateInfo
            {
                SType           = StructureType.RenderPassCreateInfo,
                AttachmentCount = 2,
                PAttachments    = attachments,
                SubpassCount    = 1,
                PSubpasses      = &subpass,
            };

            RenderPass rp;
            _vk.Vk.CreateRenderPass(_vk.Device, &rpci, null, &rp);
            RenderPass = rp;
        }

        private void CreateSwapchains()
        {
            // Get view configuration views to know the recommended eye resolution
            uint viewCount = 2;
            var vcViews = new ViewConfigurationView[2];
            vcViews[0].Type = vcViews[1].Type = StructureType.ViewConfigurationView;
            fixed (ViewConfigurationView* vp = vcViews)
                _xr.XrApi.EnumerateViewConfigurationViews(
                    _xr.XrInstance, _xr.SystemId,
                    ViewConfigurationType.PrimaryStereo,
                    viewCount, &viewCount, vp);

            for (int eye = 0; eye < 2; eye++)
            {
                var vcv = vcViews[eye];
                uint w = vcv.RecommendedImageRectWidth;
                uint h = vcv.RecommendedImageRectHeight;

                // Create swapchain
                var sci = new SwapchainCreateInfo
                {
                    Type         = StructureType.SwapchainCreateInfo,
                    UsageFlags   = SwapchainUsageFlags.ColorAttachmentBit | SwapchainUsageFlags.SampledBit,
                    Format       = (long)VulkanContext.ColorFormat,
                    SampleCount  = 1,
                    Width        = w,
                    Height       = h,
                    FaceCount    = 1,
                    ArraySize    = 1,
                    MipCount     = 1,
                };
                Silk.NET.OpenXR.Swapchain sc;
                _xr.XrApi.CreateSwapchain(_xr.XrSessionHandle, &sci, &sc);

                // Enumerate swapchain images
                uint imgCount = 0;
                _xr.XrApi.EnumerateSwapchainImages(sc, 0, &imgCount, null);
                var xrImages = new SwapchainImageVulkan2KHR[imgCount];
                for (int i = 0; i < imgCount; i++)
                    xrImages[i].Type = StructureType.SwapchainImageVulkanKhr;

                fixed (SwapchainImageVulkan2KHR* ip = xrImages)
                    _xr.XrApi.EnumerateSwapchainImages(
                        sc, imgCount, &imgCount,
                        (SwapchainImageBaseHeader*)ip);

                // Build Vulkan image views + depth buffers + framebuffers
                var framebufs = new Framebuffer[imgCount];
                var views     = new ImageView[imgCount];
                var (depthImage, depthMem, depthView) = CreateDepthBuffer(w, h);

                for (int i = 0; i < imgCount; i++)
                {
                    var img = new Silk.NET.Vulkan.Image(xrImages[i].Image);
                    views[i] = CreateImageView(img, VulkanContext.ColorFormat, ImageAspectFlags.ColorBit);
                    framebufs[i] = CreateFramebuffer(views[i], depthView, w, h);
                }

                // Per-eye command buffers
                var cbs = new CommandBuffer[imgCount];
                var allocInfo = new CommandBufferAllocateInfo
                {
                    SType              = StructureType.CommandBufferAllocateInfo,
                    CommandPool        = _vk.CommandPool,
                    Level              = CommandBufferLevel.Primary,
                    CommandBufferCount = imgCount,
                };
                fixed (CommandBuffer* cp = cbs)
                    _vk.Vk.AllocateCommandBuffers(_vk.Device, &allocInfo, cp);

                _eyes[eye] = new SwapchainData(sc, w, h, xrImages, views, depthImage, depthMem, depthView, framebufs, cbs);
            }
        }

        public void RenderFrame(Span<View> views, long predictedTime, Action<RenderContext> renderScene)
        {
            var projViews = new CompositionLayerProjectionView[2];
            for (int i = 0; i < 2; i++)
                projViews[i].Type = StructureType.CompositionLayerProjectionView;

            for (int eye = 0; eye < 2; eye++)
            {
                var sd = _eyes[eye];

                // Acquire swapchain image
                uint imgIdx = 0;
                var acquireInfo = new SwapchainImageAcquireInfo { Type = StructureType.SwapchainImageAcquireInfo };
                _xr.XrApi.AcquireSwapchainImage(sd.Swapchain, &acquireInfo, &imgIdx);

                var waitInfo = new SwapchainImageWaitInfo
                {
                    Type    = StructureType.SwapchainImageWaitInfo,
                    Timeout = long.MaxValue,
                };
                _xr.XrApi.WaitSwapchainImage(sd.Swapchain, &waitInfo);

                // Record command buffer
                var cb = sd.CommandBuffers[imgIdx];
                _vk.Vk.ResetCommandBuffer(cb, 0);

                var cbbi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
                _vk.Vk.BeginCommandBuffer(cb, &cbbi);

                var clearValues = stackalloc ClearValue[]
                {
                    new() { Color = new() { Float32_0 = 0f, Float32_1 = 0.05f, Float32_2 = 0.1f, Float32_3 = 1f } },
                    new() { DepthStencil = new() { Depth = 1f } },
                };
                var rpbi = new RenderPassBeginInfo
                {
                    SType           = StructureType.RenderPassBeginInfo,
                    RenderPass      = RenderPass,
                    Framebuffer     = sd.Framebuffers[imgIdx],
                    RenderArea      = new Rect2D { Extent = new Extent2D(sd.Width, sd.Height) },
                    ClearValueCount = 2,
                    PClearValues    = clearValues,
                };
                _vk.Vk.CmdBeginRenderPass(cb, &rpbi, SubpassContents.Inline);

                // Compute view + projection matrices
                var v = views[eye];
                var viewMat = PoseToViewMatrix(v.Pose);
                var projMat = FovToProjectionMatrix(v.Fov, 0.05f, 2000f);

                renderScene(new RenderContext(eye, viewMat, projMat, sd.Width, sd.Height, cb));

                _vk.Vk.CmdEndRenderPass(cb);
                _vk.Vk.EndCommandBuffer(cb);

                // Submit
                var si = new SubmitInfo
                {
                    SType              = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers    = &cb,
                };
                _vk.Vk.QueueSubmit(_vk.GraphicsQueue, 1, &si, default);
                _vk.Vk.QueueWaitIdle(_vk.GraphicsQueue);

                var releaseInfo = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
                _xr.XrApi.ReleaseSwapchainImage(sd.Swapchain, &releaseInfo);

                projViews[eye] = new CompositionLayerProjectionView
                {
                    Type     = StructureType.CompositionLayerProjectionView,
                    Pose     = v.Pose,
                    Fov      = v.Fov,
                    SubImage = new SwapchainSubImage
                    {
                        Swapchain   = sd.Swapchain,
                        ImageRect   = new Rect2Di { Extent = new Extent2Di((int)sd.Width, (int)sd.Height) },
                        ImageArrayIndex = 0,
                    },
                };
            }

            fixed (CompositionLayerProjectionView* pvp = projViews)
            {
                ProjectionLayers = new[]
                {
                    new CompositionLayerProjection
                    {
                        Type      = StructureType.CompositionLayerProjection,
                        Space     = _xr.LocalSpace,
                        ViewCount = 2,
                        Views     = pvp,
                    }
                };
            }
        }

        // ── Matrix helpers ────────────────────────────────────────────────────

        private static Matrix4x4 PoseToViewMatrix(Posef pose)
        {
            var pos = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
            var rot = new Quaternion(pose.Orientation.X, pose.Orientation.Y,
                                     pose.Orientation.Z, pose.Orientation.W);
            // View = inverse of camera transform
            Matrix4x4.Invert(MathEx.TRS(pos, rot, Vector3.One), out var view);
            return view;
        }

        private static Matrix4x4 FovToProjectionMatrix(Fovf fov, float near, float far)
        {
            float l = MathF.Tan(fov.AngleLeft);
            float r = MathF.Tan(fov.AngleRight);
            float d = MathF.Tan(fov.AngleDown);
            float u = MathF.Tan(fov.AngleUp);
            float w = r - l;
            float h = d - u;

            return new Matrix4x4(
                2f / w,             0,                  0,                      0,
                0,                  2f / h,             0,                      0,
                (r + l) / w,        (u + d) / h,        -(far + near) / (far - near), -1,
                0,                  0,                  -(2 * far * near) / (far - near), 0);
        }

        // ── Vulkan resource helpers ───────────────────────────────────────────

        private ImageView CreateImageView(Silk.NET.Vulkan.Image image, Format format, ImageAspectFlags aspect)
        {
            var ivci = new ImageViewCreateInfo
            {
                SType    = StructureType.ImageViewCreateInfo,
                Image    = image,
                ViewType = ImageViewType.Type2D,
                Format   = format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask     = aspect,
                    BaseMipLevel   = 0,
                    LevelCount     = 1,
                    BaseArrayLayer = 0,
                    LayerCount     = 1,
                },
            };
            ImageView view;
            _vk.Vk.CreateImageView(_vk.Device, &ivci, null, &view);
            return view;
        }

        private (Silk.NET.Vulkan.Image, DeviceMemory, ImageView) CreateDepthBuffer(uint w, uint h)
        {
            var ici = new ImageCreateInfo
            {
                SType         = StructureType.ImageCreateInfo,
                ImageType     = ImageType.Type2D,
                Format        = VulkanContext.DepthFormat,
                Extent        = new Extent3D(w, h, 1),
                MipLevels     = 1,
                ArrayLayers   = 1,
                Samples       = SampleCountFlags.Count1Bit,
                Tiling        = ImageTiling.Optimal,
                Usage         = ImageUsageFlags.DepthStencilAttachmentBit,
            };
            Silk.NET.Vulkan.Image img;
            _vk.Vk.CreateImage(_vk.Device, &ici, null, &img);

            MemoryRequirements req;
            _vk.Vk.GetImageMemoryRequirements(_vk.Device, img, &req);

            var mai = new MemoryAllocateInfo
            {
                SType           = StructureType.MemoryAllocateInfo,
                AllocationSize  = req.Size,
                MemoryTypeIndex = _vk.FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            DeviceMemory mem;
            _vk.Vk.AllocateMemory(_vk.Device, &mai, null, &mem);
            _vk.Vk.BindImageMemory(_vk.Device, img, mem, 0);

            var view = CreateImageView(img, VulkanContext.DepthFormat, ImageAspectFlags.DepthBit);
            return (img, mem, view);
        }

        private Framebuffer CreateFramebuffer(ImageView color, ImageView depth, uint w, uint h)
        {
            var attachments = stackalloc ImageView[] { color, depth };
            var fbci = new FramebufferCreateInfo
            {
                SType           = StructureType.FramebufferCreateInfo,
                RenderPass      = RenderPass,
                AttachmentCount = 2,
                PAttachments    = attachments,
                Width           = w,
                Height          = h,
                Layers          = 1,
            };
            Framebuffer fb;
            _vk.Vk.CreateFramebuffer(_vk.Device, &fbci, null, &fb);
            return fb;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _vk.Vk.DeviceWaitIdle(_vk.Device);
            _vk.Vk.DestroyRenderPass(_vk.Device, RenderPass, null);
            foreach (var sd in _eyes) sd.Dispose(_xr.XrApi, _vk);
        }

        // ── Inner type ────────────────────────────────────────────────────────

        private readonly record struct SwapchainData(
            Silk.NET.OpenXR.Swapchain Swapchain,
            uint Width, uint Height,
            SwapchainImageVulkan2KHR[] Images,
            ImageView[] Views,
            Silk.NET.Vulkan.Image DepthImage,
            DeviceMemory DepthMemory,
            ImageView DepthView,
            Framebuffer[] Framebuffers,
            CommandBuffer[] CommandBuffers)
        {
            public void Dispose(OpenXR xr, VulkanContext vk)
            {
                foreach (var v in Views)     vk.Vk.DestroyImageView(vk.Device, v, null);
                foreach (var f in Framebuffers) vk.Vk.DestroyFramebuffer(vk.Device, f, null);
                vk.Vk.DestroyImageView(vk.Device, DepthView, null);
                vk.Vk.DestroyImage(vk.Device, DepthImage, null);
                vk.Vk.FreeMemory(vk.Device, DepthMemory, null);
                if (Swapchain.Handle != 0) xr.DestroySwapchain(Swapchain);
            }
        }
    }
}
