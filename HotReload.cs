using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ES3Internal;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SigmaMod
{
    // Tools.HotReload: once a second, templates whose .glb or .json changed (and new .glb files) are rebuilt,
    // and every spawned copy of them is swapped for a new one in the same place, moving at the same speed.
    // Parts fitted to a copy go onto the same mount of the new copy (object ids are stable across rebuilds,
    // as for save and load); their FSMs that pointed at the old copy restart so they find the new one. A copy
    // the player is inside is swapped once the player is out. A file saved half-way or a .json typo keeps the
    // previous version (logged). DLL changes still need a game restart.
    internal static class HotReload
    {
        private static float _next;
        private static readonly List<KeyValuePair<long, GameObject>> Waiting = new List<KeyValuePair<long, GameObject>>();

        private static readonly DateTime Loaded = DateTime.UtcNow;
        private static bool _dllWarned;

        public static void Tick()
        {
            if (!Plugin.HotReloadEntry.Value || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            // code can't reload: say so when the DLL on disk is newer than the one running
            if (!_dllWarned)
                try
                {
                    var dll = typeof(HotReload).Assembly.Location;
                    if (File.Exists(dll) && File.GetLastWriteTimeUtc(dll) > Loaded)
                    {
                        _dllWarned = true;
                        Plugin.Log.LogWarning($"hot reload: {Path.GetFileName(dll)} was rebuilt after the game started; restart the game, the running code is the old one");
                    }
                }
                catch { _dllWarned = true; }
            var mgr = ES3ReferenceMgrBase.Current;
            if (mgr == null || CustomPrefabs.Sources == null) return;

            var rebuilt = new List<CustomPrefabs.Info>();
            foreach (var src in CustomPrefabs.Sources)
            {
                string[] files;
                try { files = Directory.GetFiles(src.Dir, "*.glb", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var path in files)
                {
                    if (!CustomPrefabs.Changed(path)) continue;
                    if ((DateTime.UtcNow - CustomPrefabs.Stamp(path)).TotalSeconds < 1) continue; // still being written
                    var before = CustomPrefabs.TemplatesOf(path);
                    var list = CustomPrefabs.LoadFile(src, path, mgr);
                    if (list.All(before.Contains)) continue; // failed: the previous version stays
                    Plugin.Log.LogInfo($"hot reload: {src.Mod}/{Path.GetFileName(path)}: {string.Join(", ", list.Select(t => t.Id).ToArray())}");
                    rebuilt.AddRange(list);
                }
            }
            if (rebuilt.Count > 0)
            {
                CustomPrefabs.Registered();
                foreach (var t in rebuilt)
                    foreach (var es3 in UnityEngine.Object.FindObjectsOfType<ES3Prefab>())
                        if (es3.prefabId == t.PrefabId && es3.gameObject != t.Prefab) Swap(es3.gameObject, t.PrefabId);
            }
            for (int i = Waiting.Count - 1; i >= 0; i--)
            {
                var w = Waiting[i];
                if (w.Value == null || !PlayerInside(w.Value)) { Waiting.RemoveAt(i); if (w.Value != null) Swap(w.Value, w.Key); }
            }
        }

        private static GameObject Player => FsmVariables.GlobalVariables.FindFsmGameObject("Player")?.Value;

        private static bool PlayerInside(GameObject go)
        {
            var p = Player;
            return p != null && p.transform.IsChildOf(go.transform);
        }

        private static void Swap(GameObject old, long prefabId)
        {
            var t = CustomPrefabs.All.FirstOrDefault(x => x.PrefabId == prefabId);
            if (t == null || t.Prefab == null) return;
            if (old.transform.parent != null)
            {
                Plugin.Log.LogInfo($"hot reload: {old.name} is fitted to something; take it off and on again for the new version");
                return;
            }
            if (PlayerInside(old))
            {
                if (!Waiting.Any(w => w.Value == old))
                {
                    Waiting.Add(new KeyValuePair<long, GameObject>(prefabId, old));
                    Plugin.Log.LogInfo($"hot reload: {old.name} is swapped when you get out");
                }
                return;
            }
            GameObject copy = null;
            try
            {
                var es3 = old.GetComponent<ES3Prefab>();
                // fitted parts: other prefabs sitting directly on one of this copy's own objects (parts on parts go along)
                var parts = new List<(Transform part, long mount, Vector3 pos, Quaternion rot)>();
                foreach (var p in old.GetComponentsInChildren<ES3Prefab>(true))
                {
                    var parent = p.transform.parent;
                    if (p == es3 || parent == null || parent.GetComponentInParent<ES3Prefab>() != es3) continue;
                    parts.Add((p.transform, es3.localRefs.TryGetValue(parent.gameObject, out long id) ? id : -1, p.transform.localPosition, p.transform.localRotation));
                }
                var body = old.GetComponent<Rigidbody>();
                copy = CustomPrefabs.Spawn(t, old.transform.position, old.transform.rotation);
                if (copy == null) return;
                // mounts of the new copy by save id; only objects of the copy itself, never the template's
                var mounts = new Dictionary<long, GameObject>();
                foreach (var kv in copy.GetComponent<ES3Prefab>().localRefs)
                    if (kv.Key is GameObject g && g != null && g.transform.IsChildOf(copy.transform)) mounts[kv.Value] = g;
                int moved = 0, dropped = 0;
                foreach (var (part, mount, pos, rot) in parts)
                {
                    try
                    {
                        if (mount != -1 && mounts.TryGetValue(mount, out var to))
                        {
                            part.SetParent(to.transform, false);
                            part.localPosition = pos;
                            part.localRotation = rot;
                            Restart(part.gameObject, old.transform);
                            moved++;
                        }
                        else
                        {
                            part.SetParent(null, true);
                            dropped++;
                        }
                    }
                    catch (Exception e) { Plugin.Log.LogError($"hot reload: moving {part.name} to {copy.name}: {e}"); }
                }
                var nb = copy.GetComponent<Rigidbody>();
                if (body != null && nb != null && !body.isKinematic && !nb.isKinematic)
                {
                    nb.velocity = body.velocity;
                    nb.angularVelocity = body.angularVelocity;
                }
                Plugin.Log.LogInfo($"hot reload: {old.name} -> {copy.name}, {moved} fitted parts moved over" +
                                   (dropped > 0 ? $", {dropped} dropped (their mount is gone)" : ""));
            }
            catch (Exception e) { Plugin.Log.LogError($"hot reload: swapping {old.name} failed: {e}"); }
            finally
            {
                // once a new copy exists the old one goes, whatever happened in between: never two of the same car
                if (copy != null)
                {
                    CustomPrefabs.Unlist(old);
                    UnityEngine.Object.Destroy(old);
                }
            }
        }

        // FSMs of a moved part that hold the old copy (a vehicle, hinge or VehicleController they looked up when
        // fitted) start over, so they look up the new one
        private static void Restart(GameObject part, Transform oldCopy)
        {
            bool Old(UnityEngine.Object o)
            {
                if (o == null) return false; // Unity's null: destroyed objects too
                var tr = o is GameObject g ? g.transform : o is Component c ? c.transform : null;
                return tr != null && tr.IsChildOf(oldCopy);
            }
            foreach (var pm in part.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                var v = pm.FsmVariables;
                if (!v.GameObjectVariables.Any(x => Old(x.Value)) && !v.ObjectVariables.Any(x => Old(x.Value))) continue;
                if (!string.IsNullOrEmpty(pm.Fsm.StartState)) pm.SetState(pm.Fsm.StartState);
            }
        }
    }
}
