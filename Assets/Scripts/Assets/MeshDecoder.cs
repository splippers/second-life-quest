using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace SLQuest.Assets
{
    /// <summary>
    /// Decodes Second Life's LLMesh binary format into Unity meshes.
    ///
    /// LLMesh structure (LLSD binary):
    ///   - Header map with LOD offsets {"high_lod": {offset, size}, ...}
    ///   - Per-LOD zlib-compressed binary blobs
    ///   - Each LOD blob: submesh array of {Position, Normal, TexCoord0,
    ///     Weights, JointNames, TriangleList} binary blocks
    /// </summary>
    public static class MeshDecoder
    {
        private const int LOD_HIGH    = 0;
        private const int LOD_MEDIUM  = 1;
        private const int LOD_LOW     = 2;
        private const int LOD_LOWEST  = 3;

        public static Mesh Decode(byte[] data, string name)
        {
            // Parse LLSD binary header to find the high LOD block
            var header = ParseHeader(data);
            if (!header.TryGetValue("high_lod", out var lodEntry))
                throw new InvalidDataException("No high_lod in mesh header");

            int offset = (int)lodEntry.offset;
            int size   = (int)lodEntry.size;

            byte[] compressed = new byte[size];
            Array.Copy(data, offset, compressed, 0, size);

            byte[] raw = Inflate(compressed);
            return ParseLOD(raw, name);
        }

        // ── Header parsing ────────────────────────────────────────────────────

        private struct LodEntry { public long offset; public long size; }

        private static Dictionary<string, LodEntry> ParseHeader(byte[] data)
        {
            // The header is an LLSD map: version int, then repeating key/offset/size triples
            // Version 1 header: 4-byte int version, then LLSD-binary-encoded map
            var result = new Dictionary<string, LodEntry>();

            using var ms  = new MemoryStream(data);
            using var br  = new BinaryReader(ms);

            int version = br.ReadInt32();
            if (version != 1)
                throw new NotSupportedException($"Unsupported LLMesh version {version}");

            // Read LLSD binary map
            int headerSize = br.ReadInt32();
            byte[] headerBytes = br.ReadBytes(headerSize);

            // Minimal LLSD-binary parser for the header map (map of maps)
            ParseLLSDBinaryHeader(headerBytes, result);
            return result;
        }

        private static void ParseLLSDBinaryHeader(byte[] data, Dictionary<string, LodEntry> result)
        {
            // LLSD binary format: type byte followed by value
            // Map: '{' count key-value-pairs '}'
            // We parse just enough for the LOD entries we need
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            ParseLLSDMap(br, result);
        }

        private static void ParseLLSDMap(BinaryReader br, Dictionary<string, LodEntry> result)
        {
            byte type = br.ReadByte();
            if (type != (byte)'{') return;

            int count = (int)ReadLLSDInt(br);
            for (int i = 0; i < count; i++)
            {
                string key = ReadLLSDKey(br);
                byte valType = br.ReadByte();

                if (valType == (byte)'{')
                {
                    // Nested map — parse offset/size
                    var entry = new LodEntry();
                    int innerCount = (int)ReadLLSDInt(br);
                    for (int j = 0; j < innerCount; j++)
                    {
                        string innerKey = ReadLLSDKey(br);
                        long   val      = ReadLLSDInteger(br);
                        if (innerKey == "offset") entry.offset = val;
                        if (innerKey == "size")   entry.size   = val;
                    }
                    br.ReadByte(); // closing '}'
                    result[key] = entry;
                }
                else
                {
                    // Skip unknown value types
                    SkipLLSDValue(br, valType);
                }
            }
            // closing '}'
            if (br.BaseStream.Position < br.BaseStream.Length)
                br.ReadByte();
        }

        // ── LOD parsing ────────────────────────────────────────────────────────

        private static Mesh ParseLOD(byte[] raw, string name)
        {
            // LOD data: LLSD array of submesh maps
            using var ms = new MemoryStream(raw);
            using var br = new BinaryReader(ms);

            byte type = br.ReadByte();
            if (type != (byte)'[')
                throw new InvalidDataException("Expected LLSD array in LOD data");

            int submeshCount = (int)ReadLLSDInt(br);

            var verts  = new List<Vector3>();
            var norms  = new List<Vector3>();
            var uvs    = new List<Vector2>();
            var subTris = new List<List<int>>();

            for (int sm = 0; sm < submeshCount; sm++)
            {
                br.ReadByte(); // '{'
                int fieldCount = (int)ReadLLSDInt(br);

                byte[] positions = null, normals = null, texCoords = null, indices = null;
                float posScale = 1f;
                Vector3 posOffset = Vector3.zero;

                for (int f = 0; f < fieldCount; f++)
                {
                    string key = ReadLLSDKey(br);
                    byte valType = br.ReadByte();

                    switch (key)
                    {
                        case "Position":
                            positions = ReadLLSDBinary(br, valType);
                            break;
                        case "Normal":
                            normals = ReadLLSDBinary(br, valType);
                            break;
                        case "TexCoord0":
                            texCoords = ReadLLSDBinary(br, valType);
                            break;
                        case "TriangleList":
                            indices = ReadLLSDBinary(br, valType);
                            break;
                        case "PositionDomain":
                            // max/min range for dequantisation — read nested map
                            ParsePositionDomain(br, out posScale, out posOffset);
                            break;
                        default:
                            SkipLLSDValue(br, valType);
                            break;
                    }
                }
                br.ReadByte(); // '}'

                if (positions == null || indices == null) continue;

                int baseVert = verts.Count;
                int vertCount = positions.Length / 6; // 3 × uint16

                for (int v = 0; v < vertCount; v++)
                {
                    // Dequantise: uint16 → float in [-0.5, 0.5] × posScale + posOffset
                    float x = (BitConverter.ToUInt16(positions, v * 6 + 0) / 65535f - 0.5f) * posScale + posOffset.x;
                    float y = (BitConverter.ToUInt16(positions, v * 6 + 2) / 65535f - 0.5f) * posScale + posOffset.y;
                    float z = (BitConverter.ToUInt16(positions, v * 6 + 4) / 65535f - 0.5f) * posScale + posOffset.z;
                    verts.Add(new Vector3(x, z, y)); // SL→Unity axis swap

                    if (normals != null && normals.Length >= (v + 1) * 6)
                    {
                        float nx = BitConverter.ToUInt16(normals, v * 6 + 0) / 65535f * 2f - 1f;
                        float ny = BitConverter.ToUInt16(normals, v * 6 + 2) / 65535f * 2f - 1f;
                        float nz = BitConverter.ToUInt16(normals, v * 6 + 4) / 65535f * 2f - 1f;
                        norms.Add(new Vector3(nx, nz, ny));
                    }
                    else
                    {
                        norms.Add(Vector3.up);
                    }

                    if (texCoords != null && texCoords.Length >= (v + 1) * 4)
                    {
                        float u = BitConverter.ToUInt16(texCoords, v * 4 + 0) / 65535f;
                        float tv = BitConverter.ToUInt16(texCoords, v * 4 + 2) / 65535f;
                        uvs.Add(new Vector2(u, tv));
                    }
                    else
                    {
                        uvs.Add(Vector2.zero);
                    }
                }

                var tris = new List<int>();
                int triCount = indices.Length / 2; // uint16 indices
                for (int t = 0; t < triCount; t += 3)
                {
                    int a = BitConverter.ToUInt16(indices, t * 2)       + baseVert;
                    int b = BitConverter.ToUInt16(indices, (t + 1) * 2) + baseVert;
                    int c = BitConverter.ToUInt16(indices, (t + 2) * 2) + baseVert;
                    // Reverse winding (SL CCW → Unity CW front-face)
                    tris.Add(a); tris.Add(c); tris.Add(b);
                }
                subTris.Add(tris);
            }

            var mesh = new Mesh { name = name };
            mesh.indexFormat = verts.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = subTris.Count;
            for (int s = 0; s < subTris.Count; s++)
                mesh.SetTriangles(subTris[s], s);

            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        // ── LLSD-binary helpers ───────────────────────────────────────────────

        private static long ReadLLSDInt(BinaryReader br)
        {
            byte t = br.ReadByte();
            return ReadLLSDInteger(br, t);
        }

        private static long ReadLLSDInteger(BinaryReader br, byte type = 0)
        {
            if (type == 0) type = br.ReadByte();
            return type switch
            {
                (byte)'i' => br.ReadInt32(),
                (byte)'r' => (long)br.ReadDouble(),
                _          => 0
            };
        }

        private static string ReadLLSDKey(BinaryReader br)
        {
            br.ReadByte(); // 'k' or 's'
            int len = br.ReadInt32();
            return System.Text.Encoding.UTF8.GetString(br.ReadBytes(len));
        }

        private static byte[] ReadLLSDBinary(BinaryReader br, byte type)
        {
            if (type != (byte)'b') { SkipLLSDValue(br, type); return null; }
            int len = br.ReadInt32();
            return br.ReadBytes(len);
        }

        private static void ParsePositionDomain(BinaryReader br, out float scale, out Vector3 offset)
        {
            scale  = 1f;
            offset = Vector3.zero;
            // Nested map with "Max" and "Min" LLSD reals
            br.ReadByte(); // '{'
            int count = (int)ReadLLSDInt(br);
            float[] max = null, min = null;
            for (int i = 0; i < count; i++)
            {
                string k = ReadLLSDKey(br);
                br.ReadByte(); // '['
                int dims = (int)ReadLLSDInt(br);
                var vals = new float[dims];
                for (int d = 0; d < dims; d++)
                {
                    br.ReadByte(); // 'r'
                    vals[d] = (float)br.ReadDouble();
                }
                br.ReadByte(); // ']'
                if (k == "Max") max = vals;
                if (k == "Min") min = vals;
            }
            br.ReadByte(); // '}'

            if (max != null && min != null && max.Length >= 3 && min.Length >= 3)
            {
                scale  = Mathf.Max(max[0] - min[0], max[1] - min[1], max[2] - min[2]);
                offset = new Vector3(min[0], min[1], min[2]);
            }
        }

        private static void SkipLLSDValue(BinaryReader br, byte type)
        {
            switch ((char)type)
            {
                case '!': break;                                      // undefined
                case '1': case '0': break;                            // bool
                case 'i': br.ReadBytes(4); break;                     // int
                case 'r': br.ReadBytes(8); break;                     // real
                case 'u': br.ReadBytes(16); break;                    // uuid
                case 's': case 'k': br.ReadBytes(br.ReadInt32()); break; // string/key
                case 'd': br.ReadBytes(8); break;                     // date
                case 'l': br.ReadBytes(br.ReadInt32()); break;        // uri
                case 'b': br.ReadBytes(br.ReadInt32()); break;        // binary
                case '[': { int n = (int)ReadLLSDInt(br); for (int i = 0; i < n; i++) SkipLLSDValue(br, br.ReadByte()); br.ReadByte(); break; }
                case '{': { int n = (int)ReadLLSDInt(br); for (int i = 0; i < n; i++) { ReadLLSDKey(br); SkipLLSDValue(br, br.ReadByte()); } br.ReadByte(); break; }
            }
        }

        // ── Decompression ─────────────────────────────────────────────────────

        private static byte[] Inflate(byte[] compressed)
        {
            // SL LOD blobs use raw zlib (not gzip)
            // Skip zlib header (2 bytes) then use DeflateStream
            using var ms  = new MemoryStream(compressed, 2, compressed.Length - 2);
            using var ds  = new DeflateStream(ms, CompressionMode.Decompress);
            using var out_ = new MemoryStream();
            ds.CopyTo(out_);
            return out_.ToArray();
        }
    }
}
