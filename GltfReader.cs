using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SigmaMod
{
    // glTF 2.0 / GLB reader with no Unity dependency (so it can be tested offline). Produces the
    // node tree with TRS, triangle meshes, materials, images and each node's "extras" (Blender
    // custom properties, exported with "Include > Custom Properties").
    //
    // Everything is converted to Unity space here: glTF is right-handed with +Z forward, Unity is
    // left-handed. Mirroring X keeps +Y up and +Z forward, so a model facing Blender's front view
    // (-Y in Blender) faces +Z in Unity. Positions/normals negate X, rotations become (x,-y,-z,w),
    // triangle winding is reversed and V is flipped (glTF UV origin is top-left).
    internal sealed class GltfModel
    {
        public readonly List<GNode> Nodes = new List<GNode>();
        public readonly List<GMesh> Meshes = new List<GMesh>();
        public readonly List<GMaterial> Materials = new List<GMaterial>();
        public readonly List<GImage> Images = new List<GImage>();
        public readonly List<int> Roots = new List<int>();
        public readonly List<string> Warnings = new List<string>();
    }

    internal sealed class GNode
    {
        public string Name;
        public int Parent = -1;
        public readonly List<int> Children = new List<int>();
        public float[] T = { 0, 0, 0 };
        public float[] R = { 0, 0, 0, 1 };
        public float[] S = { 1, 1, 1 };
        public int Mesh = -1;
        public Dictionary<string, object> Extras;
    }

    internal sealed class GMesh
    {
        public string Name;
        public float[] Positions; // xyz
        public float[] Normals;   // xyz, null if any primitive had none
        public float[] Uvs;       // uv, null if no primitive had any
        public readonly List<int[]> Submeshes = new List<int[]>();
        public readonly List<int> SubmeshMaterials = new List<int>(); // -1 = default
        public Dictionary<string, object> Extras;
    }

    internal sealed class GMaterial
    {
        public string Name;
        public float[] BaseColor = { 1, 1, 1, 1 };
        public int BaseColorImage = -1;
        public int MetallicRoughnessImage = -1; // glTF layout: G roughness, B metallic
        public int NormalImage = -1;            // tangent-space RGB
        public float NormalScale = 1f;
        public int EmissiveImage = -1;
        public float Metallic = 1f;
        public float Roughness = 1f;
        public float[] Emissive = { 0, 0, 0 };
        public string AlphaMode = "OPAQUE";
        public float AlphaCutoff = 0.5f;
        public bool DoubleSided;
        public Dictionary<string, object> Extras;
    }

    internal sealed class GImage
    {
        public string Name;
        public string MimeType;
        public string Uri;   // external file relative to the .glb; written instead of embedding when set
        public byte[] Data;
    }

    internal static class GltfReader
    {
        private const uint Magic = 0x46546C67, ChunkJson = 0x4E4F534A, ChunkBin = 0x004E4942;

        public static GltfModel Load(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            string json;
            byte[] glbBin = null;
            if (file.Length >= 12 && BitConverter.ToUInt32(file, 0) == Magic)
            {
                int off = 12;
                json = null;
                while (off + 8 <= file.Length)
                {
                    int len = BitConverter.ToInt32(file, off);
                    uint type = BitConverter.ToUInt32(file, off + 4);
                    if (type == ChunkJson) json = Encoding.UTF8.GetString(file, off + 8, len);
                    else if (type == ChunkBin) { glbBin = new byte[len]; Buffer.BlockCopy(file, off + 8, glbBin, 0, len); }
                    off += 8 + len;
                }
                if (json == null) throw new FormatException("GLB without JSON chunk");
            }
            else json = Encoding.UTF8.GetString(file);

            var root = (Dictionary<string, object>)Json.Parse(json);
            return new Ctx(root, glbBin, Path.GetDirectoryName(path)).Read();
        }

        private sealed class Ctx
        {
            private readonly Dictionary<string, object> _j;
            private readonly string _dir;
            private readonly List<byte[]> _buffers = new List<byte[]>();
            private readonly GltfModel _m = new GltfModel();

            public Ctx(Dictionary<string, object> j, byte[] glbBin, string dir)
            {
                _j = j;
                _dir = dir;
                foreach (var b in Arr(j, "buffers"))
                {
                    var bd = (Dictionary<string, object>)b;
                    string uri = Str(bd, "uri");
                    _buffers.Add(uri == null ? glbBin : Uri(uri));
                }
            }

            public GltfModel Read()
            {
                ReadImages();
                ReadMaterials();
                foreach (var o in Arr(_j, "meshes")) _m.Meshes.Add(ReadMesh((Dictionary<string, object>)o));
                ReadNodes();
                return _m;
            }

            private byte[] Uri(string uri)
            {
                if (uri.StartsWith("data:", StringComparison.Ordinal))
                    return Convert.FromBase64String(uri.Substring(uri.IndexOf(',') + 1));
                return File.ReadAllBytes(Path.Combine(_dir, System.Uri.UnescapeDataString(uri)));
            }

            private void ReadImages()
            {
                foreach (var o in Arr(_j, "images"))
                {
                    var d = (Dictionary<string, object>)o;
                    var img = new GImage { Name = Str(d, "name"), MimeType = Str(d, "mimeType") };
                    if (d.ContainsKey("bufferView"))
                    {
                        var bv = View(Int(d, "bufferView", 0), out int off, out int len, out _);
                        img.Data = new byte[len];
                        Buffer.BlockCopy(bv, off, img.Data, 0, len);
                    }
                    else if (Str(d, "uri") != null)
                    {
                        string u = Str(d, "uri");
                        if (!u.StartsWith("data:", StringComparison.Ordinal)) img.Uri = u;
                        try { img.Data = Uri(u); }
                        catch (IOException e) { _m.Warnings.Add("image " + u + ": " + e.Message); }
                    }
                    _m.Images.Add(img);
                }
            }

            private void ReadMaterials()
            {
                var textures = Arr(_j, "textures");
                foreach (var o in Arr(_j, "materials"))
                {
                    var d = (Dictionary<string, object>)o;
                    var m = new GMaterial { Name = Str(d, "name"), Extras = Obj(d, "extras") };
                    var pbr = Obj(d, "pbrMetallicRoughness");
                    if (pbr != null)
                    {
                        m.BaseColor = Floats(pbr, "baseColorFactor", m.BaseColor);
                        m.Metallic = Float(pbr, "metallicFactor", 1f);
                        m.Roughness = Float(pbr, "roughnessFactor", 1f);
                        m.BaseColorImage = Tex(textures, Obj(pbr, "baseColorTexture"));
                        m.MetallicRoughnessImage = Tex(textures, Obj(pbr, "metallicRoughnessTexture"));
                    }
                    var nt = Obj(d, "normalTexture");
                    m.NormalImage = Tex(textures, nt);
                    if (nt != null) m.NormalScale = Float(nt, "scale", 1f);
                    m.EmissiveImage = Tex(textures, Obj(d, "emissiveTexture"));
                    m.Emissive = Floats(d, "emissiveFactor", m.Emissive);
                    var ext = Obj(d, "extensions");
                    var es = ext != null ? Obj(ext, "KHR_materials_emissive_strength") : null;
                    if (es != null)
                    {
                        float k = Float(es, "emissiveStrength", 1f);
                        for (int i = 0; i < 3; i++) m.Emissive[i] *= k;
                    }
                    m.AlphaMode = Str(d, "alphaMode") ?? "OPAQUE";
                    m.AlphaCutoff = Float(d, "alphaCutoff", 0.5f);
                    m.DoubleSided = d.TryGetValue("doubleSided", out var ds) && ds is bool b && b;
                    _m.Materials.Add(m);
                }
            }

            private static int Tex(List<object> textures, Dictionary<string, object> info)
            {
                if (info == null) return -1;
                int ti = Int(info, "index", -1);
                return ti >= 0 && ti < textures.Count ? Int((Dictionary<string, object>)textures[ti], "source", -1) : -1;
            }

            private GMesh ReadMesh(Dictionary<string, object> d)
            {
                var mesh = new GMesh { Name = Str(d, "name"), Extras = Obj(d, "extras") };
                var pos = new List<float>();
                var nrm = new List<float>();
                var uv = new List<float>();
                bool allNormals = true, anyUv = false;
                // primitives that share vertex accessors (one mesh, several materials) share vertices
                var shared = new Dictionary<string, int>();
                foreach (var po in Arr(d, "primitives"))
                {
                    var p = (Dictionary<string, object>)po;
                    int mode = Int(p, "mode", 4);
                    if (mode != 4) { _m.Warnings.Add($"mesh '{mesh.Name}': primitive mode {mode} skipped (triangles only)"); continue; }
                    var at = Obj(p, "attributes");
                    if (at == null || !at.ContainsKey("POSITION")) continue;
                    string key = Int(at, "POSITION", -1) + "/" + Int(at, "NORMAL", -1) + "/" + Int(at, "TEXCOORD_0", -1);
                    if (!shared.TryGetValue(key, out int baseV))
                    {
                        baseV = pos.Count / 3;
                        shared[key] = baseV;
                        AppendVertices(at, pos, nrm, uv, ref allNormals, ref anyUv);
                    }
                    int n = Accessor(Int(at, "POSITION", -1)).Length / 3;

                    int[] idx;
                    if (p.ContainsKey("indices"))
                    {
                        var I = Accessor(Int(p, "indices", -1));
                        idx = new int[I.Length];
                        for (int i = 0; i < I.Length; i++) idx[i] = (int)I[i];
                    }
                    else { idx = new int[n]; for (int i = 0; i < n; i++) idx[i] = i; }
                    var tris = new int[idx.Length - idx.Length % 3];
                    for (int i = 0; i < tris.Length; i += 3)
                    {
                        tris[i] = idx[i] + baseV; tris[i + 1] = idx[i + 2] + baseV; tris[i + 2] = idx[i + 1] + baseV;
                    }
                    mesh.Submeshes.Add(tris);
                    mesh.SubmeshMaterials.Add(Int(p, "material", -1));
                }
                mesh.Positions = pos.ToArray();
                mesh.Normals = allNormals ? nrm.ToArray() : null;
                mesh.Uvs = anyUv ? uv.ToArray() : null;
                return mesh;
            }

            private void AppendVertices(Dictionary<string, object> at, List<float> pos, List<float> nrm, List<float> uv,
                                        ref bool allNormals, ref bool anyUv)
            {
                var P = Accessor(Int(at, "POSITION", -1));
                int n = P.Length / 3;
                for (int i = 0; i < n; i++) { pos.Add(-P[i * 3]); pos.Add(P[i * 3 + 1]); pos.Add(P[i * 3 + 2]); }

                if (at.ContainsKey("NORMAL"))
                {
                    var N = Accessor(Int(at, "NORMAL", -1));
                    for (int i = 0; i < n; i++) { nrm.Add(-N[i * 3]); nrm.Add(N[i * 3 + 1]); nrm.Add(N[i * 3 + 2]); }
                }
                else { allNormals = false; for (int i = 0; i < n * 3; i++) nrm.Add(0); }

                if (at.ContainsKey("TEXCOORD_0"))
                {
                    anyUv = true;
                    var U = Accessor(Int(at, "TEXCOORD_0", -1));
                    for (int i = 0; i < n; i++) { uv.Add(U[i * 2]); uv.Add(1f - U[i * 2 + 1]); }
                }
                else for (int i = 0; i < n * 2; i++) uv.Add(0);
            }

            private void ReadNodes()
            {
                var nodes = Arr(_j, "nodes");
                foreach (var o in nodes)
                {
                    var d = (Dictionary<string, object>)o;
                    var n = new GNode { Name = Str(d, "name"), Mesh = Int(d, "mesh", -1), Extras = Obj(d, "extras") };
                    if (d.ContainsKey("matrix")) Decompose(Floats(d, "matrix", null), n);
                    else
                    {
                        n.T = Floats(d, "translation", n.T);
                        n.R = Floats(d, "rotation", n.R);
                        n.S = Floats(d, "scale", n.S);
                    }
                    n.T[0] = -n.T[0];
                    n.R[1] = -n.R[1]; n.R[2] = -n.R[2];
                    foreach (var c in Arr(d, "children")) n.Children.Add(Convert.ToInt32(c));
                    _m.Nodes.Add(n);
                }
                for (int i = 0; i < _m.Nodes.Count; i++)
                    foreach (int c in _m.Nodes[i].Children) _m.Nodes[c].Parent = i;

                // the default scene's roots, else every parentless node
                var scenes = Arr(_j, "scenes");
                int si = Int(_j, "scene", 0);
                if (si < scenes.Count)
                    foreach (var r in Arr((Dictionary<string, object>)scenes[si], "nodes")) _m.Roots.Add(Convert.ToInt32(r));
                else
                    for (int i = 0; i < _m.Nodes.Count; i++) if (_m.Nodes[i].Parent < 0) _m.Roots.Add(i);
            }

            // column-major 4x4 -> T, R, S (no shear)
            private static void Decompose(float[] m, GNode n)
            {
                n.T = new[] { m[12], m[13], m[14] };
                float sx = Len(m[0], m[1], m[2]), sy = Len(m[4], m[5], m[6]), sz = Len(m[8], m[9], m[10]);
                float det = m[0] * (m[5] * m[10] - m[9] * m[6]) - m[4] * (m[1] * m[10] - m[9] * m[2]) + m[8] * (m[1] * m[6] - m[5] * m[2]);
                if (det < 0) sx = -sx;
                n.S = new[] { sx, sy, sz };
                float r00 = m[0] / sx, r10 = m[1] / sx, r20 = m[2] / sx;
                float r01 = m[4] / sy, r11 = m[5] / sy, r21 = m[6] / sy;
                float r02 = m[8] / sz, r12 = m[9] / sz, r22 = m[10] / sz;
                float tr = r00 + r11 + r22, x, y, z, w;
                if (tr > 0) { float s = (float)Math.Sqrt(tr + 1) * 2; w = s / 4; x = (r21 - r12) / s; y = (r02 - r20) / s; z = (r10 - r01) / s; }
                else if (r00 > r11 && r00 > r22) { float s = (float)Math.Sqrt(1 + r00 - r11 - r22) * 2; w = (r21 - r12) / s; x = s / 4; y = (r01 + r10) / s; z = (r02 + r20) / s; }
                else if (r11 > r22) { float s = (float)Math.Sqrt(1 + r11 - r00 - r22) * 2; w = (r02 - r20) / s; x = (r01 + r10) / s; y = s / 4; z = (r12 + r21) / s; }
                else { float s = (float)Math.Sqrt(1 + r22 - r00 - r11) * 2; w = (r10 - r01) / s; x = (r02 + r20) / s; y = (r12 + r21) / s; z = s / 4; }
                n.R = new[] { x, y, z, w };
            }

            private static float Len(float a, float b, float c) => (float)Math.Sqrt(a * a + b * b + c * c);

            private byte[] View(int i, out int offset, out int length, out int stride)
            {
                var bv = (Dictionary<string, object>)Arr(_j, "bufferViews")[i];
                offset = Int(bv, "byteOffset", 0);
                length = Int(bv, "byteLength", 0);
                stride = Int(bv, "byteStride", 0);
                return _buffers[Int(bv, "buffer", 0)];
            }

            // any accessor as floats (normalized integer types scaled to 0..1)
            private float[] Accessor(int index)
            {
                var a = (Dictionary<string, object>)Arr(_j, "accessors")[index];
                int count = Int(a, "count", 0);
                int comps = Comps(Str(a, "type"));
                int ct = Int(a, "componentType", 5126);
                bool norm = a.TryGetValue("normalized", out var nb) && nb is bool b && b;
                var res = new float[count * comps];
                if (!a.ContainsKey("bufferView")) return res;
                if (a.ContainsKey("sparse")) _m.Warnings.Add("sparse accessor " + index + " ignored");
                var buf = View(Int(a, "bufferView", 0), out int off, out _, out int stride);
                int csize = ct == 5126 || ct == 5125 ? 4 : ct == 5123 || ct == 5122 ? 2 : 1;
                if (stride == 0) stride = csize * comps;
                off += Int(a, "byteOffset", 0);
                for (int e = 0; e < count; e++)
                    for (int c = 0; c < comps; c++)
                    {
                        int p = off + e * stride + c * csize;
                        float v;
                        switch (ct)
                        {
                            case 5126: v = BitConverter.ToSingle(buf, p); break;
                            case 5125: v = BitConverter.ToUInt32(buf, p); break;
                            case 5123: v = BitConverter.ToUInt16(buf, p); if (norm) v /= 65535f; break;
                            case 5122: v = BitConverter.ToInt16(buf, p); if (norm) v = Math.Max(v / 32767f, -1f); break;
                            case 5121: v = buf[p]; if (norm) v /= 255f; break;
                            case 5120: v = (sbyte)buf[p]; if (norm) v = Math.Max(v / 127f, -1f); break;
                            default: throw new FormatException("componentType " + ct);
                        }
                        res[e * comps + c] = v;
                    }
                return res;
            }

            private static int Comps(string t)
            {
                switch (t)
                {
                    case "SCALAR": return 1;
                    case "VEC2": return 2;
                    case "VEC3": return 3;
                    case "VEC4": return 4;
                    case "MAT4": return 16;
                    default: throw new FormatException("accessor type " + t);
                }
            }
        }

        // JSON helpers
        private static readonly List<object> Empty = new List<object>();
        internal static List<object> Arr(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v is List<object> l ? l : Empty;
        internal static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
        internal static string Str(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as string : null;
        internal static int Int(Dictionary<string, object> d, string k, int def) => d.TryGetValue(k, out var v) && v is double x ? (int)x : def;
        internal static float Float(Dictionary<string, object> d, string k, float def) => d.TryGetValue(k, out var v) && v is double x ? (float)x : def;

        internal static float[] Floats(Dictionary<string, object> d, string k, float[] def)
        {
            if (!d.TryGetValue(k, out var v) || !(v is List<object> l)) return def;
            var r = new float[l.Count];
            for (int i = 0; i < r.Length; i++) r[i] = Convert.ToSingle(l[i]);
            return r;
        }
    }
}
