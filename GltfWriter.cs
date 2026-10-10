using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SigmaMod
{
    // Writes a GltfModel (Unity space) as GLB, applying the same X mirror as GltfReader so a
    // write/read round trip is the identity. One buffer; each mesh's submeshes are primitives that
    // share its vertex accessors.
    internal static class GltfWriter
    {
        public static void Save(GltfModel m, string path) => File.WriteAllBytes(path, Write(m));

        public static byte[] Write(GltfModel m)
        {
            var bin = new MemoryStream();
            var views = new List<object>();
            var accessors = new List<object>();

            int View(byte[] data, int target)
            {
                while (bin.Length % 4 != 0) bin.WriteByte(0);
                var v = new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = (int)bin.Length, ["byteLength"] = data.Length };
                if (target != 0) v["target"] = target;
                bin.Write(data, 0, data.Length);
                views.Add(v);
                return views.Count - 1;
            }

            int Floats(float[] f, int comps, string type, bool minMax)
            {
                var bytes = new byte[f.Length * 4];
                Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
                var a = new Dictionary<string, object>
                {
                    ["bufferView"] = View(bytes, 34962), ["componentType"] = 5126, ["count"] = f.Length / comps, ["type"] = type
                };
                if (minMax)
                {
                    var mn = new float[comps]; var mx = new float[comps];
                    for (int c = 0; c < comps; c++) { mn[c] = float.MaxValue; mx[c] = float.MinValue; }
                    for (int i = 0; i < f.Length; i++) { int c = i % comps; mn[c] = Math.Min(mn[c], f[i]); mx[c] = Math.Max(mx[c], f[i]); }
                    if (f.Length == 0) { mn = new float[comps]; mx = new float[comps]; }
                    a["min"] = mn; a["max"] = mx;
                }
                accessors.Add(a);
                return accessors.Count - 1;
            }

            int Indices(int[] idx)
            {
                var bytes = new byte[idx.Length * 4];
                Buffer.BlockCopy(idx, 0, bytes, 0, bytes.Length);
                accessors.Add(new Dictionary<string, object>
                {
                    ["bufferView"] = View(bytes, 34963), ["componentType"] = 5125, ["count"] = idx.Length, ["type"] = "SCALAR"
                });
                return accessors.Count - 1;
            }

            var meshes = new List<object>();
            foreach (var g in m.Meshes)
            {
                var pos = (float[])g.Positions.Clone();
                for (int i = 0; i < pos.Length; i += 3) pos[i] = -pos[i];
                var attrs = new Dictionary<string, object> { ["POSITION"] = Floats(pos, 3, "VEC3", true) };
                if (g.Normals != null)
                {
                    var n = (float[])g.Normals.Clone();
                    for (int i = 0; i < n.Length; i += 3) n[i] = -n[i];
                    attrs["NORMAL"] = Floats(n, 3, "VEC3", false);
                }
                if (g.Uvs != null)
                {
                    var uv = (float[])g.Uvs.Clone();
                    for (int i = 1; i < uv.Length; i += 2) uv[i] = 1f - uv[i];
                    attrs["TEXCOORD_0"] = Floats(uv, 2, "VEC2", false);
                }
                var prims = new List<object>();
                for (int s = 0; s < g.Submeshes.Count; s++)
                {
                    var t = g.Submeshes[s];
                    var idx = new int[t.Length];
                    for (int i = 0; i < t.Length; i += 3) { idx[i] = t[i]; idx[i + 1] = t[i + 2]; idx[i + 2] = t[i + 1]; }
                    var p = new Dictionary<string, object> { ["attributes"] = attrs, ["indices"] = Indices(idx), ["mode"] = 4 };
                    if (g.SubmeshMaterials[s] >= 0) p["material"] = g.SubmeshMaterials[s];
                    prims.Add(p);
                }
                var md = new Dictionary<string, object> { ["primitives"] = prims };
                if (g.Name != null) md["name"] = g.Name;
                if (g.Extras != null) md["extras"] = g.Extras;
                meshes.Add(md);
            }

            var images = new List<object>();
            var textures = new List<object>();
            foreach (var img in m.Images)
            {
                var d = new Dictionary<string, object> { ["mimeType"] = img.MimeType ?? "image/png" };
                if (img.Uri != null) d["uri"] = img.Uri;
                else d["bufferView"] = View(img.Data, 0);
                if (img.Name != null) d["name"] = img.Name;
                images.Add(d);
                textures.Add(new Dictionary<string, object> { ["source"] = images.Count - 1 });
            }

            var materials = new List<object>();
            foreach (var g in m.Materials)
            {
                var pbr = new Dictionary<string, object>
                {
                    ["baseColorFactor"] = g.BaseColor, ["metallicFactor"] = g.Metallic, ["roughnessFactor"] = g.Roughness
                };
                if (g.BaseColorImage >= 0) pbr["baseColorTexture"] = new Dictionary<string, object> { ["index"] = g.BaseColorImage };
                if (g.MetallicRoughnessImage >= 0) pbr["metallicRoughnessTexture"] = new Dictionary<string, object> { ["index"] = g.MetallicRoughnessImage };
                var d = new Dictionary<string, object> { ["pbrMetallicRoughness"] = pbr, ["alphaMode"] = g.AlphaMode ?? "OPAQUE" };
                if (g.NormalImage >= 0) d["normalTexture"] = new Dictionary<string, object> { ["index"] = g.NormalImage, ["scale"] = g.NormalScale };
                if (g.EmissiveImage >= 0) d["emissiveTexture"] = new Dictionary<string, object> { ["index"] = g.EmissiveImage };
                if (g.Name != null) d["name"] = g.Name;
                if (g.AlphaMode == "MASK") d["alphaCutoff"] = g.AlphaCutoff;
                if (g.DoubleSided) d["doubleSided"] = true;
                float emax = Math.Max(g.Emissive[0], Math.Max(g.Emissive[1], g.Emissive[2]));
                if (emax > 0)
                {
                    // emissiveFactor is clamped to 0..1; the rest goes in the strength extension
                    float k = Math.Max(1f, emax);
                    d["emissiveFactor"] = new[] { g.Emissive[0] / k, g.Emissive[1] / k, g.Emissive[2] / k };
                    if (k > 1f) d["extensions"] = new Dictionary<string, object>
                    {
                        ["KHR_materials_emissive_strength"] = new Dictionary<string, object> { ["emissiveStrength"] = k }
                    };
                }
                if (g.Extras != null) d["extras"] = g.Extras;
                materials.Add(d);
            }

            var nodes = new List<object>();
            foreach (var n in m.Nodes)
            {
                var d = new Dictionary<string, object>
                {
                    ["translation"] = new[] { -n.T[0], n.T[1], n.T[2] },
                    ["rotation"] = new[] { n.R[0], -n.R[1], -n.R[2], n.R[3] },
                    ["scale"] = n.S
                };
                if (n.Name != null) d["name"] = n.Name;
                if (n.Mesh >= 0) d["mesh"] = n.Mesh;
                if (n.Children.Count > 0) d["children"] = n.Children;
                if (n.Extras != null && n.Extras.Count > 0) d["extras"] = n.Extras;
                nodes.Add(d);
            }

            while (bin.Length % 4 != 0) bin.WriteByte(0);
            var root = new Dictionary<string, object>
            {
                ["asset"] = new Dictionary<string, object> { ["version"] = "2.0", ["generator"] = "SigmaMod" },
                ["scene"] = 0,
                ["scenes"] = new List<object> { new Dictionary<string, object> { ["nodes"] = m.Roots } },
                ["nodes"] = nodes,
                ["buffers"] = new List<object> { new Dictionary<string, object> { ["byteLength"] = (int)bin.Length } }
            };
            if (meshes.Count > 0) root["meshes"] = meshes;
            if (materials.Count > 0) root["materials"] = materials;
            if (images.Count > 0) { root["images"] = images; root["textures"] = textures; }
            if (views.Count > 0) root["bufferViews"] = views;
            if (accessors.Count > 0) root["accessors"] = accessors;
            if (UsesEmissiveStrength(m)) root["extensionsUsed"] = new List<object> { "KHR_materials_emissive_strength" };

            var json = Encoding.UTF8.GetBytes(Json.Write(root));
            int jsonPad = (4 - json.Length % 4) % 4;
            var data = bin.ToArray();
            var o = new MemoryStream();
            var w = new BinaryWriter(o);
            w.Write(0x46546C67u); w.Write(2u);
            w.Write((uint)(12 + 8 + json.Length + jsonPad + (data.Length > 0 ? 8 + data.Length : 0)));
            w.Write((uint)(json.Length + jsonPad)); w.Write(0x4E4F534Au); w.Write(json);
            for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
            if (data.Length > 0) { w.Write((uint)data.Length); w.Write(0x004E4942u); w.Write(data); }
            w.Flush();
            return o.ToArray();
        }

        private static bool UsesEmissiveStrength(GltfModel m)
        {
            foreach (var g in m.Materials)
                if (Math.Max(g.Emissive[0], Math.Max(g.Emissive[1], g.Emissive[2])) > 1f) return true;
            return false;
        }
    }
}
