using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ES3Internal;
using HarmonyLib;
using NWH.WheelController3D;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SigmaMod
{
    // Turns every .glb template in assets/ into a game prefab: vehicles, parts and items alike.
    //
    // A template's root carries es3prefab, the Easy Save prefab id of the vanilla prefab it was
    // exported from (the donor). The donor is cloned and the template applied onto the clone:
    //  - a node whose es3local matches an object of the donor is that object: transform, mesh and
    //    materials come from the template, parenting follows the template;
    //  - collider nodes matching a donor collider update it; donor colliders the template dropped are
    //    disabled;
    //  - nodes made in Blender (no components list, no ids) are added as new objects, colliders
    //    included;
    //  - nodes exported from the game that the donor prefab does not have (fitted parts, cargo,
    //    objects created at runtime) are skipped: they are separate prefabs or runtime state;
    //  - donor objects the template dropped keep their logic but stop rendering.
    // Active state and renderer state of donor objects stay as the donor has them, because game FSMs
    // drive those at runtime.
    //
    // Identity: root custom property sigma_id (else the file name) is the template id; the ES3 prefab
    // id is a hash of it, and everything new gets stable ids, so saves map the same way every session.
    // sigma_name is the display name (ItemName FSM, so Apocaspawner and hover text show it).
    // The prefab is an inactive root object kept across scenes, which is where Apocaspawner's catalog
    // looks; it is registered with each scene's reference manager on load, before the save is read.
    //
    // Injection: every instance, however it was made (Spawn, the game's spawners, Apocaspawner, a save loading), fires
    // Instantiated from ES3Prefab.Awake. A node's custom property sigma_behaviour (a name, or several
    // separated by commas) calls the handlers registered under that name with the instance object and
    // the node's custom properties.
    public static class CustomPrefabs
    {
        public sealed class Info
        {
            public string Id;            // sigma_id or file name
            public string Name;          // display name
            public long PrefabId;        // Easy Save prefab id
            public string Path;
            public string Mod;           // mod that ships the file (sigma.json "name" or its folder name)
            public string DonorName;
            public bool IsVehicle;
            public GameObject Prefab;    // template: active itself, kept asleep under an inactive holder, like a vanilla prefab asset
            public GameObject Donor;     // the game prefab its objects come from
            public bool Spawns;          // joins the game's spawn lists that hold Donor (root sigma_spawn, default true)
            // custom properties of each template node, by the ES3 local id of the object it became
            public readonly Dictionary<long, Dictionary<string, object>> NodeExtras = new Dictionary<long, Dictionary<string, object>>();
            // sigma_id of non-root nodes (unique within the template) -> ES3 local id
            public readonly Dictionary<string, long> NodeIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            internal ES3Prefab Es3;
            internal DateTime Stamp;
            internal readonly List<KeyValuePair<long, UnityEngine.Object>> Refs = new List<KeyValuePair<long, UnityEngine.Object>>();
        }

        public const string IdKey = "sigma_id", NameKey = "sigma_name", BehaviourKey = "sigma_behaviour", TypeKey = "sigma_type", SpawnKey = "sigma_spawn";

        private sealed class FileEntry { public DateTime Stamp; public readonly List<Info> Templates = new List<Info>(); }
        private static readonly Dictionary<string, FileEntry> ByPath = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<long, Info> ByPrefabId = new Dictionary<long, Info>();
        private static readonly Dictionary<string, List<Action<GameObject, Dictionary<string, object>, Info>>> Behaviours =
            new Dictionary<string, List<Action<GameObject, Dictionary<string, object>, Info>>>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> UnknownBehaviours = new HashSet<string>();
        private static Transform _workbench;

        // ---------------------------------------------------------------- public API

        // fired for every new instance of a custom prefab (root object, its template)
        public static event Action<GameObject, Info> Instantiated;

        public static IEnumerable<Info> All => ByPath.Values.SelectMany(f => f.Templates);

        public static Info Find(string id) => All.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

        // the template behind any object of a custom prefab instance
        public static bool TryGet(GameObject go, out Info info)
        {
            info = null;
            var es3 = go != null ? go.GetComponentInParent<ES3Prefab>() : null;
            return es3 != null && ByPrefabId.TryGetValue(es3.prefabId, out info);
        }

        // the custom properties of the template node an instance object came from
        public static bool TryGetExtras(GameObject go, out Dictionary<string, object> extras)
        {
            extras = null;
            if (!TryGet(go, out var info)) return false;
            var es3 = go.GetComponentInParent<ES3Prefab>();
            return es3.localRefs.TryGetValue(go, out long id) && info.NodeExtras.TryGetValue(id, out extras);
        }

        // the object of an instance whose template node has sigma_id = id; any object of the instance will do
        public static GameObject FindNode(GameObject instance, string id)
        {
            if (!TryGet(instance, out var info) || !info.NodeIds.TryGetValue(id, out long localId)) return null;
            var es3 = instance.GetComponentInParent<ES3Prefab>();
            foreach (var kv in es3.localRefs)
                if (kv.Value == localId && kv.Key is GameObject go) return go;
            return null;
        }

        // the sigma_id of the template node an instance object came from
        public static string IdOf(GameObject go) =>
            TryGetExtras(go, out var ex) && ex.TryGetValue(IdKey, out var v) ? v as string : null;

        public static void RegisterBehaviour(string name, Action<GameObject, Dictionary<string, object>, Info> attach)
        {
            if (!Behaviours.TryGetValue(name, out var list)) Behaviours[name] = list = new List<Action<GameObject, Dictionary<string, object>, Info>>();
            list.Add(attach);
        }

        // ---------------------------------------------------------------- registration

        internal static void Init()
        {
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                var mgr = ES3ReferenceMgrBase.GetManagerFromScene(scene, false);
                if (mgr == null) return;
                try { RegisterAssets(mgr); }
                catch (Exception e) { Plugin.Log.LogError("custom prefabs: " + e); }
            };
        }

        private static void RegisterAssets(ES3ReferenceMgrBase mgr)
        {
            _donorIndex = null;
            int n = 0;
            foreach (var src in AssetSources.Find())
                foreach (var path in Directory.GetFiles(src.Dir, "*.glb", SearchOption.AllDirectories))
                {
                    List<Info> list;
                    try { list = Get(path, mgr); }
                    catch (Exception e) { Plugin.Log.LogError("custom prefab " + path + ": " + e); continue; }
                    if (list.Count == 0)
                        Plugin.Log.LogWarning($"custom prefab: {src.Mod}/{Path.GetFileName(path)}: nothing loaded (no root object with {IdKey}; was Include > Custom Properties on in the export?)");
                    foreach (var t in list) { t.Mod = src.Mod; Register(t, mgr); n++; }
                }
            if (n > 0)
            {
                SpawnLists.Join(All.Where(i => i.Spawns));
                InvalidateApocaspawner();
            }
        }

        private static void Register(Info t, ES3ReferenceMgrBase mgr)
        {
            mgr.AddPrefab(t.Es3);
            foreach (var kv in t.Refs) mgr.Add(kv.Value, kv.Key);
        }

        // Apocaspawner caches its catalog; ask it to rebuild so new prefabs show up (soft dependency)
        private static void InvalidateApocaspawner()
        {
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Apocaspawner.Catalog", false)).FirstOrDefault(x => x != null);
                type?.GetMethod("Invalidate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, null);
            }
            catch (Exception e) { Plugin.Verbose("Apocaspawner catalog not invalidated: " + e.Message); }
        }

        // Builds (or rebuilds when the file changed) the prefabs of a file: one per root object that has a
        // sigma_id. Root objects without one are not loaded (reference meshes, scraps, stray colliders).
        private static string JsonPath(string glb) => Path.ChangeExtension(glb, ".json");

        private static List<Info> Get(string path, ES3ReferenceMgrBase mgr)
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (File.Exists(JsonPath(path)) && File.GetLastWriteTimeUtc(JsonPath(path)) > stamp) stamp = File.GetLastWriteTimeUtc(JsonPath(path));
            if (ByPath.TryGetValue(path, out var have) && have.Stamp == stamp) return have.Templates;
            if (have != null)
                foreach (var old in have.Templates)
                {
                    ByPrefabId.Remove(old.PrefabId);
                    if (old.Prefab != null) UnityEngine.Object.Destroy(old.Prefab);
                }
            var entry = new FileEntry { Stamp = stamp };
            ByPath[path] = entry;

            var model = GltfReader.Load(path);
            Dictionary<string, object> overrides = null;
            if (File.Exists(JsonPath(path)))
            {
                try { overrides = Json.Parse(File.ReadAllText(JsonPath(path))) as Dictionary<string, object>; }
                catch (Exception e) { Plugin.Log.LogError($"custom prefab: {JsonPath(path)}: {e.Message}"); }
            }
            foreach (int root in model.Roots)
            {
                var rootNode = model.Nodes[root];
                var rootEx = rootNode.Extras;
                if (rootEx == null || !(rootEx.TryGetValue(IdKey, out var iv) && iv is string ids && ids.Trim().Length > 0))
                {
                    Plugin.Verbose($"custom prefab: {Path.GetFileName(path)}: root '{rootNode.Name}' has no {IdKey}, not loaded");
                    continue;
                }
                string id = ids.Trim();
                long prefabId = StableId(Plugin.GUID + "/prefab/" + id);
                if (ByPrefabId.TryGetValue(prefabId, out var clash))
                {
                    Plugin.Log.LogWarning($"custom prefab: {path}: id '{id}' is already used by {clash.Path}; skipped");
                    continue;
                }
                var donor = Donor(model, root, mgr, out string how);
                if (donor == null)
                {
                    Plugin.Log.LogWarning($"custom prefab: {path}: '{id}': no game prefab found to build on ({how})");
                    continue;
                }
                if (Math.Abs(rootNode.S[0] - 1) > 1e-3 || Math.Abs(rootNode.S[1] - 1) > 1e-3 || Math.Abs(rootNode.S[2] - 1) > 1e-3)
                    Plugin.Log.LogWarning($"custom prefab: '{id}': root object is scaled {rootNode.S[0]},{rootNode.S[1]},{rootNode.S[2]}; " +
                                          "the root transform is not used, apply the scale in Blender (Ctrl+A)");
                Dictionary<string, object> spec = null;
                if (overrides != null && overrides.TryGetValue(id, out var sv) && !(sv is Dictionary<string, object>))
                    Plugin.Log.LogWarning($"custom prefab: {JsonPath(path)}: '{id}' must be an object");
                else if (overrides != null && overrides.TryGetValue(id, out sv)) spec = (Dictionary<string, object>)sv;
                var t = Assemble(model, root, path, id, prefabId, rootEx, donor, mgr, spec);
                t.Stamp = stamp;
                entry.Templates.Add(t);
                ByPrefabId[prefabId] = t;
                Plugin.Verbose($"custom prefab: '{id}' donor found by {how}");
            }
            return entry.Templates;
        }

        // The game prefab a template builds on. An unchanged export names it on its root (es3prefab).
        // Otherwise the objects say it themselves: every es3local id belongs to exactly one game prefab,
        // and all of them must belong to the same one. Ids from different prefabs refuse the template.
        private static Dictionary<long, ES3Prefab> _donorIndex;

        private static ES3Prefab Donor(GltfModel model, int root, ES3ReferenceMgrBase mgr, out string how)
        {
            ES3Prefab named = null;
            var ex = model.Nodes[root].Extras;
            if (ex != null && ex.TryGetValue("es3prefab", out var dv) && dv is string ds && long.TryParse(ds, out long donorId))
            {
                named = mgr.GetPrefab(donorId, true);
                if (named == null) { how = $"root es3prefab {donorId} is not a loaded game prefab"; return null; }
            }
            if (_donorIndex == null)
            {
                _donorIndex = new Dictionary<long, ES3Prefab>();
                foreach (var p in mgr.prefabs)
                    if (p != null && !ByPrefabId.ContainsKey(p.prefabId))
                        foreach (var kv in p.localRefs) _donorIndex[kv.Value] = p;
            }
            var owners = new Dictionary<ES3Prefab, List<string>>();
            var unknown = new List<string>();
            void Walk(int i)
            {
                var n = model.Nodes[i];
                var e = n.Extras;
                if (i != root && e != null && e.ContainsKey("es3prefab")) return; // a fitted part: its own prefab, skipped later
                if (e != null && e.TryGetValue("es3local", out var lv) && lv is string ls && long.TryParse(ls, out long l))
                {
                    if (_donorIndex.TryGetValue(l, out var p)) { if (!owners.TryGetValue(p, out var list)) owners[p] = list = new List<string>(); list.Add(n.Name); }
                    else unknown.Add(n.Name);
                }
                foreach (int c in n.Children) Walk(c);
            }
            Walk(root);
            if (unknown.Count > 0)
                Plugin.Log.LogWarning($"custom prefab: {unknown.Count} objects have ids no game prefab owns (runtime objects, skipped): {string.Join(", ", unknown.Take(12).ToArray())}");
            if (named != null) owners.Remove(named);
            if (named == null && owners.Count == 1) { var only = owners.Keys.First(); how = $"its objects' ids, all from '{only.name}'"; return only; }
            if (named != null && owners.Count == 0) { how = $"root es3prefab ('{named.name}')"; return named; }
            if (owners.Count == 0) { how = "no es3prefab on the root and no object with an es3local id"; return null; }
            how = "objects come from different game prefabs: " + string.Join("; ", owners.Select(o => $"'{o.Key.name}': {string.Join(", ", o.Value.Take(8).ToArray())}{(o.Value.Count > 8 ? " ..." : "")}").ToArray())
                  + (named != null ? $" (root names '{named.name}')" : "");
            return null;
        }

        private static Info Assemble(GltfModel model, int root, string path, string id, long prefabId, Dictionary<string, object> rootEx,
                                     ES3Prefab donor, ES3ReferenceMgrBase mgr, Dictionary<string, object> overrides)
        {
            // built under an inactive parent so no Awake runs, then kept as an inactive root object
            if (_workbench == null)
            {
                var h = new GameObject(Plugin.NAME + " workbench");
                h.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(h);
                _workbench = h.transform;
            }
            string name = rootEx.TryGetValue(NameKey, out var nv) && nv is string ns && ns.Trim().Length > 0 ? ns.Trim() : id;
            var go = UnityEngine.Object.Instantiate(donor.gameObject, _workbench);
            go.name = id;
            var es3 = go.GetComponent<ES3Prefab>();
            es3.prefabId = prefabId;
            var t = new Info
            {
                Id = id, Name = name, PrefabId = prefabId, Path = path, DonorName = donor.name, Prefab = go, Es3 = es3,
                IsVehicle = go.GetComponent<NWH.VehiclePhysics2.VehicleController>() != null, Donor = donor.gameObject,
                Spawns = !(rootEx.TryGetValue(SpawnKey, out var spv) && (spv is bool spb && !spb || spv is double spd && spd == 0 || spv is string sps && sps.Trim().ToLowerInvariant() == "false"))
            };

            // donor objects by their local id (Instantiate remaps the dictionary onto the clone)
            var byId = new Dictionary<long, UnityEngine.Object>();
            foreach (var kv in es3.localRefs) if (kv.Key != null) byId[kv.Value] = kv.Key;

            // A root with no children is a variant: the game prefab as it is, with its own id, name and .json
            // values. Otherwise the template is applied object by object.
            bool variant = model.Nodes[root].Children.Count == 0 && model.Nodes[root].Mesh < 0;
            var a = new Applier(model, es3, byId, prefabId, mgr, t.NodeExtras);
            if (variant)
            {
                if (es3.localRefs.TryGetValue(go, out long variantRoot)) t.NodeExtras[variantRoot] = rootEx;
                Plugin.Log.LogInfo($"custom prefab: '{id}' is a variant of '{donor.name}' (no objects in the template)");
            }
            else
            {
                a.Node(root, null, go);
                a.Finish(go);
                FsmOffsets.Apply(go, a.Moved, "custom prefab '" + id + "'");
            }

            // node ids: sigma_id on any node but the root, unique within the template
            long rootLocal = es3.localRefs.TryGetValue(go, out long rl) ? rl : -1;
            foreach (var kv in t.NodeExtras)
            {
                if (kv.Key == rootLocal || !(kv.Value.TryGetValue(IdKey, out var nv2) && nv2 is string nid) || nid.Trim().Length == 0) continue;
                if (t.NodeIds.ContainsKey(nid.Trim())) Plugin.Log.LogWarning($"custom prefab: {path}: sigma_id '{nid}' is used on more than one object; the first is kept");
                else t.NodeIds[nid.Trim()] = kv.Key;
            }

            string type = rootEx.TryGetValue(TypeKey, out var tv) ? tv as string : null;
            bool chassis = string.Equals(type, "chassis", StringComparison.OrdinalIgnoreCase);
            Dictionary<string, string> donorValues = null, resetValues = null;
            if (chassis)
            {
                donorValues = ValueReport.Snapshot(go);
                NewChassis(go, id, overrides);
                resetValues = ValueReport.Snapshot(go);
            }
            else if (type != null) Plugin.Log.LogWarning($"custom prefab: '{id}': unknown {TypeKey} '{type}'");

            if (overrides != null)
                Overrides.Apply(go, overrides, nid => t.NodeIds.TryGetValue(nid, out long l) ? es3.localRefs.Where(kv => kv.Value == l).Select(kv => kv.Key).OfType<GameObject>().FirstOrDefault() : null,
                                Path.GetFileName(JsonPath(path)) + "/" + id);
            if (chassis)
                try { ValueReport.Write(id, donorValues, resetValues, ValueReport.Snapshot(go)); }
                catch (Exception e) { Plugin.Log.LogWarning($"custom prefab: '{id}': chassis value report failed: {e.Message}"); }

            foreach (var kv in a.Builder.Created())
                t.Refs.Add(new KeyValuePair<long, UnityEngine.Object>(StableId(prefabId + "/" + kv.Key + "/" + a.Occurrence(kv.Key)), kv.Value));

            // display name where the game and Apocaspawner read it
            foreach (var fsm in go.GetComponents<PlayMakerFSM>())
                if (fsm.FsmName == "ItemName")
                {
                    var v = fsm.FsmVariables.GetFsmString("ItemName");
                    if (v != null) v.Value = name;
                }

            // stays under the inactive holder: copies made by Instantiate come out active, exactly like copies of
            // a vanilla prefab asset, so the game's own spawners, Easy Save and other mods can use it directly
            Plugin.Log.LogInfo($"custom prefab: '{id}' ({name}) from donor '{donor.name}', prefab id {prefabId}" + (variant ? "" : ": " + a.Stats));
            return t;
        }

        // sigma_type = chassis: the root's Rigidbody and VehicleController start from defaults, not from the
        // game vehicle the parts came from. NWH's SetDefaults rebuilds the drivetrain (axles, steering,
        // brakes, differentials, engine, clutch, gearbox) from the WheelControllers under the chassis and
        // keeps asset references (clips, mixer, ground preset). The components stay the same objects
        // because FSMs (input, pedals, handbrake, audio) hold references to them.
        private static void NewChassis(GameObject go, string id, Dictionary<string, object> overrides)
        {
            var rb = go.GetComponent<Rigidbody>();
            var vc = go.GetComponent<NWH.VehiclePhysics2.VehicleController>();
            if (rb == null || vc == null) { Plugin.Log.LogWarning($"custom prefab: '{id}': chassis root has no {(rb == null ? "Rigidbody" : "VehicleController")}"); return; }
            rb.mass = 1f; rb.drag = 0f; rb.angularDrag = 0.05f;
            rb.useGravity = true; rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb.constraints = RigidbodyConstraints.None;
            rb.ResetCenterOfMass();
            rb.ResetInertiaTensor();
            vc.SetDefaults();
            string drive = Drive(vc.powertrain, overrides, id);
            Plugin.Log.LogInfo($"custom prefab: '{id}': new chassis, {vc.powertrain.wheels.Count} wheels in {vc.powertrain.wheelGroups.Count} axles, {drive} drive");
            if (overrides == null || !overrides.TryGetValue("Rigidbody", out var r) || !(r is Dictionary<string, object> rd) || !rd.ContainsKey("mass"))
                Plugin.Log.LogWarning($"custom prefab: '{id}': chassis mass is the default 1 kg; set Rigidbody.mass in the .json");
        }

        // Drive layout from "drive" in the chassis .json. SetDefaults builds a front, a rear and a centre
        // differential (all open) with the gearbox on the centre one: all-wheel drive, 50/50. Front or rear
        // drive puts the gearbox straight onto that axle's differential and drops the other two, so the
        // undriven axle's wheels only steer and brake.
        private static string Drive(NWH.VehiclePhysics2.Powertrain.Powertrain pt, Dictionary<string, object> overrides, string id)
        {
            string drive = overrides != null && overrides.TryGetValue("drive", out var d) ? (d as string)?.Trim().ToLowerInvariant() : null;
            if (drive == null)
            {
                Plugin.Log.LogWarning($"custom prefab: '{id}': no \"drive\" in the .json (rear, front or all); all-wheel drive");
                return "all-wheel";
            }
            int axle = drive == "front" ? 0 : drive == "rear" ? 1 : drive == "all" ? -1 : -2;
            if (axle == -2) { Plugin.Log.LogWarning($"custom prefab: '{id}': unknown drive '{drive}' (rear, front or all); all-wheel drive"); return "all-wheel"; }
            if (axle < 0) return "all-wheel";
            if (pt.differentials.Count != 3) { Plugin.Log.LogWarning($"custom prefab: '{id}': {pt.differentials.Count} differentials after setup, expected 3; all-wheel drive"); return "all-wheel"; }
            var diff = pt.differentials[axle];
            pt.transmission.Output = diff;
            pt.differentials = new List<NWH.VehiclePhysics2.Powertrain.DifferentialComponent> { diff };
            return drive + "-wheel";
        }

        // NWH finds wheels with GetComponentsInChildren<WheelUAPI>(), which skips inactive objects; templates are
        // inactive, so SetDefaults would find none. Only SetDefaults calls it (in the editor: Reset).
        [HarmonyLib.HarmonyPatch(typeof(NWH.VehiclePhysics2.Powertrain.Powertrain), "VC_SetDefaults")]
        private static class PowertrainWheelsPatch
        {
            private static IEnumerable<HarmonyLib.CodeInstruction> Transpiler(IEnumerable<HarmonyLib.CodeInstruction> code)
            {
                var find = HarmonyLib.AccessTools.Method(typeof(Component), nameof(Component.GetComponentsInChildren), Type.EmptyTypes,
                                                         new[] { typeof(NWH.Common.Vehicles.WheelUAPI) });
                var with = HarmonyLib.AccessTools.Method(typeof(PowertrainWheelsPatch), nameof(Wheels));
                int n = 0;
                foreach (var c in code)
                {
                    if (c.Calls(find)) { n++; yield return new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Call, with); }
                    else yield return c;
                }
                if (n == 0) Plugin.Log.LogWarning("Powertrain.VC_SetDefaults: wheel lookup not found; chassis on inactive templates will have no wheels");
            }

            private static NWH.Common.Vehicles.WheelUAPI[] Wheels(Component c) => c.GetComponentsInChildren<NWH.Common.Vehicles.WheelUAPI>(true);
        }

        // ---------------------------------------------------------------- instances

        // ES3Prefab.Awake runs for every new instance: spawned, loaded from a save, or made by another mod
        [HarmonyLib.HarmonyPatch(typeof(ES3Prefab), "Awake")]
        private static class AwakePatch
        {
            private static void Postfix(ES3Prefab __instance)
            {
                if (!ByPrefabId.TryGetValue(__instance.prefabId, out var info) || __instance.gameObject == info.Prefab) return;
                try { OnInstance(__instance, info); }
                catch (Exception e) { Plugin.Log.LogError($"custom prefab {info.Id}: instance setup failed: {e}"); }
            }
        }

        private static void OnInstance(ES3Prefab es3, Info info)
        {
            Plugin.Verbose($"custom prefab: instance {es3.name} of '{info.Id}'");
            Instantiated?.Invoke(es3.gameObject, info);
            foreach (var kv in es3.localRefs)
            {
                if (!(kv.Key is GameObject obj) || !info.NodeExtras.TryGetValue(kv.Value, out var ex)) continue;
                if (!ex.TryGetValue(BehaviourKey, out var bv) || !(bv is string names)) continue;
                foreach (var raw in names.Split(','))
                {
                    string b = raw.Trim();
                    if (b.Length == 0) continue;
                    if (Behaviours.TryGetValue(b, out var handlers))
                        foreach (var h in handlers) h(obj, ex, info);
                    else if (UnknownBehaviours.Add(b))
                        Plugin.Log.LogWarning($"custom prefab {info.Id}: no behaviour registered as '{b}' (node {obj.name})");
                }
            }
        }

        private sealed class Applier
        {
            public readonly GltfUnity.Builder Builder;
            private readonly GltfModel _m;
            private readonly ES3Prefab _es3;
            private readonly Dictionary<long, UnityEngine.Object> _byId;
            private readonly long _prefabId;
            private readonly HashSet<UnityEngine.Object> _seen = new HashSet<UnityEngine.Object>();
            private readonly Dictionary<string, int> _occ = new Dictionary<string, int>();
            private int _mapped, _added, _skipped, _collidersMapped, _collidersAdded;
            public string Stats => $"{_mapped} objects mapped, {_added} added, {_skipped} skipped (fitted parts, runtime objects), " +
                                   $"colliders {_collidersMapped} mapped, {_collidersAdded} added, {_hidden} donor renderers hidden, {_disabled} donor colliders disabled";
            private int _hidden, _disabled;

            private readonly Dictionary<long, Dictionary<string, object>> _nodeExtras;
            // game objects the template moved, by how far (in their parent's space)
            public readonly Dictionary<Transform, Vector3> Moved = new Dictionary<Transform, Vector3>();

            public Applier(GltfModel m, ES3Prefab es3, Dictionary<long, UnityEngine.Object> byId, long prefabId, ES3ReferenceMgrBase mgr,
                           Dictionary<long, Dictionary<string, object>> nodeExtras)
            {
                _m = m; _es3 = es3; _byId = byId; _prefabId = prefabId; _nodeExtras = nodeExtras;
                Builder = new GltfUnity.Builder(m) { Inert = true, Mgr = mgr };
            }

            public int Occurrence(string key)
            {
                _occ.TryGetValue(key, out int n);
                _occ[key] = n + 1;
                return n;
            }

            private T Mapped<T>(Dictionary<string, object> ex) where T : UnityEngine.Object =>
                ex != null && ex.TryGetValue("es3local", out var v) && v is string s && long.TryParse(s, out long id) && _byId.TryGetValue(id, out var o) ? o as T : null;

            private static bool FromGame(Dictionary<string, object> ex) =>
                ex != null && (ex.ContainsKey("components") || ex.ContainsKey("es3local") || ex.ContainsKey("es3global"));

            // path from the root, for stable ids of new objects
            private string PathOf(int i) => _m.Nodes[i].Parent < 0 ? "" : PathOf(_m.Nodes[i].Parent) + "/" + _m.Nodes[i].Name;

            public void Node(int i, Transform parent, GameObject rootTarget)
            {
                var n = _m.Nodes[i];
                var ex = n.Extras ?? new Dictionary<string, object>();
                if (ex.TryGetValue(GltfUnity.MarkerKey, out var mk) && (mk as string) == GltfUnity.ColliderMarker)
                {
                    Collider(i, parent.gameObject);
                    return;
                }

                GameObject target;
                if (rootTarget != null) target = rootTarget;
                else
                {
                    if (ex.ContainsKey("es3prefab")) { _skipped++; return; } // a fitted part or cargo: its own prefab
                    target = Mapped<GameObject>(ex);
                    if (target == null && FromGame(ex)) { _skipped++; return; } // runtime object the donor prefab lacks
                    if (target == null)
                    {
                        target = new GameObject(n.Name ?? "node" + i);
                        target.transform.SetParent(parent, false);
                        target.layer = parent.gameObject.layer;
                        if (ex.TryGetValue("layer", out var l) && l is double ld) target.layer = (int)ld;
                        Local(target, PathOf(i));
                        Local(target.transform, PathOf(i) + "#transform");
                        if (ex.TryGetValue("active", out var av) && av is bool ab) target.SetActive(ab);
                        _added++;
                    }
                    else
                    {
                        bool sameParent = target.transform.parent == parent;
                        if (!sameParent) target.transform.SetParent(parent, false);
                        var d = new Vector3(n.T[0], n.T[1], n.T[2]) - target.transform.localPosition;
                        if (sameParent && d.sqrMagnitude > 1e-8f) Moved[target.transform] = d;
                        _mapped++;
                    }
                    target.transform.localPosition = new Vector3(n.T[0], n.T[1], n.T[2]);
                    target.transform.localRotation = new Quaternion(n.R[0], n.R[1], n.R[2], n.R[3]);
                    target.transform.localScale = new Vector3(n.S[0], n.S[1], n.S[2]);
                }
                _seen.Add(target);
                if (_es3.localRefs.TryGetValue(target, out long localId)) _nodeExtras[localId] = ex;

                if (target.GetComponent<SkinnedMeshRenderer>() != null)
                {
                    // a skinned mesh (the seated player model) is drawn by its bones: leave it alone. The
                    // export holds its bind pose, which as a static mesh is a T-pose.
                }
                else if (n.Mesh >= 0)
                {
                    var mesh = Builder.Mesh(n.Mesh);
                    var mf = target.GetComponent<MeshFilter>();
                    var mr = target.GetComponent<MeshRenderer>();
                    if (mf == null) { mf = target.AddComponent<MeshFilter>(); Local(mf, PathOf(i) + "#MeshFilter"); }
                    if (mr == null) { mr = target.AddComponent<MeshRenderer>(); Local(mr, PathOf(i) + "#MeshRenderer"); }
                    mf.sharedMesh = mesh;
                    mr.sharedMaterials = Builder.Materials(n.Mesh, mesh);
                }
                else
                {
                    var mr = target.GetComponent<MeshRenderer>();
                    if (mr != null && mr.enabled) { mr.enabled = false; _hidden++; }
                }

                ApplyData(target, ex);
                foreach (int c in n.Children) Node(c, target.transform, null);
            }

            private void ApplyData(GameObject target, Dictionary<string, object> ex)
            {
                if (ex.TryGetValue("rigidbody", out var rv) && rv is Dictionary<string, object> rd)
                {
                    var rb = target.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        if (rd.TryGetValue("mass", out var mv) && mv is double md) rb.mass = (float)md;
                        if (rd.TryGetValue("centerOfMass", out var cv) && cv is List<object> cl && cl.Count == 3)
                            rb.centerOfMass = new Vector3(Convert.ToSingle(cl[0]), Convert.ToSingle(cl[1]), Convert.ToSingle(cl[2]));
                    }
                }
                if (ex.TryGetValue("wheel", out var wv) && wv is Dictionary<string, object> wd)
                {
                    var wc = target.GetComponent<WheelController>();
                    if (wc != null)
                    {
                        if (wd.TryGetValue("radius", out var v) && v is double d) wc.wheel.radius = (float)d;
                        if (wd.TryGetValue("width", out v) && v is double d2) wc.wheel.width = (float)d2;
                        if (wd.TryGetValue("mass", out v) && v is double d3) wc.wheel.mass = (float)d3;
                        if (wd.TryGetValue("springMaxLength", out v) && v is double d4) wc.spring.maxLength = (float)d4;
                        if (wd.TryGetValue("springMaxForce", out v) && v is double d5) wc.spring.maxForce = (float)d5;
                    }
                }
            }

            private void Collider(int i, GameObject owner)
            {
                var n = _m.Nodes[i];
                var ex = n.Extras;
                string shape = ex.TryGetValue("shape", out var sv) ? sv as string : "box";
                bool trigger = ex.TryGetValue("trigger", out var tv) && tv is bool tb && tb;
                var c = Mapped<Collider>(ex);
                if (c == null && FromGame(ex)) { _skipped++; return; }
                var center = new Vector3(n.T[0], n.T[1], n.T[2]);
                var size = new Vector3(n.S[0], n.S[1], n.S[2]);
                bool rotated = Quaternion.Angle(Quaternion.identity, new Quaternion(n.R[0], n.R[1], n.R[2], n.R[3])) > 0.1f;

                if (c == null)
                {
                    // a collider drawn in Blender: rotated ones get their own child object
                    var host = owner;
                    if (rotated)
                    {
                        host = new GameObject(n.Name ?? "collider");
                        host.transform.SetParent(owner.transform, false);
                        host.transform.localPosition = center;
                        host.transform.localRotation = new Quaternion(n.R[0], n.R[1], n.R[2], n.R[3]);
                        host.layer = owner.layer;
                        Local(host, PathOf(i));
                        Local(host.transform, PathOf(i) + "#transform");
                        center = Vector3.zero;
                    }
                    switch (shape)
                    {
                        case "sphere": c = host.AddComponent<SphereCollider>(); break;
                        case "capsule": c = host.AddComponent<CapsuleCollider>(); break;
                        case "mesh": c = host.AddComponent<MeshCollider>(); break;
                        default: c = host.AddComponent<BoxCollider>(); break;
                    }
                    Local(c, PathOf(i) + "#collider");
                    _collidersAdded++;
                }
                else _collidersMapped++;
                _seen.Add(c);

                switch (c)
                {
                    case BoxCollider b: b.center = center; b.size = size; break;
                    case SphereCollider s: s.center = center; s.radius = Mathf.Max(size.x, Mathf.Max(size.y, size.z)); break;
                    case CapsuleCollider cc:
                    {
                        int dir = ex.TryGetValue("direction", out var dv) && dv is double dd ? (int)dd : cc.direction;
                        cc.direction = dir;
                        cc.center = center;
                        cc.height = size[dir];
                        cc.radius = Mathf.Max(size[(dir + 1) % 3], size[(dir + 2) % 3]) / 2f;
                        break;
                    }
                    case MeshCollider mc:
                        if (n.Mesh >= 0) mc.sharedMesh = Builder.Mesh(n.Mesh);
                        mc.convex = ex.TryGetValue("convex", out var cv) && cv is bool cb && cb;
                        break;
                }
                c.isTrigger = trigger;
            }

            // donor objects the template no longer has: stop drawing / colliding, keep their logic
            public void Finish(GameObject root)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (_seen.Contains(t.gameObject)) continue;
                    var mr = t.GetComponent<MeshRenderer>();
                    if (mr != null && mr.enabled) { mr.enabled = false; _hidden++; }
                }
                foreach (var c in root.GetComponentsInChildren<Collider>(true))
                {
                    if (_seen.Contains(c) || c is WheelCollider || !_byId.ContainsValue(c)) continue;
                    if (c.enabled) { c.enabled = false; _disabled++; }
                }
            }

            // a stable local id for a new object, so saves map it the same way every session
            private void Local(UnityEngine.Object o, string key)
            {
                long id = StableId(_prefabId + "/local" + key + "/" + Occurrence("local" + key));
                _es3.localRefs[o] = id;
            }
        }

        // ---------------------------------------------------------------- spawning

        // Spawns a copy of a template the way the game's spawners do: a unique item name from the game's
        // counter, and an entry in the save lists (cars or items) so the copy is saved with the game.
        // Returns the copy, or null when the template is not ready (no scene with a save manager yet).
        public static GameObject Spawn(Info t, Vector3 position, Quaternion rotation)
        {
            if (t == null || t.Prefab == null) return null;
            var staging = new GameObject(Plugin.NAME + " staging");
            staging.SetActive(false);
            try
            {
                var go = UnityEngine.Object.Instantiate(t.Prefab, position, rotation, staging.transform);
                int n = NextNameId();
                go.name = t.Id + "(Clone)" + (n < 0 ? "" : n.ToString());
                bool listed = AddToList(go, t.IsVehicle ? "ArrayList_Cars" : "ArrayList_Items");
                go.transform.SetParent(null, true);
                Plugin.Verbose($"custom prefab: spawned {go.name} at {position}, " + (listed ? "saved with the game" : "NOT saved (save list not found)"));
                return go;
            }
            finally { UnityEngine.Object.Destroy(staging); }
        }

        private static int NextNameId()
        {
            var holder = GameObject.Find("itemNameID");
            var fsm = holder != null ? holder.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "itemNameID") : null;
            var v = fsm != null ? fsm.FsmVariables.GetFsmInt("intName") : null;
            if (v == null) return -1;
            v.Value += 1;
            return v.Value;
        }

        private static bool AddToList(GameObject go, string reference)
        {
            var holder = GameObject.Find("NewGO_ArrayList");
            if (holder == null) return false;
            var list = holder.GetComponents<PlayMakerArrayListProxy>().FirstOrDefault(p => p.referenceName == reference);
            if (list == null) return false;
            list.arrayList.Add(go);
            return true;
        }

        // 64-bit FNV-1a, kept positive like ES3's own ids
        public static long StableId(string s)
        {
            ulong h = 14695981039346656037;
            foreach (char c in s) { h ^= c; h *= 1099511628211; }
            return (long)(h & 0x7FFFFFFFFFFFFFFF);
        }
    }
}
