using System;
using System.Collections.Generic;
using System.Linq;
using ES3Internal;
using NWH.VehiclePhysics2;
using NWH.WheelController3D;
using UnityEngine;
using UnityEngine.Rendering;

namespace SigmaMod
{
    // Unity <-> GltfModel, both directions.
    //
    // Export walks a GameObject tree (a vanilla prefab or a live object) and keeps every transform as
    // a node. Renderers become meshes with their materials and textures (read back through the GPU,
    // so non-readable textures work). Colliders become child nodes tagged extras.sigma = "collider",
    // drawn as translucent unit shapes scaled to the collider, so they can be edited in Blender. Node
    // extras carry what the file cannot otherwise express: layer, tag, active, component list, FSM
    // names, rigidbody and wheel data, and Easy Save reference ids (es3local: the id inside the
    // owning ES3Prefab; es3global: the scene reference manager's id, which also covers meshes and
    // materials). Non-readable meshes are written as their bounding box with extras.unreadable.
    //
    // Build creates GameObjects from a model. A material whose es3global id resolves to a loaded
    // Material uses that vanilla material; so does a mesh marked unreadable. Everything else is built
    // from the file. Rigidbody, wheel and FSM data stay in the returned extras for the caller.
    internal static class GltfUnity
    {
        public const string MarkerKey = "sigma";
        public const string ColliderMarker = "collider";

        // ================================================================ export

        public sealed class ExportStats
        {
            public int Nodes, Meshes, UnreadableMeshes, Materials, Images, Colliders;
            public override string ToString() =>
                $"{Nodes} nodes, {Meshes} meshes ({UnreadableMeshes} unreadable), {Materials} materials, {Images} images, {Colliders} colliders";
        }

        // Textures shared by several exports: written once as PNG files under Dir/textures and
        // referenced by relative uri. Names carry a content hash, so files are stable across runs and
        // an existing file is not encoded again.
        public sealed class TextureStore
        {
            public readonly string Dir;
            public readonly Dictionary<string, string> Uris = new Dictionary<string, string>();
            public int Written, Reused;
            public TextureStore(string dir) { Dir = dir; }
        }

        // store == null embeds the textures in the file
        public static GltfModel Export(GameObject root, ExportStats stats, TextureStore store)
        {
            var e = new Exporter(root, stats, store);
            e.Node(root.transform, -1, true);
            return e.M;
        }

        private sealed class Exporter
        {
            public readonly GltfModel M = new GltfModel();
            private readonly ExportStats _s;
            private readonly ES3ReferenceMgrBase _mgr = ES3ReferenceMgrBase.Current;
            private readonly ES3Prefab _prefab;
            private readonly TextureStore _store;
            private readonly Dictionary<string, int> _imageByUri = new Dictionary<string, int>();
            private readonly Dictionary<string, int> _meshes = new Dictionary<string, int>();
            private readonly Dictionary<Material, int> _mats = new Dictionary<Material, int>();
            private readonly Dictionary<string, int> _images = new Dictionary<string, int>();
            private int _colliderMat = -1, _unitBox = -1, _unitSphere = -1;

            public Exporter(GameObject root, ExportStats s, TextureStore store)
            {
                _s = s;
                _store = store;
                _prefab = root.GetComponent<ES3Prefab>();
            }

            private void Refs(UnityEngine.Object o, Dictionary<string, object> ex)
            {
                if (o == null) return;
                long l = _prefab != null ? _prefab.Get(o) : -1;
                if (l != -1) ex["es3local"] = l.ToString();
                long g = _mgr != null ? _mgr.Get(o) : -1;
                if (g != -1) ex["es3global"] = g.ToString();
            }

            public void Node(Transform t, int parent, bool isRoot)
            {
                var n = new GNode { Name = t.name, Parent = parent, S = V(t.localScale) };
                if (!isRoot) { n.T = V(t.localPosition); n.R = Q(t.localRotation); }
                var ex = new Dictionary<string, object> { ["layer"] = t.gameObject.layer, ["tag"] = t.tag };
                if (!t.gameObject.activeSelf) ex["active"] = false;
                Refs(t.gameObject, ex);

                var comps = new List<object>();
                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null || c is Transform) continue;
                    switch (c)
                    {
                        case PlayMakerFSM f: comps.Add("PlayMakerFSM:" + f.FsmName); break;
                        default: comps.Add(c.GetType().Name); break;
                    }
                    switch (c)
                    {
                        case Rigidbody rb:
                            ex["rigidbody"] = new Dictionary<string, object>
                            {
                                ["mass"] = rb.mass, ["drag"] = rb.drag, ["angularDrag"] = rb.angularDrag,
                                ["centerOfMass"] = V(rb.centerOfMass), ["inertiaTensor"] = V(rb.inertiaTensor),
                                ["inertiaTensorRotation"] = Q(rb.inertiaTensorRotation), ["kinematic"] = rb.isKinematic
                            };
                            break;
                        case WheelController wc:
                            ex["wheel"] = new Dictionary<string, object>
                            {
                                ["radius"] = wc.wheel.radius, ["width"] = wc.wheel.width, ["mass"] = wc.wheel.mass,
                                ["springMaxLength"] = wc.spring.maxLength, ["springMaxForce"] = wc.spring.maxForce
                            };
                            break;
                        case ES3Prefab p: ex["es3prefab"] = p.prefabId.ToString(); break;
                    }
                }
                ex["components"] = comps;

                int idx = M.Nodes.Count;
                M.Nodes.Add(n);
                if (parent >= 0) M.Nodes[parent].Children.Add(idx); else M.Roots.Add(idx);
                _s.Nodes++;

                Renderer r = t.GetComponent<MeshRenderer>();
                Mesh mesh = null;
                if (r != null) { var mf = t.GetComponent<MeshFilter>(); if (mf != null) mesh = mf.sharedMesh; }
                else
                {
                    var smr = t.GetComponent<SkinnedMeshRenderer>();
                    if (smr != null) { r = smr; mesh = smr.sharedMesh; ex["skinned"] = true; }
                }
                if (r != null && mesh != null)
                {
                    n.Mesh = MeshIndex(mesh, r.sharedMaterials);
                    if (!r.enabled) ex["rendererEnabled"] = false;
                }
                n.Extras = ex;

                foreach (var c in t.GetComponents<Collider>())
                    if (!(c is WheelCollider)) Collider(c, idx);
                for (int i = 0; i < t.childCount; i++) Node(t.GetChild(i), idx, false);
            }

            private void Collider(Collider c, int parent)
            {
                var n = new GNode { Name = c.name + ".collider", Parent = parent };
                var ex = new Dictionary<string, object> { [MarkerKey] = ColliderMarker, ["trigger"] = c.isTrigger };
                if (!c.enabled) ex["enabled"] = false;
                if (c.sharedMaterial != null) ex["physicMaterial"] = c.sharedMaterial.name;
                Refs(c, ex);
                switch (c)
                {
                    case BoxCollider b:
                        ex["shape"] = "box"; n.T = V(b.center); n.S = V(b.size); n.Mesh = UnitBox(); break;
                    case SphereCollider sp:
                        ex["shape"] = "sphere"; n.T = V(sp.center); n.S = new[] { sp.radius, sp.radius, sp.radius }; n.Mesh = UnitSphere(); break;
                    case CapsuleCollider cc:
                    {
                        ex["shape"] = "capsule"; ex["direction"] = cc.direction; n.T = V(cc.center);
                        var s = new[] { cc.radius * 2, cc.radius * 2, cc.radius * 2 };
                        s[cc.direction] = Mathf.Max(cc.height, cc.radius * 2);
                        n.S = s; n.Mesh = UnitBox();
                        break;
                    }
                    case MeshCollider mc:
                        ex["shape"] = "mesh"; ex["convex"] = mc.convex;
                        if (mc.sharedMesh != null) n.Mesh = MeshIndex(mc.sharedMesh, new[] { (Material)null }, ColliderMaterial());
                        break;
                    default: ex["shape"] = c.GetType().Name; break;
                }
                n.Extras = ex;
                int idx = M.Nodes.Count;
                M.Nodes.Add(n);
                M.Nodes[parent].Children.Add(idx);
                _s.Colliders++;
            }

            private int ColliderMaterial()
            {
                if (_colliderMat >= 0) return _colliderMat;
                M.Materials.Add(new GMaterial
                {
                    Name = "sigma_collider", BaseColor = new[] { 0.1f, 0.9f, 0.2f, 0.25f }, Metallic = 0, Roughness = 1,
                    AlphaMode = "BLEND", DoubleSided = true, Extras = new Dictionary<string, object> { [MarkerKey] = ColliderMarker }
                });
                return _colliderMat = M.Materials.Count - 1;
            }

            private int UnitBox()
            {
                if (_unitBox >= 0) return _unitBox;
                var g = Shapes.Box(Vector3.zero, Vector3.one, "sigma_unit_box");
                g.SubmeshMaterials[0] = ColliderMaterial();
                M.Meshes.Add(g);
                return _unitBox = M.Meshes.Count - 1;
            }

            private int UnitSphere()
            {
                if (_unitSphere >= 0) return _unitSphere;
                var g = Shapes.Sphere(1f, 8, 16, "sigma_unit_sphere");
                g.SubmeshMaterials[0] = ColliderMaterial();
                M.Meshes.Add(g);
                return _unitSphere = M.Meshes.Count - 1;
            }

            private int MeshIndex(Mesh mesh, Material[] mats, int forceMaterial = -1)
            {
                string key = mesh.GetInstanceID() + ":" + forceMaterial + ":" +
                             string.Join(",", mats.Select(x => x == null ? "0" : x.GetInstanceID().ToString()).ToArray());
                if (_meshes.TryGetValue(key, out int have)) return have;

                var ex = new Dictionary<string, object>();
                Refs(mesh, ex);
                GMesh g;
                if (mesh.isReadable)
                {
                    g = new GMesh { Name = mesh.name };
                    var v = mesh.vertices;
                    var nr = mesh.normals;
                    var uv = mesh.uv;
                    g.Positions = new float[v.Length * 3];
                    for (int i = 0; i < v.Length; i++) { g.Positions[i * 3] = v[i].x; g.Positions[i * 3 + 1] = v[i].y; g.Positions[i * 3 + 2] = v[i].z; }
                    if (nr.Length == v.Length)
                    {
                        g.Normals = new float[v.Length * 3];
                        for (int i = 0; i < v.Length; i++) { g.Normals[i * 3] = nr[i].x; g.Normals[i * 3 + 1] = nr[i].y; g.Normals[i * 3 + 2] = nr[i].z; }
                    }
                    if (uv.Length == v.Length)
                    {
                        g.Uvs = new float[v.Length * 2];
                        for (int i = 0; i < v.Length; i++) { g.Uvs[i * 2] = uv[i].x; g.Uvs[i * 2 + 1] = uv[i].y; }
                    }
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                        g.Submeshes.Add(mesh.GetTriangles(s));
                        g.SubmeshMaterials.Add(forceMaterial >= 0 ? forceMaterial : MatIndex(mats.Length > 0 ? mats[Math.Min(s, mats.Length - 1)] : null));
                    }
                }
                else
                {
                    // vertex data stays on the GPU in a player build; keep the placement and the reference
                    g = Shapes.Box(mesh.bounds.center, mesh.bounds.size, mesh.name);
                    g.SubmeshMaterials[0] = forceMaterial >= 0 ? forceMaterial : MatIndex(mats.Length > 0 ? mats[0] : null);
                    ex["unreadable"] = true;
                    // per-submesh materials by Easy Save id: material indices do not survive Blender
                    var refs = new List<object>();
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        var sm = mats.Length > 0 ? mats[Math.Min(s, mats.Length - 1)] : null;
                        if (sm != null) MatIndex(sm);
                        long id = sm != null && _mgr != null ? _mgr.Get(sm) : -1;
                        refs.Add(id != -1 ? id.ToString() : "");
                    }
                    ex["submeshMaterials"] = refs;
                    // match key for tools/fill_meshes.py, which reads the geometry from the game's asset files
                    ex["vertexCount"] = mesh.vertexCount;
                    ex["subMeshCount"] = mesh.subMeshCount;
                    var mb = mesh.bounds;
                    ex["bounds"] = new[] { mb.center.x, mb.center.y, mb.center.z, mb.extents.x, mb.extents.y, mb.extents.z };
                    _s.UnreadableMeshes++;
                }
                g.Extras = ex;
                M.Meshes.Add(g);
                _s.Meshes++;
                return _meshes[key] = M.Meshes.Count - 1;
            }

            private int MatIndex(Material mat)
            {
                if (mat == null) return -1;
                if (_mats.TryGetValue(mat, out int have)) return have;
                var g = new GMaterial { Name = mat.name, Metallic = 0f, Roughness = 0.5f };
                var ex = new Dictionary<string, object>();
                Refs(mat, ex);
                var sh = mat.shader;
                ex["shader"] = sh != null ? sh.name : "";
                ex["renderQueue"] = mat.renderQueue;
                if (mat.shaderKeywords.Length > 0) ex["keywords"] = mat.shaderKeywords.Cast<object>().ToList();

                // every shader property, for reference
                if (sh != null)
                {
                    var props = new Dictionary<string, object>();
                    for (int i = 0; i < sh.GetPropertyCount(); i++)
                    {
                        string pn = sh.GetPropertyName(i);
                        switch (sh.GetPropertyType(i))
                        {
                            case ShaderPropertyType.Color: { var c = mat.GetColor(pn); props[pn] = new[] { c.r, c.g, c.b, c.a }; break; }
                            case ShaderPropertyType.Vector: { var c = mat.GetVector(pn); props[pn] = new[] { c.x, c.y, c.z, c.w }; break; }
                            case ShaderPropertyType.Float:
                            case ShaderPropertyType.Range: props[pn] = mat.GetFloat(pn); break;
                            case ShaderPropertyType.Texture:
                            {
                                var tx = mat.GetTexture(pn);
                                if (tx != null) props[pn] = tx.name;
                                break;
                            }
                        }
                    }
                    ex["properties"] = props;
                }

                if (mat.HasProperty("_Color"))
                {
                    var c = mat.color;
                    var lin = c.linear;
                    g.BaseColor = new[] { lin.r, lin.g, lin.b, c.a };
                }
                if (mat.HasProperty("_Metallic")) g.Metallic = mat.GetFloat("_Metallic");
                if (mat.HasProperty("_Glossiness")) g.Roughness = 1f - mat.GetFloat("_Glossiness");

                var main = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                if (main != null) g.BaseColorImage = Image(main, Kind.Color, 1f);
                var bump = mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
                if (bump != null)
                {
                    g.NormalImage = Image(bump, Kind.Normal, 1f);
                    if (mat.HasProperty("_BumpScale")) g.NormalScale = mat.GetFloat("_BumpScale");
                }
                var mg = mat.HasProperty("_MetallicGlossMap") ? mat.GetTexture("_MetallicGlossMap") : null;
                if (mg != null)
                {
                    float gs = mat.HasProperty("_GlossMapScale") ? mat.GetFloat("_GlossMapScale") : 1f;
                    g.MetallicRoughnessImage = Image(mg, Kind.MetallicGloss, gs);
                    g.Metallic = 1f; g.Roughness = 1f; // the map holds the values
                }
                if (mat.IsKeywordEnabled("_EMISSION") && mat.HasProperty("_EmissionColor"))
                {
                    var ec = mat.GetColor("_EmissionColor").linear;
                    g.Emissive = new[] { ec.r, ec.g, ec.b };
                    var em = mat.HasProperty("_EmissionMap") ? mat.GetTexture("_EmissionMap") : null;
                    if (em != null) g.EmissiveImage = Image(em, Kind.Color, 1f);
                }

                float mode = mat.HasProperty("_Mode") ? mat.GetFloat("_Mode") : -1f;
                if (mode == 1f || (mode < 0 && mat.IsKeywordEnabled("_ALPHATEST_ON")))
                {
                    g.AlphaMode = "MASK";
                    if (mat.HasProperty("_Cutoff")) g.AlphaCutoff = mat.GetFloat("_Cutoff");
                }
                else if (mode >= 2f || (mode < 0 && mat.renderQueue >= (int)RenderQueue.Transparent)) g.AlphaMode = "BLEND";
                if (mat.HasProperty("_Cull") && mat.GetFloat("_Cull") == 0f) g.DoubleSided = true;

                if (ExportExtras.TryGetValue(mat, out var more)) foreach (var kv in more) ex[kv.Key] = kv.Value;
                g.Extras = ex;
                M.Materials.Add(g);
                _s.Materials++;
                return _mats[mat] = M.Materials.Count - 1;
            }

            private enum Kind { Color, Normal, MetallicGloss }

            private int Image(Texture tex, Kind kind, float param)
            {
                string key = tex.GetInstanceID() + ":" + kind + ":" + param;
                if (_images.TryGetValue(key, out int have)) return have;
                string suffix = kind == Kind.Color ? "" : "_" + kind;
                if (_store != null && _store.Uris.TryGetValue(key, out string known))
                {
                    _store.Reused++;
                    return _images[key] = UriImage(tex.name + suffix, known);
                }
                Color32[] px;
                int w, h;
                try { px = TextureIO.ReadBack(tex, kind != Kind.Color, out w, out h); }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"export: texture '{tex.name}' ({tex.GetType().Name}) not read: {e.Message}");
                    return _images[key] = -1;
                }
                if (kind == Kind.Normal)
                {
                    // Unity normal maps are DXT5nm (x in A) or BC5/RG (x in R): x = R*A covers both
                    for (int i = 0; i < px.Length; i++)
                    {
                        float x = px[i].r / 255f * (px[i].a / 255f) * 2f - 1f, y = px[i].g / 255f * 2f - 1f;
                        float z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));
                        px[i] = new Color32((byte)Mathf.RoundToInt((x + 1) * 127.5f), (byte)Mathf.RoundToInt((y + 1) * 127.5f), (byte)Mathf.RoundToInt((z + 1) * 127.5f), 255);
                    }
                }
                else if (kind == Kind.MetallicGloss)
                {
                    // Unity: R metallic, A smoothness. glTF: G roughness, B metallic
                    for (int i = 0; i < px.Length; i++)
                    {
                        byte rough = (byte)Mathf.RoundToInt(255f * (1f - Mathf.Clamp01(px[i].a / 255f * param)));
                        px[i] = new Color32(255, rough, px[i].r, 255);
                    }
                }
                if (_store != null)
                {
                    string file = "textures/" + Safe(tex.name + suffix) + "_" + Hash(px, w, h) + ".png";
                    string full = System.IO.Path.Combine(_store.Dir, file);
                    if (!System.IO.File.Exists(full))
                    {
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
                        System.IO.File.WriteAllBytes(full, TextureIO.Png(px, w, h));
                        _store.Written++;
                    }
                    else _store.Reused++;
                    _store.Uris[key] = file;
                    return _images[key] = UriImage(tex.name + suffix, file);
                }
                M.Images.Add(new GImage { Name = tex.name + suffix, MimeType = "image/png", Data = TextureIO.Png(px, w, h) });
                _s.Images++;
                return _images[key] = M.Images.Count - 1;
            }

            private int UriImage(string name, string uri)
            {
                if (_imageByUri.TryGetValue(uri, out int i)) return i;
                M.Images.Add(new GImage { Name = name, MimeType = "image/png", Uri = uri });
                _s.Images++;
                return _imageByUri[uri] = M.Images.Count - 1;
            }

            private static string Safe(string s)
            {
                var sb = new System.Text.StringBuilder();
                foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_');
                return sb.ToString();
            }

            // FNV-1a over size and pixels
            private static string Hash(Color32[] px, int w, int h)
            {
                uint x = 2166136261;
                void Mix(uint v) { x = (x ^ v) * 16777619; }
                Mix((uint)w); Mix((uint)h);
                for (int i = 0; i < px.Length; i++) Mix((uint)(px[i].r | px[i].g << 8 | px[i].b << 16 | px[i].a << 24));
                return x.ToString("x8");
            }
        }

        // ================================================================ build

        public sealed class Built
        {
            public GameObject Root;
            public readonly Dictionary<Transform, Dictionary<string, object>> Extras = new Dictionary<Transform, Dictionary<string, object>>();
            public int VanillaMeshes, VanillaMaterials;
        }

        // inert: leave every object on the default layer and untagged, so game FSMs ignore it
        public static Built Build(GltfModel m, string name, bool inert)
        {
            var b = new Builder(m) { Inert = inert };
            var res = b.Res;
            if (m.Roots.Count == 1) res.Root = b.Node(m.Roots[0], null);
            else
            {
                res.Root = new GameObject(name);
                foreach (int r in m.Roots) b.Node(r, res.Root.transform);
            }
            return res;
        }

        internal sealed class Builder
        {
            public readonly Built Res = new Built();
            private readonly GltfModel _m;
            private ES3ReferenceMgrBase _mgr = ES3ReferenceMgrBase.Current;
            public ES3ReferenceMgrBase Mgr { set => _mgr = value; }
            private readonly Dictionary<int, Mesh> _meshes = new Dictionary<int, Mesh>();
            private readonly Dictionary<int, Material> _mats = new Dictionary<int, Material>();
            private readonly Dictionary<string, Texture2D> _tex = new Dictionary<string, Texture2D>();

            public bool Inert;

            public Builder(GltfModel m) { _m = m; }

            private T Vanilla<T>(Dictionary<string, object> ex) where T : UnityEngine.Object
            {
                if (_mgr == null || ex == null || !(ex.TryGetValue("es3global", out var v) && v is string s && long.TryParse(s, out long id))) return null;
                return _mgr.Get(id, true) as T;
            }

            public GameObject Node(int i, Transform parent)
            {
                var n = _m.Nodes[i];
                var go = new GameObject(n.Name ?? "node" + i);
                var t = go.transform;
                t.SetParent(parent, false);
                t.localPosition = new Vector3(n.T[0], n.T[1], n.T[2]);
                t.localRotation = new Quaternion(n.R[0], n.R[1], n.R[2], n.R[3]);
                t.localScale = new Vector3(n.S[0], n.S[1], n.S[2]);
                var ex = n.Extras ?? new Dictionary<string, object>();
                Res.Extras[t] = ex;
                if (!Inert && ex.TryGetValue("layer", out var l) && l is double ld) go.layer = (int)ld;
                if (!Inert && ex.TryGetValue("tag", out var tg) && tg is string ts && ts != "Untagged")
                    try { go.tag = ts; } catch (UnityException) { }

                bool isCollider = ex.TryGetValue(MarkerKey, out var mk) && (mk as string) == ColliderMarker;
                bool skinned = ex.TryGetValue("skinned", out var sk) && sk is bool skb && skb;
                if (isCollider) Collider(go, n, ex);
                else if (n.Mesh >= 0 && !skinned) // a skinned mesh's bind pose drawn static is a T-pose
                {
                    var mesh = Mesh(n.Mesh);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterials = Materials(n.Mesh, mesh);
                    if (ex.TryGetValue("rendererEnabled", out var re) && re is bool reb) r.enabled = reb;
                }
                foreach (int c in n.Children) Node(c, t);
                if (ex.TryGetValue("active", out var a) && a is bool ab) go.SetActive(ab);
                return go;
            }

            // one material per submesh of the Unity mesh built for model mesh i
            public Material[] Materials(int i, Mesh mesh)
            {
                var g = _m.Meshes[i];
                var mats = new Material[mesh.subMeshCount];
                List<object> refs = null;
                if (IsUnreadable(g) && g.Extras.TryGetValue("submeshMaterials", out var sr)) refs = sr as List<object>;
                for (int s = 0; s < mats.Length; s++)
                {
                    if (refs != null && s < refs.Count && refs[s] is string rs && long.TryParse(rs, out long rid) && _mgr != null)
                        mats[s] = _mgr.Get(rid, true) as Material;
                    if (mats[s] == null)
                        mats[s] = Material(s < g.SubmeshMaterials.Count ? g.SubmeshMaterials[s] : g.SubmeshMaterials.Count > 0 ? g.SubmeshMaterials[0] : -1);
                }
                return mats;
            }

            // every Mesh and Material created from the file (not vanilla ones), keyed by kind and name (Blender keeps names, not order)
            // so callers can give it a stable id
            public IEnumerable<KeyValuePair<string, UnityEngine.Object>> Created()
            {
                foreach (var kv in _meshes)
                    if (kv.Value != null && Vanilla<Mesh>(_m.Meshes[kv.Key].Extras) != kv.Value)
                        yield return new KeyValuePair<string, UnityEngine.Object>("mesh/" + kv.Value.name, kv.Value);
                foreach (var kv in _mats)
                    if (kv.Value != null && Vanilla<Material>(_m.Materials[kv.Key].Extras) != kv.Value)
                        yield return new KeyValuePair<string, UnityEngine.Object>("material/" + kv.Value.name, kv.Value);
                if (_default != null) yield return new KeyValuePair<string, UnityEngine.Object>("material/default", _default);
            }

            private void Collider(GameObject go, GNode n, Dictionary<string, object> ex)
            {
                string shape = ex.TryGetValue("shape", out var sv) ? sv as string : "box";
                bool trigger = ex.TryGetValue("trigger", out var tv) && tv is bool tb && tb;
                Collider c;
                switch (shape)
                {
                    case "sphere": c = go.AddComponent<SphereCollider>(); ((SphereCollider)c).radius = 1f; break;
                    case "capsule":
                    {
                        // unit box scaled to the capsule: undo the scale, size the capsule from it
                        int dir = ex.TryGetValue("direction", out var dv) && dv is double dd ? (int)dd : 1;
                        var s = go.transform.localScale;
                        go.transform.localScale = Vector3.one;
                        var cc = go.AddComponent<CapsuleCollider>();
                        cc.direction = dir;
                        cc.height = s[dir];
                        cc.radius = Mathf.Max(s[(dir + 1) % 3], s[(dir + 2) % 3]) / 2f;
                        c = cc;
                        break;
                    }
                    case "mesh":
                    {
                        var mc = go.AddComponent<MeshCollider>();
                        mc.convex = ex.TryGetValue("convex", out var cv) && cv is bool cb && cb;
                        if (n.Mesh >= 0) mc.sharedMesh = Mesh(n.Mesh);
                        c = mc;
                        break;
                    }
                    default: c = go.AddComponent<BoxCollider>(); ((BoxCollider)c).size = Vector3.one; break;
                }
                c.isTrigger = trigger;
                if (ex.TryGetValue("enabled", out var ev) && ev is bool eb) c.enabled = eb;
            }

            private static bool IsUnreadable(GMesh g) => g.Extras != null && g.Extras.TryGetValue("unreadable", out var u) && u is bool b && b;

            public Mesh Mesh(int i)
            {
                if (_meshes.TryGetValue(i, out var have)) return have;
                var g = _m.Meshes[i];
                if (IsUnreadable(g))
                {
                    var v = Vanilla<Mesh>(g.Extras);
                    if (v != null) { Res.VanillaMeshes++; return _meshes[i] = v; }
                }
                int n = g.Positions.Length / 3;
                var mesh = new Mesh { name = g.Name ?? "mesh" + i };
                if (n > 65535) mesh.indexFormat = IndexFormat.UInt32;
                var pos = new Vector3[n];
                for (int k = 0; k < n; k++) pos[k] = new Vector3(g.Positions[k * 3], g.Positions[k * 3 + 1], g.Positions[k * 3 + 2]);
                mesh.vertices = pos;
                if (g.Normals != null)
                {
                    var nr = new Vector3[n];
                    for (int k = 0; k < n; k++) nr[k] = new Vector3(g.Normals[k * 3], g.Normals[k * 3 + 1], g.Normals[k * 3 + 2]);
                    mesh.normals = nr;
                }
                if (g.Uvs != null)
                {
                    var uv = new Vector2[n];
                    for (int k = 0; k < n; k++) uv[k] = new Vector2(g.Uvs[k * 2], g.Uvs[k * 2 + 1]);
                    mesh.uv = uv;
                }
                mesh.subMeshCount = Math.Max(1, g.Submeshes.Count);
                for (int s = 0; s < g.Submeshes.Count; s++) mesh.SetTriangles(g.Submeshes[s], s);
                if (g.Normals == null) mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                if (g.Uvs != null) mesh.RecalculateTangents();
                return _meshes[i] = mesh;
            }

            public Material Material(int i)
            {
                if (i < 0) return DefaultMaterial();
                if (_mats.TryGetValue(i, out var have)) return have;
                var g = _m.Materials[i];
                var v = Vanilla<Material>(g.Extras);
                if (v != null)
                {
                    // the game's material while the model still shows its texture; given another image in Blender, a
                    // copy of it (its shader, normal map, gloss) with that image
                    var img = g.BaseColorImage >= 0 ? _m.Images[g.BaseColorImage] : null;
                    if (img == null || IsTexture(img.Name, v.mainTexture)) { Res.VanillaMaterials++; return _mats[i] = v; }
                    var own = new Material(v) { name = g.Name ?? v.name };
                    var tex = Tex(g.BaseColorImage, false, null);
                    if (tex != null) own.mainTexture = tex;
                    return _mats[i] = own;
                }
                if (g.Extras != null && g.Extras.TryGetValue("terrainLayer", out var tl) && tl is string layer)
                {
                    var lm = TerrainLayerMaterial(layer);
                    if (lm != null) { Res.VanillaMaterials++; return _mats[i] = lm; }
                }

                var m = new Material(StandardShader()) { name = g.Name ?? "material" + i };
                var bc = new Color(g.BaseColor[0], g.BaseColor[1], g.BaseColor[2], 1f).gamma;
                bc.a = g.BaseColor[3];
                m.color = bc;
                m.SetFloat("_Metallic", g.Metallic);
                m.SetFloat("_Glossiness", 1f - g.Roughness);
                if (g.BaseColorImage >= 0) m.mainTexture = Tex(g.BaseColorImage, false, null);
                if (g.NormalImage >= 0)
                {
                    // RGB normal with A = 1 decodes correctly through UnpackNormalmapRGorAG
                    m.SetTexture("_BumpMap", Tex(g.NormalImage, true, null));
                    m.SetFloat("_BumpScale", g.NormalScale);
                    m.EnableKeyword("_NORMALMAP");
                }
                if (g.MetallicRoughnessImage >= 0)
                {
                    m.SetTexture("_MetallicGlossMap", Tex(g.MetallicRoughnessImage, true, px =>
                    {
                        for (int k = 0; k < px.Length; k++)
                        {
                            byte metal = (byte)Mathf.RoundToInt(px[k].b * g.Metallic);
                            byte smooth = (byte)Mathf.RoundToInt(255f - px[k].g * g.Roughness);
                            px[k] = new Color32(metal, metal, metal, smooth);
                        }
                    }));
                    m.SetFloat("_GlossMapScale", 1f);
                    m.EnableKeyword("_METALLICGLOSSMAP");
                }
                float emax = Mathf.Max(g.Emissive[0], Mathf.Max(g.Emissive[1], g.Emissive[2]));
                if (emax > 0f)
                {
                    m.SetColor("_EmissionColor", new Color(g.Emissive[0], g.Emissive[1], g.Emissive[2]).gamma);
                    if (g.EmissiveImage >= 0) m.SetTexture("_EmissionMap", Tex(g.EmissiveImage, false, null));
                    m.EnableKeyword("_EMISSION");
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                }
                if (g.AlphaMode == "MASK") StandardMode(m, 1, g.AlphaCutoff);
                else if (g.AlphaMode == "BLEND") StandardMode(m, 2, 0f);
                return _mats[i] = m;
            }

            // an image named as the texture is, or as an export names its file of it (<texture>_<8 hex digits>)
            private static bool IsTexture(string image, Texture t)
            {
                if (t == null || string.IsNullOrEmpty(image)) return t == null;
                if (image == t.name) return true;
                int u = image.LastIndexOf('_');
                return u > 0 && image.Length - u - 1 == 8 && image.Substring(0, u) == t.name
                    && image.Substring(u + 1).All(c => Uri.IsHexDigit(c));
            }

            private Material _default;
            private Material DefaultMaterial() => _default ?? (_default = new Material(StandardShader()) { name = "default" });

            private Texture2D Tex(int image, bool linear, Action<Color32[]> convert)
            {
                string key = image + ":" + linear + ":" + (convert != null ? convert.Method.Name + convert.Target?.GetHashCode() : "");
                if (_tex.TryGetValue(key, out var have)) return have;
                var img = _m.Images[image];
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = img.Name ?? "image" + image };
                if (img.Data == null || !ImageConversion.LoadImage(t, img.Data)) return _tex[key] = null;
                if (convert != null)
                {
                    var px = t.GetPixels32();
                    convert(px);
                    t.SetPixels32(px);
                    t.Apply(true);
                }
                return _tex[key] = t;
            }
        }

        // Standard shader blend modes, as its inspector sets them
        public static void StandardMode(Material m, int mode, float cutoff)
        {
            m.SetFloat("_Mode", mode);
            m.DisableKeyword("_ALPHATEST_ON");
            m.DisableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            switch (mode)
            {
                case 1:
                    m.SetInt("_SrcBlend", (int)BlendMode.One); m.SetInt("_DstBlend", (int)BlendMode.Zero); m.SetInt("_ZWrite", 1);
                    m.SetFloat("_Cutoff", cutoff);
                    m.EnableKeyword("_ALPHATEST_ON");
                    m.renderQueue = (int)RenderQueue.AlphaTest;
                    break;
                case 2:
                    m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha); m.SetInt("_ZWrite", 0);
                    m.EnableKeyword("_ALPHABLEND_ON");
                    m.renderQueue = (int)RenderQueue.Transparent;
                    break;
            }
        }

        private static Shader _standard;
        // Materials made only to be exported, with what identifies them in place of an Easy Save id (a terrain
        // layer's name): merged into their extras.
        public static readonly Dictionary<Material, Dictionary<string, object>> ExportExtras = new Dictionary<Material, Dictionary<string, object>>();

        // A terrain layer as a material (extras.terrainLayer): the layer's own textures and surface, the texture once
        // across the mesh's UVs. Terrain layers have no Easy Save id; the layer asset is known by its name.
        private static readonly Dictionary<string, Material> TerrainLayerMaterials = new Dictionary<string, Material>();
        public static Material TerrainLayerMaterial(string layer)
        {
            if (TerrainLayerMaterials.TryGetValue(layer, out var have) && have != null) return have;
            var l = Resources.FindObjectsOfTypeAll<TerrainLayer>().FirstOrDefault(x => x != null && x.name == layer);
            if (l == null || l.diffuseTexture == null) return null;
            var m = new Material(StandardShader()) { name = "terrain " + layer, mainTexture = l.diffuseTexture };
            m.SetFloat("_Metallic", l.metallic);
            m.SetFloat("_Glossiness", l.smoothness);
            if (l.normalMapTexture != null)
            {
                m.SetTexture("_BumpMap", l.normalMapTexture);
                m.SetFloat("_BumpScale", l.normalScale);
                m.EnableKeyword("_NORMALMAP");
            }
            return TerrainLayerMaterials[layer] = m;
        }

        public static Shader StandardShader()
        {
            if (_standard != null) return _standard;
            _standard = Shader.Find("Standard");
            if (_standard == null)
            {
                Plugin.Log.LogWarning("Standard shader not in this build; falling back to Legacy Diffuse");
                _standard = Shader.Find("Legacy Shaders/Diffuse");
            }
            return _standard;
        }

        private static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
        private static float[] Q(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
    }

    // GPU readback and PNG encoding: works for textures that are not CPU-readable
    internal static class TextureIO
    {
        public static Color32[] ReadBack(Texture tex, bool linear, out int w, out int h)
        {
            w = tex.width; h = tex.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
            t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            t.Apply(false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var px = t.GetPixels32();
            // immediate: a deferred Destroy keeps every texture alive until the frame ends
            UnityEngine.Object.DestroyImmediate(t);
            return px;
        }

        public static byte[] Png(Color32[] px, int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.SetPixels32(px);
            t.Apply(false);
            var png = ImageConversion.EncodeToPNG(t);
            UnityEngine.Object.DestroyImmediate(t);
            return png;
        }
    }

    // Small generated meshes in Unity winding: (p1 - p0) x (p2 - p0) points along the normal
    internal static class Shapes
    {
        public static GMesh Box(Vector3 c, Vector3 size, string name)
        {
            var g = new GMesh { Name = name };
            var p = new List<float>(); var n = new List<float>(); var uv = new List<float>(); var tri = new List<int>();
            var h = size / 2f;
            void Face(Vector3 nrm, Vector3 u, Vector3 v)
            {
                int b = p.Count / 3;
                var corners = new[] { c + nrm - u - v, c + nrm + u - v, c + nrm + u + v, c + nrm - u + v };
                var uvs = new[] { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };
                for (int k = 0; k < 4; k++)
                {
                    p.Add(corners[k].x); p.Add(corners[k].y); p.Add(corners[k].z);
                    var nn = nrm.normalized; n.Add(nn.x); n.Add(nn.y); n.Add(nn.z);
                    uv.Add(uvs[k * 2]); uv.Add(uvs[k * 2 + 1]);
                }
                tri.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }
            Vector3 X = new Vector3(h.x, 0, 0), Y = new Vector3(0, h.y, 0), Z = new Vector3(0, 0, h.z);
            Face(X, Y, Z); Face(-X, Z, Y);
            Face(Y, Z, X); Face(-Y, X, Z);
            Face(Z, X, Y); Face(-Z, Y, X);
            g.Positions = p.ToArray(); g.Normals = n.ToArray(); g.Uvs = uv.ToArray();
            g.Submeshes.Add(tri.ToArray()); g.SubmeshMaterials.Add(-1);
            return g;
        }

        public static GMesh Sphere(float r, int rings, int segs, string name)
        {
            var g = new GMesh { Name = name };
            var p = new List<float>(); var n = new List<float>(); var uv = new List<float>(); var tri = new List<int>();
            for (int i = 0; i <= rings; i++)
            {
                float th = Mathf.PI * i / rings;
                for (int j = 0; j <= segs; j++)
                {
                    float ph = 2f * Mathf.PI * j / segs;
                    var d = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                    p.Add(d.x * r); p.Add(d.y * r); p.Add(d.z * r);
                    n.Add(d.x); n.Add(d.y); n.Add(d.z);
                    uv.Add((float)j / segs); uv.Add(1f - (float)i / rings);
                }
            }
            int row = segs + 1;
            for (int i = 0; i < rings; i++)
                for (int j = 0; j < segs; j++)
                {
                    int a = i * row + j, b = a + 1, c = a + row, d = c + 1;
                    Add(tri, p, n, a, c, b);
                    Add(tri, p, n, b, c, d);
                }
            g.Positions = p.ToArray(); g.Normals = n.ToArray(); g.Uvs = uv.ToArray();
            g.Submeshes.Add(tri.ToArray()); g.SubmeshMaterials.Add(-1);
            return g;
        }

        // adds a triangle, flipped if needed so its face normal agrees with the vertex normals
        private static void Add(List<int> tri, List<float> p, List<float> n, int a, int b, int c)
        {
            Vector3 P(int i) => new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
            Vector3 N(int i) => new Vector3(n[i * 3], n[i * 3 + 1], n[i * 3 + 2]);
            var f = Vector3.Cross(P(b) - P(a), P(c) - P(a));
            if (f.sqrMagnitude < 1e-12f) return; // degenerate at the poles
            if (Vector3.Dot(f, N(a) + N(b) + N(c)) < 0) { int t = b; b = c; c = t; }
            tri.Add(a); tri.Add(b); tri.Add(c);
        }
    }
}
