using System;
using System.Collections;
using OpenMetaverse;
using SLQuest.Assets;
using SLQuest.Core;
using SLQuest.Rendering;
using UnityEngine;

namespace SLQuest.World
{
    /// <summary>
    /// Per-prim MonoBehaviour that owns the visual representation of one Second Life primitive.
    /// Handles geometry generation from prim parameters, texture application, and physics.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public sealed class SLPrimitive : MonoBehaviour
    {
        public uint LocalId { get; private set; }
        public Guid FullId  { get; private set; }
        public Primitive Prim { get; private set; }

        private MeshFilter            _meshFilter;
        private MeshRenderer          _meshRenderer;
        private MeshCollider          _meshCollider;
        private AssetManager          _assets;
        private RegionManager         _region;
        private RenderMaterialsManager _renderMaterials;

        // Physics interpolation
        private Vector3    _velocity;
        private Vector3    _angularVelocity;

        private LODSystem _lod;

        private void Awake()
        {
            _meshFilter      = GetComponent<MeshFilter>();
            _meshRenderer    = GetComponent<MeshRenderer>();
            _meshCollider    = GetComponent<MeshCollider>();
            _renderMaterials = SLApplication.Instance?.RenderMaterials
                               ?? FindObjectOfType<RenderMaterialsManager>();
            _lod             = SLApplication.Instance?.LOD ?? FindObjectOfType<LODSystem>();
        }

        private void OnEnable()  => _lod?.Register(this);
        private void OnDisable() => _lod?.Unregister(this);
        private void OnDestroy() => _lod?.Unregister(this);

        public void Initialise(Primitive prim, Simulator sim,
                               RegionManager region, AssetManager assets)
        {
            LocalId  = prim.LocalID;
            FullId   = prim.ID;
            Prim     = prim;
            _region  = region;
            _assets  = assets;

            RebuildGeometry(prim);
            StartCoroutine(ApplyTextures(prim));
        }

        public void ApplyDataBlock(ObjectDataBlockUpdateEventArgs e)
        {
            Prim = e.Prim;
            RebuildGeometry(e.Prim);
        }

        public void ApplyVelocity(OpenMetaverse.Vector3 vel, OpenMetaverse.Vector3 angVel)
        {
            _velocity        = new Vector3(vel.X, vel.Z, vel.Y);
            _angularVelocity = new Vector3(angVel.X, angVel.Z, angVel.Y);
        }

        private void Update()
        {
            // Dead-reckoning: physics objects continue moving between updates
            if (_velocity.sqrMagnitude > 0.0001f)
                transform.position += _velocity * Time.deltaTime;

            if (_angularVelocity.sqrMagnitude > 0.0001f)
                transform.rotation *= Quaternion.Euler(_angularVelocity * (Mathf.Rad2Deg * Time.deltaTime));
        }

        // ── Geometry ──────────────────────────────────────────────────────────

        private void RebuildGeometry(Primitive prim)
        {
            Mesh mesh;

            if (prim.Type == PrimType.Mesh && prim.Sculpt?.SculptTexture != UUID.Zero)
            {
                // Mesh prim — defer to async loader
                StartCoroutine(LoadMesh(prim.Sculpt.SculptTexture));
                return;
            }

            // Generate Unity mesh from prim parameters using our procedural builder
            mesh = PrimMeshBuilder.Build(prim);
            SetMesh(mesh);
        }

        private IEnumerator LoadMesh(UUID meshId)
        {
            var handle = _assets.RequestMesh(meshId);
            yield return new WaitUntil(() => handle.IsReady);
            if (handle.Mesh != null)
                SetMesh(handle.Mesh);
        }

        private void SetMesh(Mesh mesh)
        {
            _meshFilter.sharedMesh   = mesh;
            _meshCollider.sharedMesh = mesh;
        }

        // ── Textures / PBSM / Media ───────────────────────────────────────────

        private IEnumerator ApplyTextures(Primitive prim)
        {
            if (prim.Textures == null) yield break;

            var te        = prim.Textures;
            int faceCount = prim.Textures.FaceTextures.Length;
            var materials = new Material[faceCount];

            for (int f = 0; f < faceCount; f++)
            {
                var face = te.FaceTextures[f] ?? te.DefaultTexture;
                if (face == null) { materials[f] = MaterialConverter.DefaultMaterial(); continue; }

                var texHandle = _assets.RequestTexture(face.TextureID);
                yield return new WaitUntil(() => texHandle.IsReady);

                materials[f] = MaterialConverter.FromFace(face, texHandle.Texture);

                // Attach media surface if this face has media
                if (face.MediaFlags)
                    AttachMediaSurface(f);

                // Request PBSM data if the face has a RenderMaterial assigned
                if (face.RenderMaterialID != UUID.Zero && _renderMaterials != null)
                    RequestPBSM(f, face.RenderMaterialID, materials);
            }

            _meshRenderer.materials = materials;
        }

        private void AttachMediaSurface(int faceIndex)
        {
            // Avoid duplicates
            var existing = GetComponents<MediaSurface>();
            foreach (var ms in existing)
                if (ms.FaceIndex == faceIndex) return;

            var surf = gameObject.AddComponent<MediaSurface>();
            surf.FaceIndex = faceIndex;
        }

        private void RequestPBSM(int faceIndex, UUID renderMatId, Material[] materials)
        {
            _renderMaterials.RequestMaterial(renderMatId, pbsm =>
            {
                // Fetch normal map, then apply
                if (pbsm.NormMapId != UUID.Zero && _assets != null)
                {
                    var normHandle = _assets.RequestTexture(pbsm.NormMapId);
                    StartCoroutine(ApplyPBSMWhenReady(faceIndex, pbsm, normHandle));
                }
                else
                {
                    MaterialConverter.ApplyPBSM(materials[faceIndex], pbsm, null, null);
                    _meshRenderer.materials = materials;
                }
            });
        }

        private IEnumerator ApplyPBSMWhenReady(int faceIndex, PBSMaterial pbsm,
                                                TextureHandle normHandle)
        {
            yield return new WaitUntil(() => normHandle.IsReady);

            // Fetch optional specular map
            TextureHandle specHandle = null;
            if (pbsm.SpecMapId != UUID.Zero)
            {
                specHandle = _assets.RequestTexture(pbsm.SpecMapId);
                yield return new WaitUntil(() => specHandle.IsReady);
            }

            if (faceIndex < _meshRenderer.materials.Length)
            {
                MaterialConverter.ApplyPBSM(
                    _meshRenderer.materials[faceIndex],
                    pbsm,
                    normHandle.Texture,
                    specHandle?.Texture);
            }

            EventBus.Publish(new RenderMaterialReadyEvent(LocalId));
        }
    }
}
