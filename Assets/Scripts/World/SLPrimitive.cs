using System;
using System.Collections;
using OpenMetaverse;
using SLQuest.Assets;
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

        private MeshFilter   _meshFilter;
        private MeshRenderer _meshRenderer;
        private MeshCollider _meshCollider;
        private AssetManager _assets;
        private RegionManager _region;

        // Physics interpolation
        private Vector3    _velocity;
        private Vector3    _angularVelocity;

        private void Awake()
        {
            _meshFilter   = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();
            _meshCollider = GetComponent<MeshCollider>();
        }

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

        // ── Textures ──────────────────────────────────────────────────────────

        private IEnumerator ApplyTextures(Primitive prim)
        {
            if (prim.Textures == null) yield break;

            var te = prim.Textures;
            var materials = new Material[prim.Textures.FaceTextures.Length];

            for (int f = 0; f < prim.Textures.FaceTextures.Length; f++)
            {
                var face = te.FaceTextures[f] ?? te.DefaultTexture;
                if (face == null) { materials[f] = MaterialConverter.DefaultMaterial(); continue; }

                var texHandle = _assets.RequestTexture(face.TextureID);
                yield return new WaitUntil(() => texHandle.IsReady);

                materials[f] = MaterialConverter.FromFace(face, texHandle.Texture);
            }

            _meshRenderer.materials = materials;
        }
    }
}
