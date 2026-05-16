using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using SLQuest.XR;

namespace SLQuest.Rendering
{
    /// <summary>
    /// Owns the Vulkan instance, physical device, logical device, and graphics queue.
    /// Created after OpenXR so we can satisfy xrGetVulkanGraphicsDeviceKHR requirements.
    /// </summary>
    public sealed unsafe class VulkanContext : IDisposable
    {
        public Vk Vk { get; private set; } = null!;
        public Silk.NET.Vulkan.Instance VkInstance    { get; private set; }
        public PhysicalDevice           PhysicalDevice { get; private set; }
        public Device                   Device         { get; private set; }
        public Queue                    GraphicsQueue  { get; private set; }
        public uint                     GraphicsQueueFamily { get; private set; }
        public CommandPool              CommandPool    { get; private set; }

        private bool _disposed;

        // Formats chosen for Quest 3
        public const Format    ColorFormat   = Format.R8G8B8A8Srgb;
        public const Format    DepthFormat   = Format.D24UnormS8Uint;
        public const SampleCountFlags Samples = SampleCountFlags.Count4Bit;

        public void Init(XRSession xr)
        {
            Vk = Vk.GetApi();
            CreateInstance(xr);
            PickPhysicalDevice(xr);
            CreateLogicalDevice();
            CreateCommandPool();
        }

        private void CreateInstance(XRSession xr)
        {
            var exts = new[]
            {
                "VK_KHR_get_physical_device_properties2",
                "VK_KHR_external_memory_capabilities",
            };

            var extPtrs = exts.Select(e =>
                Marshal.StringToHGlobalAnsi(e)).ToArray();

            fixed (nint* pp = extPtrs)
            fixed (byte* appName = "SLQuest\0"u8)
            {
                var appInfo = new ApplicationInfo
                {
                    SType          = StructureType.ApplicationInfo,
                    ApiVersion     = Vk.Version12,
                };
                Buffer.MemoryCopy(appName, appInfo.PApplicationName, 8, 8);

                var createInfo = new InstanceCreateInfo
                {
                    SType                   = StructureType.InstanceCreateInfo,
                    PApplicationInfo        = &appInfo,
                    EnabledExtensionCount   = (uint)exts.Length,
                    PpEnabledExtensionNames = (byte**)pp,
                };

                Silk.NET.Vulkan.Instance inst;
                Check(Vk.CreateInstance(&createInfo, null, &inst), "vkCreateInstance");
                VkInstance = inst;
            }

            foreach (var p in extPtrs) Marshal.FreeHGlobal(p);
        }

        private void PickPhysicalDevice(XRSession xr)
        {
            // OpenXR tells us which physical device to use
            // (xrGetVulkanGraphicsDeviceKHR — we call via Silk.NET extension)
            // For now enumerate and pick the first discrete GPU (Quest 3 has one)
            uint count = 0;
            Vk.EnumeratePhysicalDevices(VkInstance, &count, null);
            var devices = new PhysicalDevice[count];
            fixed (PhysicalDevice* dp = devices)
                Vk.EnumeratePhysicalDevices(VkInstance, &count, dp);

            PhysicalDevice = devices[0]; // Quest 3 has one GPU
        }

        private void CreateLogicalDevice()
        {
            // Find graphics queue family
            uint qCount = 0;
            Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &qCount, null);
            var qProps = new QueueFamilyProperties[qCount];
            fixed (QueueFamilyProperties* qp = qProps)
                Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &qCount, qp);

            GraphicsQueueFamily = 0;
            for (uint i = 0; i < qCount; i++)
            {
                if ((qProps[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                {
                    GraphicsQueueFamily = i;
                    break;
                }
            }

            float priority = 1f;
            var qci = new DeviceQueueCreateInfo
            {
                SType            = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = GraphicsQueueFamily,
                QueueCount       = 1,
                PQueuePriorities = &priority,
            };

            var devExts = new[]
            {
                "VK_KHR_swapchain",
                "VK_KHR_external_memory",
                "VK_KHR_external_memory_fd",
            };
            var extPtrs = devExts.Select(e => Marshal.StringToHGlobalAnsi(e)).ToArray();

            fixed (nint* pp = extPtrs)
            {
                var features = new PhysicalDeviceFeatures { SamplerAnisotropy = true };
                var dci = new DeviceCreateInfo
                {
                    SType                    = StructureType.DeviceCreateInfo,
                    QueueCreateInfoCount     = 1,
                    PQueueCreateInfos        = &qci,
                    EnabledExtensionCount    = (uint)devExts.Length,
                    PpEnabledExtensionNames  = (byte**)pp,
                    PEnabledFeatures         = &features,
                };

                Device dev;
                Check(Vk.CreateDevice(PhysicalDevice, &dci, null, &dev), "vkCreateDevice");
                Device = dev;
            }

            foreach (var p in extPtrs) Marshal.FreeHGlobal(p);

            Queue q;
            Vk.GetDeviceQueue(Device, GraphicsQueueFamily, 0, &q);
            GraphicsQueue = q;
        }

        private void CreateCommandPool()
        {
            var cpci = new CommandPoolCreateInfo
            {
                SType            = StructureType.CommandPoolCreateInfo,
                Flags            = CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = GraphicsQueueFamily,
            };
            CommandPool pool;
            Check(Vk.CreateCommandPool(Device, &cpci, null, &pool), "vkCreateCommandPool");
            CommandPool = pool;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        public uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
        {
            PhysicalDeviceMemoryProperties props;
            Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, &props);
            for (uint i = 0; i < props.MemoryTypeCount; i++)
                if ((typeBits & (1u << (int)i)) != 0 &&
                    (props.MemoryTypes[(int)i].PropertyFlags & required) == required)
                    return i;
            throw new InvalidOperationException("No suitable memory type");
        }

        public CommandBuffer BeginOneShot()
        {
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType              = StructureType.CommandBufferAllocateInfo,
                Level              = CommandBufferLevel.Primary,
                CommandPool        = CommandPool,
                CommandBufferCount = 1,
            };
            CommandBuffer cb;
            Vk.AllocateCommandBuffers(Device, &allocInfo, &cb);
            var bi = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Vk.BeginCommandBuffer(cb, &bi);
            return cb;
        }

        public void EndOneShot(CommandBuffer cb)
        {
            Vk.EndCommandBuffer(cb);
            var si = new SubmitInfo
            {
                SType              = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers    = &cb,
            };
            Vk.QueueSubmit(GraphicsQueue, 1, &si, default);
            Vk.QueueWaitIdle(GraphicsQueue);
            Vk.FreeCommandBuffers(Device, CommandPool, 1, &cb);
        }

        private static void Check(Result r, string op)
        {
            if (r != Result.Success)
                throw new InvalidOperationException($"Vulkan {op} failed: {r}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Vk.DeviceWaitIdle(Device);
            if (CommandPool.Handle != 0)  Vk.DestroyCommandPool(Device, CommandPool, null);
            if (Device.Handle      != 0)  Vk.DestroyDevice(Device, null);
            if (VkInstance.Handle  != 0)  Vk.DestroyInstance(VkInstance, null);
            Vk.Dispose();
        }
    }
}
