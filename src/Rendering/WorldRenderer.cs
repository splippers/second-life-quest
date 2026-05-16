using System.Numerics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Silk.NET.Vulkan;
using SLQuest.Core;
using SLQuest.World;
using SLQuest.Avatar;
using SLQuest.UI;
using SLQuest.Assets;
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

    // Mirrors ui.vert / ui.frag push_constant block — 96 bytes
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct UIPush
    {
        public Matrix4x4 Transform;   // 64
        public Vector4   Color;       // 16
        public float     UvOffsetX;   //  4
        public float     UvOffsetY;   //  4
        public float     UvScaleX;    //  4
        public float     UvScaleY;    //  4
    }

    public sealed unsafe class WorldRenderer : IDisposable
    {
        private readonly VulkanContext  _vk;
        private readonly ObjectManager  _objects;
        private readonly TerrainManager _terrain;
        private readonly AvatarManager  _avatars;
        private readonly AssetManager?  _assets;
        private SwapchainRenderer       _swapchain = null!

        // Per-texture GPU resources — keyed by SL asset UUID.
        private sealed class TexEntry
        {
            public Silk.NET.Vulkan.Image Img;
            public DeviceMemory          Mem;
            public ImageView             View;
            public DescriptorSet         Desc;
        }
        private readonly ConcurrentDictionary<Guid, TexEntry> _texCache = new();;

        // World geometry pipeline
        private Pipeline            _pipeline;
        private PipelineLayout      _pipelineLayout;
        private DescriptorSetLayout _descLayout;
        private DescriptorPool      _descPool;
        private DescriptorSet       _whiteDescSet;
        private Sampler             _sampler;

        // UI pipeline (ui.vert / ui.frag) — screen-space, no depth test, alpha blend
        private Pipeline            _uiPipeline;
        private PipelineLayout      _uiLayout;
        private DescriptorSet       _fontDescSet;

        // Bitmap font atlas — 5×7 glyph, 8px wide cell × 12 rows × 8 cols = 64×84 image
        // Charset: ASCII 32–127 (96 printable chars) in 8-col rows
        private Silk.NET.Vulkan.Image _fontTex;
        private DeviceMemory          _fontTexMem;
        private ImageView             _fontTexView;

        // Per-frame dynamic quad buffer for UI geometry — persistently mapped
        private Buffer       _uiVb;
        private DeviceMemory _uiVbMem;
        private void*        _uiMapped    = null;  // persistent host pointer; never unmapped
        private int          _uiCursor    = 0;     // write cursor in vertices (5 floats each)
        private const int    UiVbCapacity = 65536; // bytes — ~2600 quads

        // White 1×1 texture used when a prim has no texture assigned
        private Silk.NET.Vulkan.Image _whiteTex;
        private DeviceMemory          _whiteTexMem;
        private ImageView             _whiteTexView;

        // Terrain GPU buffers
        private Buffer      _terrainVb,  _terrainIb;
        private DeviceMemory _terrainVbMem, _terrainIbMem;
        private int          _terrainIndexCount;
        private bool         _terrainDirty = true;

        // Cube mesh (Box prims and fallback)
        private Buffer       _cubeVb,  _cubeIb;
        private DeviceMemory _cubeVbMem, _cubeIbMem;
        private int          _cubeIndexCount;

        // Cylinder mesh (Cylinder prims and avatar capsules)
        private Buffer       _cylinderVb,  _cylinderIb;
        private DeviceMemory _cylinderVbMem, _cylinderIbMem;
        private int          _cylinderIndexCount;

        // Sphere mesh (Sphere prims)
        private Buffer       _sphereVb,  _sphereIb;
        private DeviceMemory _sphereVbMem, _sphereIbMem;
        private int          _sphereIndexCount;

        // Torus mesh (Torus prims)
        private Buffer       _torusVb,  _torusIb;
        private DeviceMemory _torusVbMem, _torusIbMem;
        private int          _torusIndexCount;

        private bool _disposed;

        // Terrain colours: simple altitude bands
        private static readonly Vector4 ColWater = new(0.10f, 0.25f, 0.55f, 1f);
        private static readonly Vector4 ColGrass = new(0.22f, 0.55f, 0.18f, 1f);
        private static readonly Vector4 ColRock  = new(0.55f, 0.50f, 0.40f, 1f);
        private static readonly Vector4 ColSnow  = new(0.90f, 0.92f, 0.95f, 1f);
        private static readonly Vector4 ColPrim  = new(0.80f, 0.80f, 0.85f, 1f);
        private static readonly Vector4 ColAv    = new(0.40f, 0.65f, 1.00f, 1f);

        public WorldRenderer(VulkanContext vk, ObjectManager objects,
                             TerrainManager terrain, AvatarManager avatars,
                             AssetManager? assets = null)
        {
            _vk = vk; _objects = objects; _terrain = terrain;
            _avatars = avatars; _assets = assets;
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
                BuildCylinderMesh();
                BuildSphereMesh();
                BuildTorusMesh();
                CreateFontAtlas();
                LoadUIPipeline();
                CreateUiVertexBuffer();
            });

            EventBus.Subscribe<TerrainPatchEvent>(_ => _terrainDirty = true);
        }

        public void Render(in RenderContext ctx, UIManager? ui = null)
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

            // Terrain always uses the white descriptor (no textures on terrain)
            var whiteds = _whiteDescSet;
            _vk.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics,
                _pipelineLayout, 0, 1, &whiteds, 0, null);

            DrawTerrain(cmd, ctx);
            DrawObjects(cmd, ctx);

            if (ui != null)
                DrawUI(cmd, ui, ctx.Width, ctx.Height, ctx.ViewMatrix * ctx.ProjectionMatrix);
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
            var vp     = ctx.ViewMatrix * ctx.ProjectionMatrix;
            var planes = ExtractFrustumPlanes(vp);

            Buffer       curVb = default;
            DescriptorSet curDs = default;

            foreach (var (_, obj) in _objects.Objects)
            {
                float radius = obj.Scale.Length() * 0.866f;
                if (!SphereInFrustum(planes, obj.Position, radius)) continue;

                // Select geometry based on prim shape
                Buffer needVb, needIb;
                int indexCount;
                if (obj.Shape == SLPrimShape.Sphere) {
                    needVb = _sphereVb; needIb = _sphereIb; indexCount = _sphereIndexCount;
                } else if (obj.Shape == SLPrimShape.Cylinder || obj.IsAvatar) {
                    needVb = _cylinderVb; needIb = _cylinderIb; indexCount = _cylinderIndexCount;
                } else if (obj.Shape == SLPrimShape.Torus) {
                    needVb = _torusVb; needIb = _torusIb; indexCount = _torusIndexCount;
                } else {
                    needVb = _cubeVb; needIb = _cubeIb; indexCount = _cubeIndexCount;
                }

                if (needVb.Handle != curVb.Handle) {
                    ulong zero = 0;
                    _vk.Vk.CmdBindVertexBuffers(cmd, 0, 1, &needVb, &zero);
                    _vk.Vk.CmdBindIndexBuffer(cmd, needIb, 0, IndexType.Uint32);
                    curVb = needVb;
                }

                // Bind per-prim texture if available, else white fallback
                DescriptorSet needDs = (obj.TextureId != Guid.Empty &&
                    _texCache.TryGetValue(obj.TextureId, out var tex))
                    ? tex.Desc : _whiteDescSet;
                if (needDs.Handle != curDs.Handle) {
                    _vk.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics,
                        _pipelineLayout, 0, 1, &needDs, 0, null);
                    curDs = needDs;
                }

                var model = obj.IsAvatar
                    ? CapsuleMatrix(obj.Position, obj.Rotation, obj.Scale)
                    : MathEx.TRS(obj.Position, obj.Rotation, obj.Scale);

                var push = new WorldPush
                {
                    Model   = model,
                    View    = ctx.ViewMatrix,
                    Proj    = ctx.ProjectionMatrix,
                    Tint    = obj.IsAvatar ? ColAv : Vector4.One,
                    Glow    = 0,
                    RepeatU = 1, RepeatV = 1,
                };

                _vk.Vk.CmdPushConstants(cmd, _pipelineLayout,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                    0, (uint)sizeof(WorldPush), &push);

                _vk.Vk.CmdDrawIndexed(cmd, (uint)indexCount, 1, 0, 0, 0);
            }
        }

        // ── Frustum culling ───────────────────────────────────────────────────

        // Extracts 6 normalized frustum planes from a combined VP matrix.
        // System.Numerics is row-major; transform is v' = v * M.
        // Gribb-Hartmann extraction adapted for Vulkan NDC (z in [0,1]).
        private static System.Numerics.Plane[] ExtractFrustumPlanes(Matrix4x4 m)
        {
            static System.Numerics.Plane MakePlane(float a, float b, float c, float d)
            {
                float len = MathF.Sqrt(a*a + b*b + c*c);
                return new System.Numerics.Plane(a/len, b/len, c/len, d/len);
            }
            return
            [
                MakePlane(m.M14+m.M11, m.M24+m.M21, m.M34+m.M31, m.M44+m.M41), // left
                MakePlane(m.M14-m.M11, m.M24-m.M21, m.M34-m.M31, m.M44-m.M41), // right
                MakePlane(m.M14+m.M12, m.M24+m.M22, m.M34+m.M32, m.M44+m.M42), // bottom
                MakePlane(m.M14-m.M12, m.M24-m.M22, m.M34-m.M32, m.M44-m.M42), // top
                MakePlane(        m.M13,         m.M23,         m.M33,         m.M43), // near (Vulkan)
                MakePlane(m.M14-m.M13, m.M24-m.M23, m.M34-m.M33, m.M44-m.M43), // far
            ];
        }

        // Returns false if the sphere is fully outside any frustum plane.
        private static bool SphereInFrustum(
            System.Numerics.Plane[] planes, Vector3 center, float radius)
        {
            foreach (var p in planes)
                if (System.Numerics.Plane.DotCoordinate(p, center) < -radius)
                    return false;
            return true;
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

        // ── Cylinder mesh ─────────────────────────────────────────────────────

        private void BuildCylinderMesh()
        {
            const int   segs = 16;
            const float r    = 0.5f, h = 0.5f; // unit cylinder: radius=0.5, half-height=0.5
            var verts   = new List<WorldVertex>();
            var indices = new List<uint>();

            // Top cap centre
            int topCtr = verts.Count;
            verts.Add(new WorldVertex { Position = new(0, h, 0), Normal = Vector3.UnitY, UV = new(0.5f, 0.5f) });

            // Top ring (segs+1 so the last point wraps to 0 for UVs)
            int topRing = verts.Count;
            for (int i = 0; i <= segs; i++)
            {
                float a = 2 * MathF.PI * i / segs;
                float x = r * MathF.Cos(a), z = r * MathF.Sin(a);
                verts.Add(new WorldVertex { Position = new(x, h, z), Normal = Vector3.UnitY,
                    UV = new(x + 0.5f, z + 0.5f) });
            }

            // Bottom ring
            int botRing = verts.Count;
            for (int i = 0; i <= segs; i++)
            {
                float a = 2 * MathF.PI * i / segs;
                float x = r * MathF.Cos(a), z = r * MathF.Sin(a);
                verts.Add(new WorldVertex { Position = new(x, -h, z), Normal = -Vector3.UnitY,
                    UV = new(x + 0.5f, z + 0.5f) });
            }

            // Bottom cap centre
            int botCtr = verts.Count;
            verts.Add(new WorldVertex { Position = new(0, -h, 0), Normal = -Vector3.UnitY, UV = new(0.5f, 0.5f) });

            // Side verts with outward normals (separate from cap verts for correct shading)
            int sideBase = verts.Count;
            for (int i = 0; i <= segs; i++)
            {
                float a = 2 * MathF.PI * i / segs;
                float x = r * MathF.Cos(a), z = r * MathF.Sin(a);
                var n = Vector3.Normalize(new Vector3(x, 0, z));
                float u = (float)i / segs;
                verts.Add(new WorldVertex { Position = new(x,  h, z), Normal = n, UV = new(u, 0) });
                verts.Add(new WorldVertex { Position = new(x, -h, z), Normal = n, UV = new(u, 1) });
            }

            // Top cap fans
            for (int i = 0; i < segs; i++)
            {
                indices.Add((uint)topCtr);
                indices.Add((uint)(topRing + i));
                indices.Add((uint)(topRing + i + 1));
            }

            // Bottom cap fans (reversed winding for outward normal)
            for (int i = 0; i < segs; i++)
            {
                indices.Add((uint)botCtr);
                indices.Add((uint)(botRing + i + 1));
                indices.Add((uint)(botRing + i));
            }

            // Side quads
            for (int i = 0; i < segs; i++)
            {
                uint tl = (uint)(sideBase + i * 2);
                uint bl = (uint)(sideBase + i * 2 + 1);
                uint tr = (uint)(sideBase + (i + 1) * 2);
                uint br = (uint)(sideBase + (i + 1) * 2 + 1);
                indices.Add(tl); indices.Add(bl); indices.Add(tr);
                indices.Add(tr); indices.Add(bl); indices.Add(br);
            }

            var vArr = verts.ToArray();
            var iArr = indices.ToArray();
            CreateBuffer(vArr, BufferUsageFlags.VertexBufferBit, out _cylinderVb,  out _cylinderVbMem);
            CreateBuffer(iArr, BufferUsageFlags.IndexBufferBit,  out _cylinderIb,  out _cylinderIbMem);
            _cylinderIndexCount = iArr.Length;
        }

        // ── Sphere mesh ───────────────────────────────────────────────────────

        private void BuildSphereMesh()
        {
            const int stacks = 8, slices = 16;
            var verts   = new List<WorldVertex>();
            var indices = new List<uint>();

            // Top pole
            verts.Add(new WorldVertex { Position = new(0, 0.5f, 0), Normal = Vector3.UnitY, UV = new(0.5f, 0) });

            // Latitude rings (stacks-1 interior rings, poles handled separately)
            for (int st = 1; st < stacks; st++)
            {
                float phi = MathF.PI * st / stacks;
                float y   = 0.5f * MathF.Cos(phi);
                float cr  = 0.5f * MathF.Sin(phi);
                for (int sl = 0; sl <= slices; sl++)
                {
                    float theta = 2 * MathF.PI * sl / slices;
                    float x = cr * MathF.Cos(theta);
                    float z = cr * MathF.Sin(theta);
                    var pos = new Vector3(x, y, z);
                    verts.Add(new WorldVertex
                    {
                        Position = pos,
                        Normal   = Vector3.Normalize(pos),
                        UV       = new Vector2((float)sl / slices, (float)st / stacks),
                    });
                }
            }

            // Bottom pole
            int botPole = verts.Count;
            verts.Add(new WorldVertex { Position = new(0, -0.5f, 0), Normal = -Vector3.UnitY, UV = new(0.5f, 1) });

            int RingStart(int st) => 1 + (st - 1) * (slices + 1);

            // Top cap (pole → first ring)
            for (int sl = 0; sl < slices; sl++)
            {
                indices.Add(0);
                indices.Add((uint)(RingStart(1) + sl + 1));
                indices.Add((uint)(RingStart(1) + sl));
            }

            // Middle quad bands
            for (int st = 1; st < stacks - 1; st++)
            {
                for (int sl = 0; sl < slices; sl++)
                {
                    uint tl = (uint)(RingStart(st)     + sl);
                    uint tr = (uint)(RingStart(st)     + sl + 1);
                    uint bl = (uint)(RingStart(st + 1) + sl);
                    uint br = (uint)(RingStart(st + 1) + sl + 1);
                    indices.Add(tl); indices.Add(bl); indices.Add(tr);
                    indices.Add(tr); indices.Add(bl); indices.Add(br);
                }
            }

            // Bottom cap (last ring → bottom pole)
            int lastRing = stacks - 1;
            for (int sl = 0; sl < slices; sl++)
            {
                indices.Add((uint)botPole);
                indices.Add((uint)(RingStart(lastRing) + sl));
                indices.Add((uint)(RingStart(lastRing) + sl + 1));
            }

            var vArr = verts.ToArray();
            var iArr = indices.ToArray();
            CreateBuffer(vArr, BufferUsageFlags.VertexBufferBit, out _sphereVb,  out _sphereVbMem);
            CreateBuffer(iArr, BufferUsageFlags.IndexBufferBit,  out _sphereIb,  out _sphereIbMem);
            _sphereIndexCount = iArr.Length;
        }

        // ── Torus mesh ────────────────────────────────────────────────────────

        private void BuildTorusMesh()
        {
            const int   majorSegs = 24;    // segments around the ring
            const int   minorSegs = 12;    // segments around the tube
            const float majorR    = 0.5f;  // distance from centre of ring to centre of tube
            const float minorR    = 0.15f; // tube radius

            var verts   = new List<WorldVertex>();
            var indices = new List<uint>();

            for (int j = 0; j <= majorSegs; j++)
            {
                float phi = 2 * MathF.PI * j / majorSegs;
                float cosPhi = MathF.Cos(phi), sinPhi = MathF.Sin(phi);

                for (int i = 0; i <= minorSegs; i++)
                {
                    float theta = 2 * MathF.PI * i / minorSegs;
                    float cosTheta = MathF.Cos(theta), sinTheta = MathF.Sin(theta);

                    // Position on torus surface
                    float x = (majorR + minorR * cosTheta) * cosPhi;
                    float y =  minorR * sinTheta;
                    float z = (majorR + minorR * cosTheta) * sinPhi;

                    // Normal = direction from tube centre to surface point
                    float nx = cosTheta * cosPhi;
                    float ny = sinTheta;
                    float nz = cosTheta * sinPhi;

                    verts.Add(new WorldVertex
                    {
                        Position = new Vector3(x, y, z),
                        Normal   = new Vector3(nx, ny, nz),
                        UV       = new Vector2((float)j / majorSegs, (float)i / minorSegs),
                    });
                }
            }

            int ring = minorSegs + 1;
            for (int j = 0; j < majorSegs; j++)
            for (int i = 0; i < minorSegs; i++)
            {
                uint tl = (uint)(j       * ring + i);
                uint tr = (uint)(j       * ring + i + 1);
                uint bl = (uint)((j + 1) * ring + i);
                uint br = (uint)((j + 1) * ring + i + 1);
                indices.Add(tl); indices.Add(bl); indices.Add(tr);
                indices.Add(tr); indices.Add(bl); indices.Add(br);
            }

            var vArr = verts.ToArray();
            var iArr = indices.ToArray();
            CreateBuffer(vArr, BufferUsageFlags.VertexBufferBit, out _torusVb,  out _torusVbMem);
            CreateBuffer(iArr, BufferUsageFlags.IndexBufferBit,  out _torusIb,  out _torusIbMem);
            _torusIndexCount = iArr.Length;
        }

        // ── UI rendering ──────────────────────────────────────────────────────

        // Font: 5×7 glyph bitmap for ASCII 32–127, packed as rows of 5-bit patterns.
        // Row-major within each glyph. Bit 4 = leftmost pixel column.
        // 96 chars × 7 rows = 672 bytes. Char index = ascii - 32.
        private static readonly byte[] _fontBits = BuildFontBits();

        private static byte[] BuildFontBits()
        {
            // 5-bit row patterns for each printable ASCII char (32-127).
            // Columns: 0=space,1=!,2=",3=#,4=$,5=%,6=&,7=',8=(,9=),10=*,11=+,
            //          12=,,13=-,14=.,15=/,16-25=0-9,26=:,27=;,28=<,29==,30=>,31=?,
            //          32=@,33-58=A-Z,59=[,60=\,61=],62=^,63=_,
            //          64=`,65-90=a-z,91={,92=|,93=},94=~,95=DEL(treated as space)
            // Each char = 7 bytes (one per row, bits 4..0 = cols 0..4).
            var d = new byte[96 * 7];
            // Helper to set glyph rows
            void G(int idx, byte r0,byte r1,byte r2,byte r3,byte r4,byte r5,byte r6)
            {
                int b = idx * 7;
                d[b]=r0; d[b+1]=r1; d[b+2]=r2; d[b+3]=r3; d[b+4]=r4; d[b+5]=r5; d[b+6]=r6;
            }
            // space (0)
            G(0,  0,0,0,0,0,0,0);
            // ! (1)
            G(1,  0x04,0x04,0x04,0x04,0x00,0x04,0x00);
            // " (2)
            G(2,  0x0A,0x0A,0x00,0x00,0x00,0x00,0x00);
            // # (3)
            G(3,  0x0A,0x1F,0x0A,0x0A,0x1F,0x0A,0x00);
            // $ (4) — simplified
            G(4,  0x04,0x0F,0x10,0x0E,0x01,0x1E,0x04);
            // % (5)
            G(5,  0x11,0x09,0x02,0x04,0x08,0x13,0x11);
            // & (6)
            G(6,  0x08,0x14,0x14,0x08,0x15,0x12,0x0D);
            // ' (7)
            G(7,  0x04,0x04,0x00,0x00,0x00,0x00,0x00);
            // ( (8)
            G(8,  0x02,0x04,0x08,0x08,0x08,0x04,0x02);
            // ) (9)
            G(9,  0x08,0x04,0x02,0x02,0x02,0x04,0x08);
            // * (10)
            G(10, 0x00,0x04,0x15,0x0E,0x15,0x04,0x00);
            // + (11)
            G(11, 0x00,0x04,0x04,0x1F,0x04,0x04,0x00);
            // , (12)
            G(12, 0x00,0x00,0x00,0x00,0x04,0x04,0x08);
            // - (13)
            G(13, 0x00,0x00,0x00,0x1F,0x00,0x00,0x00);
            // . (14)
            G(14, 0x00,0x00,0x00,0x00,0x00,0x04,0x00);
            // / (15)
            G(15, 0x01,0x02,0x02,0x04,0x08,0x08,0x10);
            // 0-9 (16-25) — standard digit bitmaps
            G(16, 0x0E,0x11,0x13,0x15,0x19,0x11,0x0E); // 0
            G(17, 0x04,0x0C,0x04,0x04,0x04,0x04,0x0E); // 1
            G(18, 0x0E,0x11,0x01,0x02,0x04,0x08,0x1F); // 2
            G(19, 0x1F,0x02,0x04,0x02,0x01,0x11,0x0E); // 3
            G(20, 0x02,0x06,0x0A,0x12,0x1F,0x02,0x02); // 4
            G(21, 0x1F,0x10,0x1E,0x01,0x01,0x11,0x0E); // 5
            G(22, 0x06,0x08,0x10,0x1E,0x11,0x11,0x0E); // 6
            G(23, 0x1F,0x01,0x02,0x04,0x08,0x08,0x08); // 7
            G(24, 0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E); // 8
            G(25, 0x0E,0x11,0x11,0x0F,0x01,0x02,0x0C); // 9
            // : (26)
            G(26, 0x00,0x04,0x00,0x00,0x04,0x00,0x00);
            // ; (27)
            G(27, 0x00,0x04,0x00,0x00,0x04,0x04,0x08);
            // < (28)
            G(28, 0x02,0x04,0x08,0x10,0x08,0x04,0x02);
            // = (29)
            G(29, 0x00,0x00,0x1F,0x00,0x1F,0x00,0x00);
            // > (30)
            G(30, 0x08,0x04,0x02,0x01,0x02,0x04,0x08);
            // ? (31)
            G(31, 0x0E,0x11,0x01,0x02,0x04,0x00,0x04);
            // @ (32)
            G(32, 0x0E,0x11,0x17,0x15,0x17,0x10,0x0E);
            // A-Z (33-58): lifted directly from text_overlay.cpp kFontData columns 0-25
            byte[] az = {
                // A           B           C           D           E           F
                0x0E,0x1E,    0x0E,0x1E,  0x0E,0x11,  0x1E,0x11,  0x1F,0x1F,  0x1F,0x1F,
                // G           H           I           J           K           L
                0x0E,0x11,    0x11,0x11,  0x1F,0x04,  0x0F,0x01,  0x11,0x12,  0x10,0x10,
                // M           N           O           P           Q           R
                0x11,0x11,    0x11,0x11,  0x0E,0x11,  0x1E,0x11,  0x0E,0x11,  0x1E,0x11,
                // S           T           U           V           W           X
                0x0E,0x11,    0x1F,0x04,  0x11,0x11,  0x11,0x11,  0x11,0x11,  0x11,0x11,
                // Y           Z
                0x11,0x11,    0x1F,0x01,
            };
            // A=33 in our indexing (ASCII 65 → idx = 65-32 = 33)
            byte[][,] azGlyphs = {
                // A
                new byte[,]{{0x0E},{0x11},{0x11},{0x1F},{0x11},{0x11},{0x11}},
                new byte[,]{{0x1E},{0x11},{0x11},{0x1E},{0x11},{0x11},{0x1E}},
                new byte[,]{{0x0E},{0x11},{0x10},{0x10},{0x10},{0x11},{0x0E}},
                new byte[,]{{0x1E},{0x11},{0x11},{0x11},{0x11},{0x11},{0x1E}},
                new byte[,]{{0x1F},{0x10},{0x10},{0x1E},{0x10},{0x10},{0x1F}},
                new byte[,]{{0x1F},{0x10},{0x10},{0x1E},{0x10},{0x10},{0x10}},
                new byte[,]{{0x0E},{0x11},{0x10},{0x17},{0x11},{0x11},{0x0F}},
                new byte[,]{{0x11},{0x11},{0x11},{0x1F},{0x11},{0x11},{0x11}},
                new byte[,]{{0x1F},{0x04},{0x04},{0x04},{0x04},{0x04},{0x1F}},
                new byte[,]{{0x0F},{0x01},{0x01},{0x01},{0x01},{0x11},{0x0E}},
                new byte[,]{{0x11},{0x12},{0x14},{0x18},{0x14},{0x12},{0x11}},
                new byte[,]{{0x10},{0x10},{0x10},{0x10},{0x10},{0x10},{0x1F}},
                new byte[,]{{0x11},{0x1B},{0x15},{0x15},{0x11},{0x11},{0x11}},
                new byte[,]{{0x11},{0x19},{0x15},{0x13},{0x11},{0x11},{0x11}},
                new byte[,]{{0x0E},{0x11},{0x11},{0x11},{0x11},{0x11},{0x0E}},
                new byte[,]{{0x1E},{0x11},{0x11},{0x1E},{0x10},{0x10},{0x10}},
                new byte[,]{{0x0E},{0x11},{0x11},{0x11},{0x15},{0x12},{0x0D}},
                new byte[,]{{0x1E},{0x11},{0x11},{0x1E},{0x14},{0x12},{0x11}},
                new byte[,]{{0x0E},{0x11},{0x10},{0x0E},{0x01},{0x11},{0x0E}},
                new byte[,]{{0x1F},{0x04},{0x04},{0x04},{0x04},{0x04},{0x04}},
                new byte[,]{{0x11},{0x11},{0x11},{0x11},{0x11},{0x11},{0x0E}},
                new byte[,]{{0x11},{0x11},{0x11},{0x11},{0x11},{0x0A},{0x04}},
                new byte[,]{{0x11},{0x11},{0x11},{0x15},{0x15},{0x1B},{0x11}},
                new byte[,]{{0x11},{0x11},{0x0A},{0x04},{0x0A},{0x11},{0x11}},
                new byte[,]{{0x11},{0x11},{0x0A},{0x04},{0x04},{0x04},{0x04}},
                new byte[,]{{0x1F},{0x01},{0x02},{0x04},{0x08},{0x10},{0x1F}},
            };
            for (int i = 0; i < 26; i++)
            {
                int idx = 33 + i; // ASCII A=65 → idx 33
                int b2  = idx * 7;
                for (int r = 0; r < 7; r++)
                    d[b2 + r] = azGlyphs[i][r, 0];
            }
            // [ (59)
            G(59, 0x0E,0x08,0x08,0x08,0x08,0x08,0x0E);
            // \ (60)
            G(60, 0x10,0x08,0x08,0x04,0x02,0x02,0x01);
            // ] (61)
            G(61, 0x0E,0x02,0x02,0x02,0x02,0x02,0x0E);
            // ^ (62)
            G(62, 0x04,0x0A,0x11,0x00,0x00,0x00,0x00);
            // _ (63)
            G(63, 0x00,0x00,0x00,0x00,0x00,0x00,0x1F);
            // ` (64)
            G(64, 0x08,0x04,0x00,0x00,0x00,0x00,0x00);
            // a-z (65-90): use uppercase bitmaps (caps-only display for v1.0)
            for (int i = 0; i < 26; i++)
            {
                int srcB = (33 + i) * 7;
                int dstB = (65 + i) * 7;
                for (int r = 0; r < 7; r++) d[dstB + r] = d[srcB + r];
            }
            // { | } ~ (91-94)
            G(91, 0x02,0x04,0x04,0x08,0x04,0x04,0x02);
            G(92, 0x04,0x04,0x04,0x04,0x04,0x04,0x04);
            G(93, 0x08,0x04,0x04,0x02,0x04,0x04,0x08);
            G(94, 0x00,0x04,0x0A,0x11,0x00,0x00,0x00);
            G(95, 0,0,0,0,0,0,0); // DEL = blank
            return d;
        }

        // Font atlas layout: 96 chars, each 8×8 cell (5×7 glyph + padding)
        // Atlas: 8 chars per row × 12 rows = 96 chars → 64 wide × 96 tall, R8_UNORM
        private const int GlyphW = 5, GlyphH = 7, CellW = 8, CellH = 8;
        private const int AtlasCols = 8, AtlasRows = 12;
        private const int AtlasW = AtlasCols * CellW, AtlasH = AtlasRows * CellH;

        private void CreateFontAtlas()
        {
            // Build pixel data
            var pixels = new byte[AtlasW * AtlasH];
            for (int ci = 0; ci < 96; ci++)
            {
                int col = ci % AtlasCols, row = ci / AtlasCols;
                int bx  = col * CellW,    by = row * CellH;
                for (int py = 0; py < GlyphH; py++)
                {
                    byte bits = _fontBits[ci * 7 + py];
                    for (int px = 0; px < GlyphW; px++)
                    {
                        bool lit = (bits >> (4 - px) & 1) != 0;
                        pixels[(by + py) * AtlasW + (bx + px)] = lit ? (byte)255 : (byte)0;
                    }
                }
            }

            // Upload as VK_FORMAT_R8_UNORM
            var ici = new ImageCreateInfo
            {
                SType       = StructureType.ImageCreateInfo,
                ImageType   = ImageType.Type2D,
                Format      = Format.R8Unorm,
                Extent      = new Extent3D((uint)AtlasW, (uint)AtlasH, 1),
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

            CreateBuffer(pixels, BufferUsageFlags.TransferSrcBit,
                out var staging, out var stagingMem);

            var cb = _vk.BeginOneShot();
            TransitionImage(cb, img, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers
                    { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageExtent = new Extent3D((uint)AtlasW, (uint)AtlasH, 1),
            };
            _vk.Vk.CmdCopyBufferToImage(cb, staging, img,
                ImageLayout.TransferDstOptimal, 1, &region);
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
                Format   = Format.R8Unorm,
                SubresourceRange = new ImageSubresourceRange
                    { AspectMask = ImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            };
            ImageView view;
            _vk.Vk.CreateImageView(_vk.Device, &ivci, null, &view);

            _fontTex     = img;
            _fontTexMem  = mem;
            _fontTexView = view;

            // Allocate and write font descriptor set (reuse existing pool + layout)
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
            _fontDescSet = ds;

            var imgInfo = new DescriptorImageInfo
            {
                Sampler     = _sampler,
                ImageView   = _fontTexView,
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

        private void CreateUiVertexBuffer()
        {
            // Host-coherent, persistently mapped — eliminates per-draw MapMemory/UnmapMemory.
            var bci = new BufferCreateInfo
            {
                SType       = StructureType.BufferCreateInfo,
                Size        = UiVbCapacity,
                Usage       = BufferUsageFlags.VertexBufferBit,
                SharingMode = SharingMode.Exclusive,
            };
            Buffer b;
            _vk.Vk.CreateBuffer(_vk.Device, &bci, null, &b);

            MemoryRequirements req;
            _vk.Vk.GetBufferMemoryRequirements(_vk.Device, b, &req);
            var mai = new MemoryAllocateInfo
            {
                SType           = StructureType.MemoryAllocateInfo,
                AllocationSize  = req.Size,
                MemoryTypeIndex = _vk.FindMemoryType(req.MemoryTypeBits,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
            };
            DeviceMemory m;
            _vk.Vk.AllocateMemory(_vk.Device, &mai, null, &m);
            _vk.Vk.BindBufferMemory(_vk.Device, b, m, 0);

            void* ptr;
            _vk.Vk.MapMemory(_vk.Device, m, 0, UiVbCapacity, 0, &ptr);
            _uiMapped = ptr;

            _uiVb    = b;
            _uiVbMem = m;
        }

        private void LoadUIPipeline()
        {
            var vertSpv = LoadSpirV("ui.vert");
            var fragSpv = LoadSpirV("ui.frag");
            var vertMod = CreateShaderModule(vertSpv);
            var fragMod = CreateShaderModule(fragSpv);

            var pcRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                Size       = (uint)sizeof(UIPush),
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
            _uiLayout = pl;

            // UI vertex: vec3 pos + vec2 uv = 20 bytes per vertex
            var vbDesc = new VertexInputBindingDescription
                { Binding = 0, Stride = 5 * sizeof(float), InputRate = VertexInputRate.Vertex };
            var attrs = stackalloc VertexInputAttributeDescription[]
            {
                new() { Binding=0, Location=0, Format=Format.R32G32B32Sfloat, Offset=0  },
                new() { Binding=0, Location=1, Format=Format.R32G32Sfloat,    Offset=12 },
            };
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType                           = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount   = 1,
                PVertexBindingDescriptions      = &vbDesc,
                VertexAttributeDescriptionCount = 2,
                PVertexAttributeDescriptions    = attrs,
            };

            var ia  = new PipelineInputAssemblyStateCreateInfo
                { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var vps = new PipelineViewportStateCreateInfo
                { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var ras = new PipelineRasterizationStateCreateInfo
                { SType = StructureType.PipelineRasterizationStateCreateInfo,
                  PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None,
                  FrontFace = FrontFace.CounterClockwise, LineWidth = 1f };
            var ms2  = new PipelineMultisampleStateCreateInfo
                { SType = StructureType.PipelineMultisampleStateCreateInfo,
                  RasterizationSamples = SampleCountFlags.Count1Bit };
            var ds2  = new PipelineDepthStencilStateCreateInfo
                { SType = StructureType.PipelineDepthStencilStateCreateInfo,
                  DepthTestEnable = false, DepthWriteEnable = false };
            var ba   = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp        = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.Zero,
                AlphaBlendOp        = BlendOp.Add,
            };
            var blnd = new PipelineColorBlendStateCreateInfo
                { SType = StructureType.PipelineColorBlendStateCreateInfo,
                  AttachmentCount = 1, PAttachments = &ba };
            var dyn  = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            var dys  = new PipelineDynamicStateCreateInfo
                { SType = StructureType.PipelineDynamicStateCreateInfo,
                  DynamicStateCount = 2, PDynamicStates = dyn };

            Pipeline pipe;
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
                    SType=StructureType.GraphicsPipelineCreateInfo, StageCount=2, PStages=stages,
                    PVertexInputState=&vertexInput, PInputAssemblyState=&ia, PViewportState=&vps,
                    PRasterizationState=&ras, PMultisampleState=&ms2, PDepthStencilState=&ds2,
                    PColorBlendState=&blnd, PDynamicState=&dys,
                    Layout=_uiLayout, RenderPass=_swapchain.RenderPass, Subpass=0,
                };
                _vk.Vk.CreateGraphicsPipelines(_vk.Device, default, 1, &gpci, null, &pipe);
            }
            _uiPipeline = pipe;

            _vk.Vk.DestroyShaderModule(_vk.Device, vertMod, null);
            _vk.Vk.DestroyShaderModule(_vk.Device, fragMod, null);
        }

        // Converts ASCII char to (u, v) top-left corner in the font atlas (0..1 range).
        private static (float u, float v) GlyphUV(char c)
        {
            int idx = c >= 32 && c < 128 ? c - 32 : 0;
            int col = idx % AtlasCols, row = idx / AtlasCols;
            return (col * CellW / (float)AtlasW, row * CellH / (float)AtlasH);
        }

        // Emit two triangles (6 floats × 5 = 30 floats) for a screen-space quad.
        // Coords in pixels; z=0; uv from font atlas if texQuad=true, else 0..1.
        private static int EmitQuad(float[] buf, int at,
            float x, float y, float w, float h,
            float u0, float v0, float u1, float v1)
        {
            // Triangle 1: TL, BL, TR
            // Triangle 2: TR, BL, BR
            float[] q =
            {
                x,   y,   0, u0, v0,
                x,   y+h, 0, u0, v1,
                x+w, y,   0, u1, v0,
                x+w, y,   0, u1, v0,
                x,   y+h, 0, u0, v1,
                x+w, y+h, 0, u1, v1,
            };
            Array.Copy(q, 0, buf, at, q.Length);
            return at + q.Length;
        }

        private void DrawUI(CommandBuffer cmd, UIManager ui, float w, float h, Matrix4x4 worldVp)
        {
            _uiCursor = 0; // reset per-frame write cursor

            // NDC ortho: maps pixel coords (0..w, 0..h) → NDC (-1..1, -1..1)
            var proj = new Matrix4x4(
                2f/w, 0,    0, 0,
                0,    2f/h, 0, 0,
                0,    0,    1, 0,
               -1f,  -1f,  0, 1);

            _vk.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _uiPipeline);
            var vp = new Viewport(0, 0, w, h, 0, 1);
            var sc = new Rect2D(default, new Extent2D((uint)w, (uint)h));
            _vk.Vk.CmdSetViewport(cmd, 0, 1, &vp);
            _vk.Vk.CmdSetScissor(cmd,  0, 1, &sc);

            // Avatar name labels float above heads in world space
            DrawAvatarLabels(cmd, proj, w, h, worldVp);

            if (ui.ActivePanel == UIPanel.Login)
                DrawLoginPanel(cmd, proj, ui, w, h);

            DrawChatOverlay(cmd, proj, ui, w, h);

            if (ui.NotificationVisible)
                DrawNotification(cmd, proj, ui, w, h);

            if (ui.DialogVisible)
                DrawDialog(cmd, proj, ui, w, h);

            if (ui.ActivePanel != UIPanel.Login)
                DrawMinimap(cmd, proj, w, h);
        }

        private static readonly Vector4 ColMinimapBg  = new(0.05f, 0.07f, 0.12f, 0.78f);
        private static readonly Vector4 ColMinimapBdr  = new(0.35f, 0.55f, 0.80f, 0.90f);
        private static readonly Vector4 ColMinimapSelf = new(1.00f, 1.00f, 1.00f, 1.00f);
        private static readonly Vector4 ColMinimapAv   = new(1.00f, 0.92f, 0.30f, 0.95f);
        private static readonly Vector4 ColMinimapObj  = new(0.55f, 0.55f, 0.65f, 0.70f);

        private void DrawMinimap(CommandBuffer cmd, Matrix4x4 proj, float w, float h)
        {
            const float MapSize  = 120f;  // px
            const float DotR     = 3.5f;  // dot radius in px
            const float Range    = 64f;   // world units covered by half the map

            float mx = w - MapSize - 12f; // bottom-right corner with 12px margin
            float my = 12f;

            // Background panel
            DrawSolidQuad(cmd, proj, mx, my, MapSize, MapSize, ColMinimapBg);
            // Border (four 1px-wide lines)
            DrawSolidQuad(cmd, proj, mx,             my,             MapSize,  1f,     ColMinimapBdr);
            DrawSolidQuad(cmd, proj, mx,             my + MapSize,   MapSize,  1f,     ColMinimapBdr);
            DrawSolidQuad(cmd, proj, mx,             my,             1f,       MapSize, ColMinimapBdr);
            DrawSolidQuad(cmd, proj, mx + MapSize,   my,             1f,       MapSize, ColMinimapBdr);

            var local = SLApplication.Instance?.LocalAvatar;
            Vector3 center = local?.Position ?? Vector3.Zero;

            float halfMap = MapSize * 0.5f;

            // Helper: world pos → minimap pixel
            Vector2 WorldToPx(Vector3 wp)
            {
                float dx = wp.X - center.X;
                float dz = wp.Z - center.Z;
                float px = mx + halfMap + (dx / Range) * halfMap;
                float py = my + halfMap - (dz / Range) * halfMap; // flip Z: +Z = up on map
                return new Vector2(px, py);
            }

            // Draw object dots (grey) — limit to 200 nearest to avoid noise
            int objDrawn = 0;
            foreach (var (_, obj) in _objects.Objects)
            {
                if (obj.IsAvatar) continue;
                float dist = (obj.Position - center).LengthSquared();
                if (dist > Range * Range) continue;
                if (++objDrawn > 200) break;
                var p = WorldToPx(obj.Position);
                DrawSolidQuad(cmd, proj, p.X - DotR * 0.7f, p.Y - DotR * 0.7f,
                              DotR * 1.4f, DotR * 1.4f, ColMinimapObj);
            }

            // Draw remote avatar dots (yellow)
            foreach (var (_, av) in _avatars.Avatars)
            {
                float dist = (av.Position - center).LengthSquared();
                if (dist > Range * Range) continue;
                var p = WorldToPx(av.Position);
                DrawSolidQuad(cmd, proj, p.X - DotR, p.Y - DotR,
                              DotR * 2f, DotR * 2f, ColMinimapAv);
            }

            // Local avatar dot (white) — always centred
            DrawSolidQuad(cmd, proj, mx + halfMap - DotR, my + halfMap - DotR,
                          DotR * 2f, DotR * 2f, ColMinimapSelf);

            // "MAP" label in top-left of the panel
            DrawText(cmd, proj, "MAP", mx + 4f, my + MapSize - CellH - 4f, 0.9f,
                     new Vector4(0.65f, 0.80f, 1.00f, 0.85f));
        }

        private void DrawAvatarLabels(CommandBuffer cmd, Matrix4x4 proj2D,
            float w, float h, Matrix4x4 worldVp)
        {
            foreach (var (_, av) in _avatars.Avatars)
            {
                if (string.IsNullOrEmpty(av.Name)) continue;

                // Project a point 2.2m above the avatar's root position
                var head   = new Vector4(av.Position + new Vector3(0, 2.2f, 0), 1f);
                var clip   = Vector4.Transform(head, worldVp);
                if (clip.W <= 0.01f) continue; // behind camera

                float ndcX = clip.X / clip.W;
                float ndcY = clip.Y / clip.W;
                if (ndcX < -1.1f || ndcX > 1.1f || ndcY < -1.1f || ndcY > 1.1f) continue;

                float px = (ndcX * 0.5f + 0.5f) * w;
                float py = (ndcY * 0.5f + 0.5f) * h;

                const float scale = 1.5f;
                string name  = av.Name.Length > 22 ? av.Name[..22] : av.Name;
                float  textW = name.Length * CellW * scale;
                DrawText(cmd, proj2D, name,
                    px - textW * 0.5f, py - CellH * scale * 0.5f, scale,
                    new Vector4(1f, 1f, 0.5f, 0.92f));
            }
        }

        // ── UI draw helpers ───────────────────────────────────────────────────

        private void DrawSolidQuad(CommandBuffer cmd, Matrix4x4 proj,
            float x, float y, float qw, float qh, Vector4 color)
        {
            var verts = new float[6 * 5];
            EmitQuad(verts, 0, x, y, qw, qh, 0, 0, 1, 1);
            UploadAndDrawUI(cmd, proj, verts, color, _whiteDescSet,
                scaleU: 1, scaleV: 1, offU: 0, offV: 0);
        }

        private void DrawText(CommandBuffer cmd, Matrix4x4 proj,
            string text, float x, float y, float scale, Vector4 color)
        {
            float glyphScaleX = CellW * scale;
            float glyphScaleH = CellH * scale;
            float cellUW = CellW / (float)AtlasW;
            float cellVH = CellH / (float)AtlasH;

            var verts = new float[text.Length * 6 * 5];
            int at = 0;
            float cx = x;
            foreach (char c in text.ToUpperInvariant())
            {
                if (c == ' ') { cx += glyphScaleX; continue; }
                var (u0, v0) = GlyphUV(c);
                at = EmitQuad(verts, at, cx, y, glyphScaleX, glyphScaleH,
                              u0, v0, u0 + cellUW, v0 + cellVH);
                cx += glyphScaleX;
            }
            if (at == 0) return;

            UploadAndDrawUI(cmd, proj, verts[..at], color, _fontDescSet,
                scaleU: 1, scaleV: 1, offU: 0, offV: 0);
        }

        private void UploadAndDrawUI(CommandBuffer cmd, Matrix4x4 proj,
            float[] verts, Vector4 color, DescriptorSet ds,
            float scaleU, float scaleV, float offU, float offV)
        {
            if (verts.Length == 0 || _uiMapped == null) return;
            int byteOffset = _uiCursor * 5 * sizeof(float);
            int byteLen    = verts.Length * sizeof(float);
            if (byteOffset + byteLen > UiVbCapacity) return;

            // Direct copy to persistently-mapped host-coherent memory — no Map/Unmap.
            new Span<float>((float*)_uiMapped + _uiCursor * 5, verts.Length)
                .TryCopyFrom(verts);

            ulong byteOffsetUl = (ulong)byteOffset;
            _vk.Vk.CmdBindVertexBuffers(cmd, 0, 1, &_uiVb, &byteOffsetUl);

            var dsLocal = ds;
            _vk.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics,
                _uiLayout, 0, 1, &dsLocal, 0, null);

            var push = new UIPush
            {
                Transform = proj,
                Color     = color,
                UvScaleX  = scaleU, UvScaleY = scaleV,
                UvOffsetX = offU,   UvOffsetY = offV,
            };
            _vk.Vk.CmdPushConstants(cmd, _uiLayout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0, (uint)sizeof(UIPush), &push);

            _vk.Vk.CmdDraw(cmd, (uint)(verts.Length / 5), 1, 0, 0);
            _uiCursor += verts.Length / 5;
        }

        private void DrawLoginPanel(CommandBuffer cmd, Matrix4x4 proj,
            UIManager ui, float w, float h)
        {
            float panW = w * 0.5f, panH = h * 0.4f;
            float panX = (w - panW) * 0.5f, panY = (h - panH) * 0.5f;

            // Dark background panel
            DrawSolidQuad(cmd, proj, panX, panY, panW, panH,
                new Vector4(0.05f, 0.05f, 0.08f, 0.90f));

            // Title
            float scale = 3.5f;
            float titleW = "SECOND LIFE".Length * CellW * scale;
            DrawText(cmd, proj, "SECOND LIFE",
                panX + (panW - titleW) * 0.5f, panY + 20, scale,
                new Vector4(0.85f, 0.85f, 1.0f, 1.0f));

            // Status line
            float scale2  = 2f;
            string status = ui.LoginStatus;
            if (ui.LoginFailed) status = "ERROR: " + ui.LoginError;
            float statusW = status.Length * CellW * scale2;
            var   statusC = ui.LoginFailed
                ? new Vector4(1f, 0.3f, 0.3f, 1f)
                : new Vector4(0.6f, 0.9f, 0.6f, 1f);
            DrawText(cmd, proj, status,
                panX + (panW - statusW) * 0.5f, panY + panH - 50, scale2, statusC);
        }

        private void DrawChatOverlay(CommandBuffer cmd, Matrix4x4 proj,
            UIManager ui, float w, float h)
        {
            var chat = ui.RecentChat;
            if (chat.Count == 0) return;

            float scale = 1.8f;
            float lineH = CellH * scale + 2f;
            float chatH = chat.Count * lineH + 8f;
            float chatW = w * 0.38f;
            float chatX = 12f, chatY = h - chatH - 12f;

            // Semi-transparent background
            DrawSolidQuad(cmd, proj, chatX - 4, chatY - 4, chatW + 8, chatH + 8,
                new Vector4(0, 0, 0, 0.55f));

            for (int i = 0; i < chat.Count; i++)
            {
                var (name, msg) = chat[i];
                string line = name + ": " + msg;
                if (line.Length > 38) line = line[..37] + "…";
                DrawText(cmd, proj, line, chatX, chatY + i * lineH, scale,
                    new Vector4(0.9f, 0.9f, 0.9f, 1f));
            }
        }

        private void DrawNotification(CommandBuffer cmd, Matrix4x4 proj,
            UIManager ui, float w, float h)
        {
            float scale = 2.2f;
            string title = ui.NotificationTitle;
            string body  = ui.NotificationBody;
            float boxW   = Math.Max(title.Length, body.Length) * CellW * scale + 24f;
            float boxH   = CellH * scale * 2 + 20f;
            float boxX   = (w - boxW) * 0.5f;
            float boxY   = 30f;

            DrawSolidQuad(cmd, proj, boxX, boxY, boxW, boxH,
                new Vector4(0.1f, 0.4f, 0.1f, 0.88f));

            DrawText(cmd, proj, title, boxX + 12, boxY + 6, scale,
                new Vector4(1f, 1f, 0.6f, 1f));
            DrawText(cmd, proj, body, boxX + 12, boxY + 8 + CellH * scale, scale * 0.8f,
                new Vector4(0.9f, 0.9f, 0.9f, 1f));
        }

        private void DrawDialog(CommandBuffer cmd, Matrix4x4 proj,
            UIManager ui, float w, float h)
        {
            float scale = 2f;
            float dlgW  = w * 0.55f, dlgH = h * 0.45f;
            float dlgX  = (w - dlgW) * 0.5f, dlgY = (h - dlgH) * 0.5f;

            DrawSolidQuad(cmd, proj, dlgX, dlgY, dlgW, dlgH,
                new Vector4(0.08f, 0.08f, 0.12f, 0.94f));

            DrawText(cmd, proj, ui.DialogTitle, dlgX + 12, dlgY + 12, scale,
                new Vector4(0.85f, 0.85f, 1f, 1f));

            // Wrap message into ~30-char lines
            string msg  = ui.DialogMessage;
            int    maxW = (int)(dlgW / (CellW * scale * 0.8f));
            float  ly   = dlgY + 12 + CellH * scale + 8;
            for (int i = 0; i < msg.Length; i += maxW)
            {
                string chunk = msg.Substring(i, Math.Min(maxW, msg.Length - i));
                DrawText(cmd, proj, chunk, dlgX + 12, ly, scale * 0.8f,
                    new Vector4(0.8f, 0.8f, 0.8f, 1f));
                ly += CellH * scale * 0.8f + 2;
                if (ly > dlgY + dlgH - 40) break;
            }

            // Buttons row
            float bx = dlgX + 12, by = dlgY + dlgH - 32;
            foreach (string btn in ui.DialogButtons)
            {
                float bw = btn.Length * CellW * scale + 16f;
                DrawSolidQuad(cmd, proj, bx, by, bw, 26f,
                    new Vector4(0.2f, 0.2f, 0.5f, 0.9f));
                DrawText(cmd, proj, btn, bx + 8, by + 4, scale,
                    new Vector4(1f, 1f, 1f, 1f));
                bx += bw + 8;
                if (bx > dlgX + dlgW - 12) break;
            }
        }

        // ── Per-prim texture streaming ────────────────────────────────────────

        // Call once per frame, BEFORE starting the render pass.
        // Checks AssetManager for any new textures ready to upload and uploads
        // up to 2 per frame so individual uploads don't stall the frame loop.
        public void FlushTextureUploads()
        {
            if (_assets == null) return;
            int uploaded = 0;
            foreach (var (_, obj) in _objects.Objects)
            {
                if (obj.TextureId == Guid.Empty) continue;
                if (_texCache.ContainsKey(obj.TextureId)) continue;

                if (!_assets.TryGet(obj.TextureId, out var td))
                {
                    _ = _assets.RequestAsync(obj.TextureId); // start network fetch
                    continue;
                }

                if (td == null || td.Rgba.Length == 0) continue;
                UploadTexture(obj.TextureId, td);
                if (++uploaded >= 2) break;
            }
        }

        private void UploadTexture(Guid id, TextureData td)
        {
            uint w = (uint)td.Width, h = (uint)td.Height;

            var ici = new ImageCreateInfo
            {
                SType         = StructureType.ImageCreateInfo,
                ImageType     = ImageType.Type2D,
                Format        = Format.R8G8B8A8Srgb,
                Extent        = new Extent3D(w, h, 1),
                MipLevels     = 1,
                ArrayLayers   = 1,
                Samples       = SampleCountFlags.Count1Bit,
                Tiling        = ImageTiling.Optimal,
                Usage         = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
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

            CreateBuffer(td.Rgba, BufferUsageFlags.TransferSrcBit, out var staging, out var stagMem);

            var cb = _vk.BeginOneShot();
            TransitionImage(cb, img, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers
                    { AspectMask = ImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageExtent = new Extent3D(w, h, 1),
            };
            _vk.Vk.CmdCopyBufferToImage(cb, staging, img, ImageLayout.TransferDstOptimal, 1, &region);
            TransitionImage(cb, img, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            _vk.EndOneShot(cb);

            _vk.Vk.DestroyBuffer(_vk.Device, staging, null);
            _vk.Vk.FreeMemory(_vk.Device, stagMem, null);

            var ivci = new ImageViewCreateInfo
            {
                SType    = StructureType.ImageViewCreateInfo,
                Image    = img,
                ViewType = ImageViewType.Type2D,
                Format   = Format.R8G8B8A8Srgb,
                SubresourceRange = new ImageSubresourceRange
                    { AspectMask = ImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            };
            ImageView view;
            _vk.Vk.CreateImageView(_vk.Device, &ivci, null, &view);

            var layout = _descLayout;
            var ai = new DescriptorSetAllocateInfo
            {
                SType              = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool     = _descPool,
                DescriptorSetCount = 1,
                PSetLayouts        = &layout,
            };
            DescriptorSet ds;
            if (_vk.Vk.AllocateDescriptorSets(_vk.Device, &ai, &ds) != Result.Success)
            {
                // Pool exhausted — destroy the image and bail
                _vk.Vk.DestroyImageView(_vk.Device, view, null);
                _vk.Vk.DestroyImage(_vk.Device, img, null);
                _vk.Vk.FreeMemory(_vk.Device, mem, null);
                return;
            }

            var imgInfo = new DescriptorImageInfo
            {
                Sampler     = _sampler,
                ImageView   = view,
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

            _texCache[id] = new TexEntry { Img = img, Mem = mem, View = view, Desc = ds };
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
                DescriptorCount = 512, // white + font + up to ~510 in-world textures
            };
            var pci = new DescriptorPoolCreateInfo
            {
                SType         = StructureType.DescriptorPoolCreateInfo,
                MaxSets       = 512,
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

            DestroyBuffer(ref _terrainVb,    ref _terrainVbMem);
            DestroyBuffer(ref _terrainIb,    ref _terrainIbMem);
            DestroyBuffer(ref _cubeVb,       ref _cubeVbMem);
            DestroyBuffer(ref _cubeIb,       ref _cubeIbMem);
            DestroyBuffer(ref _cylinderVb,   ref _cylinderVbMem);
            DestroyBuffer(ref _cylinderIb,   ref _cylinderIbMem);
            DestroyBuffer(ref _sphereVb,     ref _sphereVbMem);
            DestroyBuffer(ref _sphereIb,     ref _sphereIbMem);
            DestroyBuffer(ref _torusVb,      ref _torusVbMem);
            DestroyBuffer(ref _torusIb,      ref _torusIbMem);
            if (_uiMapped != null && _uiVbMem.Handle != 0)
            {
                _vk.Vk.UnmapMemory(_vk.Device, _uiVbMem);
                _uiMapped = null;
            }
            DestroyBuffer(ref _uiVb,       ref _uiVbMem);

            // Texture cache
            foreach (var (_, e) in _texCache)
            {
                if (e.View.Handle != 0) _vk.Vk.DestroyImageView(_vk.Device, e.View, null);
                if (e.Img.Handle  != 0) _vk.Vk.DestroyImage(_vk.Device, e.Img, null);
                if (e.Mem.Handle  != 0) _vk.Vk.FreeMemory(_vk.Device, e.Mem, null);
            }
            _texCache.Clear();

            if (_uiPipeline.Handle    != 0) _vk.Vk.DestroyPipeline(_vk.Device, _uiPipeline, null);
            if (_uiLayout.Handle      != 0) _vk.Vk.DestroyPipelineLayout(_vk.Device, _uiLayout, null);
            if (_fontTexView.Handle   != 0) _vk.Vk.DestroyImageView(_vk.Device, _fontTexView, null);
            if (_fontTex.Handle       != 0) _vk.Vk.DestroyImage(_vk.Device, _fontTex, null);
            if (_fontTexMem.Handle    != 0) _vk.Vk.FreeMemory(_vk.Device, _fontTexMem, null);

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
