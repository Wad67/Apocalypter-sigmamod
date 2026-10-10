using System;
using System.IO;
using System.Linq;
using System.Text;
using ES3Internal;
using NWH.VehiclePhysics2;
using NWH.WheelController3D;
using UnityEngine;

namespace SigmaMod
{
    // Debug: writes the full hierarchy of every vehicle prefab (and live vehicle) with transforms,
    // meshes and their bounds, colliders, rigidbody and wheel data, plus ES3 prefab ids, to
    // BepInEx/<NAME>-vehicle-dump.txt. Runs alongside the FSM dump.
    internal static class PrefabDump
    {
        public static void Write()
        {
            var sb = new StringBuilder();
            var mgr = ES3ReferenceMgrBase.Current;
            sb.Append("ES3ReferenceMgr: ").Append(mgr != null ? mgr.name + " prefabs=" + mgr.prefabs.Count : "none").Append('\n');

            foreach (var vc in Resources.FindObjectsOfTypeAll<VehicleController>()
                         .OrderBy(v => v.gameObject.scene.IsValid()).ThenBy(v => v.name))
            {
                var go = vc.gameObject;
                var pf = go.GetComponent<ES3Prefab>();
                sb.Append("\n==== ").Append(go.name).Append(go.scene.IsValid() ? " (live)" : " (prefab)")
                  .Append(" es3PrefabId=").Append(pf != null ? pf.prefabId.ToString() : "none")
                  .Append(" localRefs=").Append(pf != null ? pf.localRefs.Count : 0).Append('\n');
                var rb = go.GetComponent<Rigidbody>();
                if (rb != null)
                    sb.Append("  rigidbody mass=").Append(rb.mass).Append(" com=").Append(V(rb.centerOfMass))
                      .Append(" inertia=").Append(V(rb.inertiaTensor)).Append('\n');
                var b = Bounds(go);
                sb.Append("  renderer bounds (local) center=").Append(V(b.center)).Append(" size=").Append(V(b.size)).Append('\n');
                foreach (var w in vc.powertrain.wheels)
                {
                    var wc = w.wheelUAPI as WheelController;
                    if (wc == null) continue;
                    sb.Append("  wheel ").Append(w.name).Append(" at ").Append(V(go.transform.InverseTransformPoint(wc.transform.position)))
                      .Append(" radius=").Append(wc.wheel.radius).Append(" width=").Append(wc.wheel.width)
                      .Append(" spring len=").Append(wc.spring.maxLength).Append(" force=").Append(wc.spring.maxForce).Append('\n');
                }
                Tree(sb, go.transform, 1);
            }

            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, Plugin.NAME + "-vehicle-dump.txt");
            File.WriteAllText(path, sb.ToString());
            Plugin.Log.LogInfo("vehicle dump written to " + path);
        }

        private static void Tree(StringBuilder sb, Transform t, int depth)
        {
            var ind = new string(' ', depth * 2);
            sb.Append(ind).Append(t.name).Append(t.gameObject.activeSelf ? "" : " [inactive]")
              .Append(" pos=").Append(V(t.localPosition)).Append(" rot=").Append(V(t.localEulerAngles));
            if (t.localScale != Vector3.one) sb.Append(" scale=").Append(V(t.localScale));
            sb.Append(" layer=").Append(t.gameObject.layer).Append(" tag=").Append(t.tag).Append('\n');
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                sb.Append(ind).Append("  : ").Append(c.GetType().Name);
                switch (c)
                {
                    case MeshFilter mf when mf.sharedMesh != null:
                        sb.Append(" mesh=").Append(mf.sharedMesh.name).Append(" verts=").Append(mf.sharedMesh.vertexCount)
                          .Append(" bounds=").Append(V(mf.sharedMesh.bounds.center)).Append("/").Append(V(mf.sharedMesh.bounds.size)); break;
                    case Renderer r:
                        sb.Append(" enabled=").Append(r.enabled).Append(" mats=[")
                          .Append(string.Join(",", r.sharedMaterials.Select(m => m == null ? "null" : m.name + "(" + (m.shader != null ? m.shader.name : "?") + ")").ToArray())).Append("]"); break;
                    case BoxCollider bc: sb.Append(" center=").Append(V(bc.center)).Append(" size=").Append(V(bc.size)).Append(Trig(bc)); break;
                    case MeshCollider mc: sb.Append(" mesh=").Append(mc.sharedMesh != null ? mc.sharedMesh.name : "null").Append(" convex=").Append(mc.convex).Append(Trig(mc)); break;
                    case SphereCollider sc: sb.Append(" center=").Append(V(sc.center)).Append(" r=").Append(sc.radius).Append(Trig(sc)); break;
                    case CapsuleCollider cc: sb.Append(" center=").Append(V(cc.center)).Append(" r=").Append(cc.radius).Append(" h=").Append(cc.height).Append(Trig(cc)); break;
                    case Joint j: sb.Append(" connected=").Append(j.connectedBody != null ? j.connectedBody.name : "null").Append(" breakForce=").Append(j.breakForce); break;
                    case PlayMakerFSM fsm: sb.Append(" '").Append(fsm.FsmName).Append("'"); break;
                    case ES3Prefab p: sb.Append(" id=").Append(p.prefabId); break;
                }
                sb.Append('\n');
            }
            for (int i = 0; i < t.childCount; i++) Tree(sb, t.GetChild(i), depth + 1);
        }

        private static string Trig(Collider c) => c.isTrigger ? " trigger" : "";

        private static Bounds Bounds(GameObject go)
        {
            var inv = go.transform.worldToLocalMatrix;
            bool any = false;
            var b = new Bounds();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var m = inv * mf.transform.localToWorldMatrix;
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1)));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            return b;
        }

        private static string V(Vector3 v) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.###},{1:0.###},{2:0.###})", v.x, v.y, v.z);
    }
}
