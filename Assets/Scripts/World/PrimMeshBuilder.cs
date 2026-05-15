using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.Rendering;
using UnityEngine;

namespace SLQuest.World
{
    /// <summary>
    /// Converts an OpenMetaverse <see cref="Primitive"/> into a Unity <see cref="Mesh"/>
    /// using libopenmetaverse's built-in mesh renderer.
    /// </summary>
    public static class PrimMeshBuilder
    {
        // SimpleRenderer is libopenmetaverse's bundled CPU tessellator
        private static readonly SimpleRenderer _renderer = new();

        public static Mesh Build(Primitive prim)
        {
            try
            {
                var faceted = _renderer.GenerateFacetedMesh(
                    prim, prim.Sculpt?.SculptType ?? SculptType.None,
                    DetailLevel.High);

                return FacetedToUnityMesh(faceted);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PrimMesh] Failed for {prim.LocalID}: {ex.Message}");
                return BuildBox(prim);
            }
        }

        private static Mesh FacetedToUnityMesh(FacetedMesh faceted)
        {
            var verts  = new List<Vector3>();
            var norms  = new List<Vector3>();
            var uvs    = new List<Vector2>();
            var subMeshTris = new List<List<int>>();

            foreach (var face in faceted.Faces)
            {
                int baseIdx = verts.Count;
                var tris = new List<int>();

                foreach (var vert in face.Vertices)
                {
                    verts.Add(new Vector3(vert.Position.X, vert.Position.Z, vert.Position.Y));
                    norms.Add(new Vector3(vert.Normal.X,   vert.Normal.Z,   vert.Normal.Y));
                    uvs.Add(new Vector2(vert.TexCoord.X,   vert.TexCoord.Y));
                }

                for (int i = 0; i < face.Indices.Count; i += 3)
                {
                    // Reverse winding for Unity's CCW front-face convention
                    tris.Add(baseIdx + face.Indices[i]);
                    tris.Add(baseIdx + face.Indices[i + 2]);
                    tris.Add(baseIdx + face.Indices[i + 1]);
                }

                subMeshTris.Add(tris);
            }

            var mesh = new Mesh { name = "prim" };
            mesh.indexFormat = verts.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = subMeshTris.Count;

            for (int s = 0; s < subMeshTris.Count; s++)
                mesh.SetTriangles(subMeshTris[s], s);

            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildBox(Primitive prim)
        {
            // Fallback 1×1×1 unit cube — Unity's default cube layout
            var mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            return mesh;
        }
    }
}
