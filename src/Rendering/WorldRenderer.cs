using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using SLQuest.Core;
using SLQuest.World;
using SLQuest.Avatar;
using Format = Silk.NET.Vulkan.Format;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace SLQuest.Rendering
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct WorldVertex
    {
        public Vector3 Position; // 12
        public Vector3 Normal;   // 12
        public Vector2 UV;       // 8
        // total 32 bytes
    }

    // Mirrors the push_constant block in world.vert / world.frag.
    // Total: 3×64 + 16 + 8×4 = 240 bytes.  Quest 3 / Adreno 740 supports 256.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal unsafe struct WorldPush
    {
        public Matrix4x4 Model;
        public Matrix4x4 View;
        public Matrix4x4 Proj;
        public Vector4   Tint;
        public float     Glow;
        public float     RepeatU, RepeatV;
        public float     OffsetU, OffsetV;
        public float     Rotation;
        public float     _pad0, _pad1;
    }

    public sealed unsafe class WorldRenderer : IDisposable
    {
        private readonly VulkanContext  _vk;
        private readonly ObjectManager  _objects;
        private readonly TerrainManager _terrain;
        private readonly AvatarManager  _avatars;
        private SwapchainRenderer       _swapchain = null!;

        // Pipeline
        private Pipeline            _pipeline;
        private PipelineLayout      _pipelineLayout;
        private DescriptorSetLayout _descLayout;
        private DescriptorPool      _descPool;
        private DescriptorSet       _whiteDescSet;
        private Sampler             _sampler;

        // White 1×1 texture used when a prim has no texture assigned
        private Silk.NET.Vulkan.Image _whiteTex;
        private DeviceMemory          _whiteTexMem;
        private ImageView             _whiteTexView;

        // Terrain GPU buffers
        private Buffer      _terrainVb,  _terrainIb;
        private DeviceMemory _terrainVbMem, _terrainIbMem;
        private int          _terrainIndexCount;
        private bool         _terrainDirty = true;

        // Cube mesh (used for all prims)
        private Buffer       _cubeVb,  _cubeIb;
        private DeviceMemory _cubeVbMem, _cubeIbMem;
        private int          _cubeIndexCount;

        private bool _disposed;

        // Terrain colours: simple altitude bands
        private static readonly Vector4 ColWater = new(0.10f, 0.25f, 0.55f, 1f);
        private static readonly Vector4 ColGrass = new(0.22f, 0.55f, 0.18f, 1f);
        private static readonly Vector4 ColRock  = new(0.55f, 0.50f, 0.40f, 1f);
        private static readonly Vector4 ColSnow  = new(0.90f, 0.92f, 0.95f, 1f);
        private static readonly Vector4 ColPrim  = new(0.80f, 0.80f, 0.85f, 1f);
        private static readonly Vector4 ColAv    = new(0.40f, 0.65f, 1.00f, 1f);

        public WorldRenderer(VulkanContext vk, ObjectManager objects,
                             TerrainManager terrain, AvatarManager avatars)
        {
            _vk = vk; _objects = objects; _terrain = terrain; _avatars = avatars;
        }

        public void BindSwapchain(SwapchainRenderer sc) => _swapchain = sc;

        public async Task InitAsync()
        {
            await Task.Run(() =>
            {
                CreateSampler();
                CreateWhiteTexture();
                CreateDescriptorSetLayout();
                CreateDescriptorPool();
                AllocWriteWhiteDesc();
                LoadPipeline();
                BuildCubeMesh();
                // Terrain built lazily on first TerrainPatchEvent
            });

            EventBus.Subscribe<TerrainPatchEvent>(_ => _terrainDirty = true);
        }

        public void Render(in RenderContext ctx)
        {
            if (_terrainDirty)
            {
                RebuildTerrain();
                _terrainDirty = false;
            }

            var cmd = ctx.Cmd;

            _vk.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);

            var vp = new Viewport(0, 0, ctx.Width, ctx.Height, 0, 1);
            var sc = new Rect2D(default, new Extent2D(ctx.Width, ctx.Height));
            _vk.Vk.CmdSetViewport(cmd, 0, 1, &vp);
            _vk.Vk.CmdSetScissor(cmd,  0, 1, &sc);

            var ds = _whiteDescSet;
            _vk.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics,
                _pipelineLayout, 0, 1, &ds, 0, null);

            DrawTerrain(cmd, ctx);
            DrawObjects(cmd, ctx);
        }

        // ── Terrain ───────────────────────────────────────────────────────────

        private void DrawTerrain(CommandBuffer cmd, in RenderContext ctx)
        {
            if (_terrainIndexCount == 0) return;

            ulong zero = 0;
            _vk.Vk.CmdBindVertexBuffers(cmd, 0, 1, &_terrainVb, &zero);
            _vk.Vk.CmdBindIndexBuffer(cmd, _terrainIb, 0, IndexType.Uint32);

            // One draw call for the entire 256×256 terrain
            var push = new WorldPush
            {
                Model   = Matrix4x4.Identity,
                View    = ctx.ViewMatrix,
                Proj    = ctx.ProjectionMatrix,
                Tint    = Vector4.One, // vertex colour carries the tint
                Glow    = 0,
                RepeatU = 1, RepeatV = 1,
            };
            _vk.Vk.CmdPushConstants(cmd, _pipelineLayout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0, (uint)sizeof(WorldPush), &push);

            _vk.Vk.CmdDrawIndexed(cmd, (uint)_terrainIndexCount, 1, 0, 0, 0);
        }

        private void RebuildTerrain()
        {
            const int N = TerrainManager.RegionVerts; // 257
            int vCount  = N * N;
            int iCount  = (N - 1) * (N - 1) * 6;

            var verts   = new WorldVertex[vCount];
            var indices = new uint[iCount];

            var heights = _terrain.Heights;

            // Build vertices
            for (int z = 0; z < N; z++)
            for (int x = 0; x < N; x++)
            {
                float h = heights[z * N + x];

                // Compute normal from finite differences
                float hL = x > 0     ? heights[z * N + x - 1] : h;
                float hR = x < N - 1 ? heights[z * N + x + 1] : h;
                float hD = z > 0     ? heights[(z - 1) * N + x] : h;
                float hU = z < N - 1 ? heights[(z + 1) * N + x] : h;
                var normal = Vector3.Normalize(new Vector3(hL - hR, 2f, hD - hU));

                verts[z * N + x] = new WorldVertex
                {
                    Position = new Vector3(x, h, z),
                    Normal   = normal,
                    UV       = new Vector2(x / (float)(N - 1), z / (float)(N - 1)),
                };
            }

            // Build indices (CCW quads)
            int ii = 0;
            for (int z = 0; z < N - 1; z++)
            for (int x = 0; x < N - 1; x++)
            {
                uint tl = (uint)(z * N + x);
                uint tr = tl + 1;
                uint bl = (uint)((z + 1) * N + x);
                uint br = bl + 1;
                indices[ii++] = tl; indices[ii++] = bl; indices[ii++] = tr;
                indices[ii++] = tr; indices[ii++] = bl; indices[ii++] = br;
            }

            // Free old buffers
            DestroyBuffer(ref _terrainVb, ref _terrainVbMem);
            DestroyBuffer(ref _terrainIb, ref _terrainIbMem);

            CreateBuffer(verts,   BufferUsageFlags.VertexBufferBit, out _terrainVb,  out _terrainVbMem);
            CreateBuffer(indices, BufferUsageFlags.IndexBufferBit,  out _terrainIb,  out _terrainIbMem);
            _terrainIndexCount = iCount;
        }

        // ── Objects ───────────────────────────────────────────────────────────

        private void DrawObjects(CommandBuffer cmd, in RenderContext ctx)
        {
            ulong zero = 0;
            _vk.Vk.CmdBindVertexBuffers(cmd, 0, 1, &_cubeVb, &zero);
            _vk.Vk.CmdBindIndexBuffer(cmd, _cubeIb, 0, IndexType.Uint32);

            foreach (var (_, obj) in _objects.Objects)
            {
                var model = obj.IsAvatar
                    ? CapsuleMatrix(obj.Position, obj.Rotation, obj.Scale)
                    : MathEx.TRS(obj.Position, obj.Rotation, obj.Scale);

                var push = new WorldPush
                {
                    Model   = model,
                    View    = ctx.ViewMatrix,
                    Proj    = ctx.ProjectionMatrix,
                    Tint    = obj.IsAvatar ? ColAv : ColPrim,
                    Glow    = 0,
                    RepeatU = 1, RepeatV = 1,
                };

                _vk.Vk.CmdPushConstants(cmd, _pipelineLayout,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                    0, (uint)sizeof(WorldPush), &push);

                _vk.Vk.CmdDrawIndexed(cmd, (uint)_cubeIndexCount, 1, 0, 0, 0);
            }
        }

        private static Matrix4x4 CapsuleMatrix(Vector3 pos, Quaternion rot, Vector3 scale)
            => MathEx.TRS(pos + new Vector3(0, scale.Y * 0.5f, 0), rot, scale);

        // ── Cube mesh ─────────────────────────────────────────────────────────

        private void BuildCubeMesh()
        {
            // Unit cube centred at origin: ±0.5 on each axis
            var v = new WorldVertex[]
            {
                // +Y (top)
                new() { Position=new(-0.5f, 0.5f,-0.5f), Normal=Vector3.UnitY, UV=new(0,0) },
                new() { Position=new( 0.5f, 0.5f,-0.5f), Normal=Vector3.UnitY, UV=new(1,0) },
                new() { Position=new( 0.5f, 0.5f, 0.5f), Normal=Vector3.UnitY, UV=new(1,1) },
                new() { Position=new(-0.5f, 0.5f, 0.5f), Normal=Vector3.UnitY, UV=new(0,1) },
                // -Y (bottom)
                new() { Position=new(-0.5f,-0.5f,-0.5f), Normal=-Vector3.UnitY, UV=new(0,0) },
                new() { Position=new( 0.5f,-0.5f,-0.5f), Normal=-Vector3.UnitY, UV=new(1,0) },
                new() { Position=new( 0.5f,-0.5f, 0.5f), Normal=-Vector3.UnitY, UV=new(1,1) },
                new() { Position=new(-0.5f,-0.5f, 0.5f), Normal=-Vector3.UnitY, UV=new(0,1) },
                // +X
                new() { Position=new( 0.5f,-0.5f,-0.5f), Normal=Vector3.UnitX, UV=new(0,0) },
                new() { Position=new( 0.5f, 0.5f,-0.5f), Normal=Vector3.UnitX, UV=new(1,0) },
                new() { Position=new( 0.5f, 0.5f, 0.5f), Normal=Vector3.UnitX, UV=new(1,1) },
                new() { Position=new( 0.5f,-0.5f, 0.5f), Normal=Vector3.UnitX, UV=new(0,1) },
                // -X
                new() { Position=new(-0.5f,-0.5f,-0.5f), Normal=-Vector3.UnitX, UV=new(0,0) },
                new() { Position=new(-0.5f, 0.5f,-0.5f), Normal=-Vector3.UnitX, UV=new(1,0) },
                new() { Position=new(-0.5f, 0.5f, 0.5f), Normal=-Vector3.UnitX, UV=new(1,1) },
                new() { Position=new(-0.5f,-0.5f, 0.5f), Normal=-Vector3.UnitX, UV=new(0,1) },
                // +Z (south in SL convention)
                new() { Position=new(-0.5f,-0.5f, 0.5f), Normal=Vector3.UnitZ, UV=new(0,0) },
                new() { Position=new( 0.5f,-0.5f, 0.5f), Normal=Vector3.UnitZ, UV=new(1,0) },
                new() { Position=new( 0.5f, 0.5f, 0.5f), Normal=Vector3.UnitZ, UV=new(1,1) },
                new() { Position=new(-0.5f, 0.5f, 0.5f), Normal=Vector3.UnitZ, UV=new(0,1) },
                // -Z
                new() { Position=new(-0.5f,-0.5f,-0.5f), Normal=-Vector3.UnitZ, UV=new(0,0) },
                new() { Position=new( 0.5f,-0.5f,-0.5f), Normal=-Vector3.UnitZ, UV=new(1,0) },
                new() { Position=new( 0.5f, 0.5f,-0.5f), Normal=-Vector3.UnitZ, UV=new(1,1) },
                new() { Position=new(-0.5f, 0.5f,-0.5f), Normal=-Vector3.UnitZ, UV=new(0,1) },
            };

            var idx = new uint[36];
            for (int f = 0; f < 6; f++)
            {
                uint b = (uint)(f * 4);
                int  i = f * 6;
                idx[i]   = b;     idx[i+1] = b+1; idx[i+2] = b+2;
                idx[i+3] = b;     idx[i+4] = b+2; idx[i+5] = b+3;
            }

            CreateBuffer(v,   BufferUsageFlags.VertexBufferBit, out _cubeVb,  out _cubeVbMem);
            CreateBuffer(idx, BufferUsageFlags.IndexBufferBit,  out _cubeIb,  out _cubeIbMem);
            _cubeIndexCount = idx.Length;
        }

        // ── Vulkan setup ──────────────────────────────────────────────────────

        private void CreateSampler()
        {
            var si = new SamplerCreateInfo
            {
                SType        = StructureType.SamplerCreateInfo,
                MagFilter    = Filter.Linear,
                MinFilter    = Filter.Linear,
                MipmapMode   = SamplerMipmapMode.Linear,
                AddressModeU = SamplerAddressMode.Repeat,
                AddressModeV = SamplerAddressMode.Repeat,
                AddressModeW = SamplerAddressMode.Repeat,
                MaxLod       = 16f,
            };
            Sampler s;
            _vk.Vk.CreateSampler(_vk.Device, &si, null, &s);
            _sampler = s;
        }

        private void CreateWhiteTexture()
        {
            var ici = new ImageCreateInfo
            {
                SType       = StructureType.ImageCreateInfo,
                ImageType   = ImageType.Type2D,
                Format      = Format.R8G8B8A8Srgb,
                Extent      = new Extent3D(1, 1, 1),
                MipLevels   = 1,
                ArrayLayers = 1,
                Samples     = SampleCountFlags.Count1Bit,
                Tiling      = ImageTiling.Optimal,
                Usage       = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                InitialLayout = ImageLayout.Undefined,
            };
            Silk.NET.Vulkan.Image img;
            _vk.Vk.CreateImage(_vk.Device, &ici, null, &img);

            MemoryRequirements req;
            _vk.Vk.GetImageMemoryRequirements(_vk.Device, img, &req);
            var mai = new MemoryAllocateInfo
            {
                SType           = StructureType.MemoryAllocateInfo,
                AllocationSize  = req.Size,
                MemoryTypeIndex = _vk.FindMemoryType(req.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            DeviceMemory mem;
            _vk.Vk.AllocateMemory(_vk.Device, &mai, null, &mem);
            _vk.Vk.BindImageMemory(_vk.Device, img, mem, 0);

            // Upload 0xFFFFFFFF pixel via staging buffer
            byte[] white = [255, 255, 255, 255];
            CreateBuffer(white, BufferUsageFlags.TransferSrcBit,
                out var staging, out var stagingMem);

            var cb = _vk.BeginOneShot();

            // Transition Undefined → TransferDst
            TransitionImage(cb, img, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);

            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LayerCount = 1,
                },
                ImageExtent = new Extent3D(1, 1, 1),
            };
            _vk.Vk.CmdCopyBufferToImage(cb, staging, img,
                ImageLayout.TransferDstOptimal, 1, &region);

            // Transition TransferDst → ShaderReadOnly
            TransitionImage(cb, img, ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal);

            _vk.EndOneShot(cb);

            _vk.Vk.DestroyBuffer(_vk.Device, staging, null);
            _vk.Vk.FreeMemory(_vk.Device, stagingMem, null);

            var ivci = new ImageViewCreateInfo
            {
                SType    = StructureType.ImageViewCreateInfo,
                Image    = img,
                ViewType = ImageViewType.Type2D,
                Format   = Format.R8G8B8A8Srgb,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LevelCount = 1,
                    LayerCount = 1,
                },
            };
            ImageView view;
            _vk.Vk.CreateImageView(_vk.Device, &ivci, null, &view);

            _whiteTex     = img;
            _whiteTexMem  = mem;
            _whiteTexView = view;
        }

        private void CreateDescriptorSetLayout()
        {
            var binding = new DescriptorSetLayoutBinding
            {
                Binding         = 0,
                DescriptorType  = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags      = ShaderStageFlags.FragmentBit,
            };
            var lci = new DescriptorSetLayoutCreateInfo
            {
                SType        = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings    = &binding,
            };
            DescriptorSetLayout dsl;
            _vk.Vk.CreateDescriptorSetLayout(_vk.Device, &lci, null, &dsl);
            _descLayout = dsl;
        }

        private void CreateDescriptorPool()
        {
            var size = new DescriptorPoolSize
            {
                Type            = DescriptorType.CombinedImageSampler,
                DescriptorCount = 64, // room for many textures later
            };
            var pci = new DescriptorPoolCreateInfo
            {
                SType         = StructureType.DescriptorPoolCreateInfo,
                MaxSets       = 64,
                PoolSizeCount = 1,
                PPoolSizes    = &size,
            };
            DescriptorPool pool;
            _vk.Vk.CreateDescriptorPool(_vk.Device, &pci, null, &pool);
            _descPool = pool;
        }

        private void AllocWriteWhiteDesc()
        {
            var layout = _descLayout;
            var ai = new DescriptorSetAllocateInfo
            {
                SType              = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool     = _descPool,
                DescriptorSetCount = 1,
                PSetLayouts        = &layout,
            };
            DescriptorSet ds;
            _vk.Vk.AllocateDescriptorSets(_vk.Device, &ai, &ds);
            _whiteDescSet = ds;

            var imgInfo = new DescriptorImageInfo
            {
                Sampler     = _sampler,
                ImageView   = _whiteTexView,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            var write = new WriteDescriptorSet
            {
                SType           = StructureType.WriteDescriptorSet,
                DstSet          = ds,
                DstBinding      = 0,
                DescriptorCount = 1,
                DescriptorType  = DescriptorType.CombinedImageSampler,
                PImageInfo      = &imgInfo,
            };
            _vk.Vk.UpdateDescriptorSets(_vk.Device, 1, &write, 0, null);
        }

        private void LoadPipeline()
        {
            var vertSpv = LoadSpirV("world.vert");
            var fragSpv = LoadSpirV("world.frag");

            var vertMod = CreateShaderModule(vertSpv);
            var fragMod = CreateShaderModule(fragSpv);

            // Push constant range: 240 bytes, both stages
            var pcRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                Offset     = 0,
                Size       = (uint)sizeof(WorldPush),
            };

            var descLayout = _descLayout;
            var plci = new PipelineLayoutCreateInfo
            {
                SType                  = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount         = 1,
                PSetLayouts            = &descLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges    = &pcRange,
            };
            PipelineLayout pl;
            _vk.Vk.CreatePipelineLayout(_vk.Device, &plci, null, &pl);
            _pipelineLayout = pl;

            // Vertex input: one binding, stride=32
            var vbDesc = new VertexInputBindingDescription
            {
                Binding   = 0,
                Stride    = (uint)sizeof(WorldVertex),
                InputRate = VertexInputRate.Vertex,
            };
            var attrs = stackalloc VertexInputAttributeDescription[]
            {
                new() { Binding=0, Location=0, Format=Format.R32G32B32Sfloat, Offset=0  },  // position
                new() { Binding=0, Location=1, Format=Format.R32G32B32Sfloat, Offset=12 },  // normal
                new() { Binding=0, Location=2, Format=Format.R32G32Sfloat,    Offset=24 },  // uv
            };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType                           = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount   = 1,
                PVertexBindingDescriptions      = &vbDesc,
                VertexAttributeDescriptionCount = 3,
                PVertexAttributeDescriptions    = attrs,
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType    = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };

            var viewport = new PipelineViewportStateCreateInfo
            {
                SType         = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount  = 1,
            };

            var raster = new PipelineRasterizationStateCreateInfo
            {
                SType       = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode    = CullModeFlags.BackBit,
                FrontFace   = FrontFace.CounterClockwise,
                LineWidth   = 1f,
            };

            var ms = new PipelineMultisampleStateCreateInfo
            {
                SType                = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };

            var ds = new PipelineDepthStencilStateCreateInfo
            {
                SType            = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable  = true,
                DepthWriteEnable = true,
                DepthCompareOp   = CompareOp.LessOrEqual,
            };

            var blendAttach = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable    = false,
            };
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType           = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments    = &blendAttach,
            };

            var dynamics = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            var dynState = new PipelineDynamicStateCreateInfo
            {
                SType             = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates    = dynamics,
            };

            fixed (byte* mainName = _mainUtf8)
            {
            var stages = stackalloc PipelineShaderStageCreateInfo[]
            {
                new() { SType=StructureType.PipelineShaderStageCreateInfo,
                        Stage=ShaderStageFlags.VertexBit,   Module=vertMod, PName=mainName },
                new() { SType=StructureType.PipelineShaderStageCreateInfo,
                        Stage=ShaderStageFlags.FragmentBit, Module=fragMod, PName=mainName },
            };

            var gpci = new GraphicsPipelineCreateInfo
            {
                SType               = StructureType.GraphicsPipelineCreateInfo,
                StageCount          = 2,
                PStages             = stages,
                PVertexInputState   = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState      = &viewport,
                PRasterizationState = &raster,
                PMultisampleState   = &ms,
                PDepthStencilState  = &ds,
                PColorBlendState    = &blend,
                PDynamicState       = &dynState,
                Layout              = _pipelineLayout,
                RenderPass          = _swapchain.RenderPass,
                Subpass             = 0,
            };

            Pipeline pipe;
            _vk.Vk.CreateGraphicsPipelines(_vk.Device, default, 1, &gpci, null, &pipe);
            _pipeline = pipe;

            } // end fixed (mainName)

            _vk.Vk.DestroyShaderModule(_vk.Device, vertMod, null);
            _vk.Vk.DestroyShaderModule(_vk.Device, fragMod, null);
        }

        // ── Vulkan helpers ────────────────────────────────────────────────────

        private static byte[] LoadSpirV(string name)
        {
            // Primary: embedded bytecode generated by build.sh into ShaderBytecode.g.cs
            var key = name.Replace('.', '_');
            var field = typeof(ShaderBytecode).GetField(key,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (field?.GetValue(null) is byte[] embedded && embedded.Length > 0)
                return embedded;

            // Development fallback: .spv file on device (adb push)
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Resources", "shaders", name + ".spv"),
                Path.Combine("/data/local/tmp/slquest/shaders", name + ".spv"),
            };
            foreach (var p in candidates)
                if (File.Exists(p)) return File.ReadAllBytes(p);

            throw new FileNotFoundException(
                $"SPIR-V not found for '{name}'. Run build.sh to compile shaders.");
        }

        private ShaderModule CreateShaderModule(byte[] spv)
        {
            fixed (byte* p = spv)
            {
                var ci = new ShaderModuleCreateInfo
                {
                    SType    = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)spv.Length,
                    PCode    = (uint*)p,
                };
                ShaderModule m;
                _vk.Vk.CreateShaderModule(_vk.Device, &ci, null, &m);
                return m;
            }
        }

        // "main" as UTF-8 bytes, pinned for the lifetime of the app.
        private static readonly byte[] _mainUtf8 = [(byte)'m',(byte)'a',(byte)'i',(byte)'n',0];

        private void CreateBuffer<T>(T[] data, BufferUsageFlags usage,
            out Buffer buf, out DeviceMemory mem) where T : struct
        {
            int byteLen = data.Length * Marshal.SizeOf<T>();

            // Staging buffer (host visible)
            var bci = new BufferCreateInfo
            {
                SType       = StructureType.BufferCreateInfo,
                Size        = (ulong)byteLen,
                Usage       = usage | BufferUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
            };
            Buffer b;
            _vk.Vk.CreateBuffer(_vk.Device, &bci, null, &b);

            MemoryRequirements req;
            _vk.Vk.GetBufferMemoryRequirements(_vk.Device, b, &req);

            // Try device-local with host-visible fallback (mobile UMA)
            uint memType;
            try
            {
                memType = _vk.FindMemoryType(req.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit);
            }
            catch
            {
                // Fallback: separate staging (uncommon on Quest but safe)
                memType = _vk.FindMemoryType(req.MemoryTypeBits,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            }

            var mai = new MemoryAllocateInfo
            {
                SType           = StructureType.MemoryAllocateInfo,
                AllocationSize  = req.Size,
                MemoryTypeIndex = memType,
            };
            DeviceMemory m2;
            _vk.Vk.AllocateMemory(_vk.Device, &mai, null, &m2);
            _vk.Vk.BindBufferMemory(_vk.Device, b, m2, 0);

            void* mapped;
            _vk.Vk.MapMemory(_vk.Device, m2, 0, (ulong)byteLen, 0, &mapped);
            var span = new Span<T>(mapped, data.Length);
            data.CopyTo(span);
            _vk.Vk.UnmapMemory(_vk.Device, m2);

            buf = b;
            mem = m2;
        }

        private void DestroyBuffer(ref Buffer b, ref DeviceMemory m)
        {
            if (b.Handle != 0)
            {
                _vk.Vk.DestroyBuffer(_vk.Device, b, null);
                _vk.Vk.FreeMemory(_vk.Device, m, null);
                b = default;
                m = default;
            }
        }

        private void TransitionImage(CommandBuffer cb, Silk.NET.Vulkan.Image img,
            ImageLayout from, ImageLayout to)
        {
            var barrier = new ImageMemoryBarrier
            {
                SType            = StructureType.ImageMemoryBarrier,
                OldLayout        = from,
                NewLayout        = to,
                Image            = img,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LevelCount = 1,
                    LayerCount = 1,
                },
                SrcAccessMask    = AccessFlags.None,
                DstAccessMask    = AccessFlags.TransferWriteBit,
            };
            _vk.Vk.CmdPipelineBarrier(cb,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, &barrier);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _vk.Vk.DeviceWaitIdle(_vk.Device);

            DestroyBuffer(ref _terrainVb,  ref _terrainVbMem);
            DestroyBuffer(ref _terrainIb,  ref _terrainIbMem);
            DestroyBuffer(ref _cubeVb,     ref _cubeVbMem);
            DestroyBuffer(ref _cubeIb,     ref _cubeIbMem);

            if (_pipeline.Handle      != 0) _vk.Vk.DestroyPipeline(_vk.Device, _pipeline, null);
            if (_pipelineLayout.Handle != 0) _vk.Vk.DestroyPipelineLayout(_vk.Device, _pipelineLayout, null);
            if (_descPool.Handle      != 0) _vk.Vk.DestroyDescriptorPool(_vk.Device, _descPool, null);
            if (_descLayout.Handle    != 0) _vk.Vk.DestroyDescriptorSetLayout(_vk.Device, _descLayout, null);
            if (_whiteTexView.Handle  != 0) _vk.Vk.DestroyImageView(_vk.Device, _whiteTexView, null);
            if (_whiteTex.Handle      != 0) _vk.Vk.DestroyImage(_vk.Device, _whiteTex, null);
            if (_whiteTexMem.Handle   != 0) _vk.Vk.FreeMemory(_vk.Device, _whiteTexMem, null);
            if (_sampler.Handle       != 0) _vk.Vk.DestroySampler(_vk.Device, _sampler, null);
        }
    }
}
