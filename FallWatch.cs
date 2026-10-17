using System.Collections.Generic;
using System.Linq;
using ES3Internal;
using UnityEngine;

namespace SigmaMod
{
    // Bodies that have fallen through the ground. A body falling for long enough is far enough down for the game to
    // lock up, so every two seconds each Rigidbody is checked against the terrain under it:
    //   - one more than Deep metres below the ground and still falling (faster than Falling m/s) has fallen through:
    //     it is put back on the ground right above, its lowest point half a metre over it, at rest. Something resting
    //     underground (in a cave) isn't falling, and stays;
    //   - with Debug.VerboseLog, one more than 50 m below is reported once (what it is, where it came from), and again
    //     each further kilometre.
    internal static class FallWatch
    {
        private const float Deep = 100f, Falling = 10f, Report = 50f, Clearance = 0.5f;
        private static float _next;
        private static readonly Dictionary<int, float> Reported = new Dictionary<int, float>();   // body -> depth last reported

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 2f;
            foreach (var rb in Object.FindObjectsOfType<Rigidbody>())
            {
                if (rb == null || rb.isKinematic) continue;
                var p = rb.position;
                if (!Ground(p, out float ground)) continue;
                float below = ground - p.y;
                if (below < Report) continue;
                if (below > Deep && rb.velocity.y < -Falling) { Rescue(rb, ground, below); continue; }
                if (!Plugin.VerboseLogEntry.Value) continue;
                int id = rb.GetInstanceID();
                if (Reported.TryGetValue(id, out float last) && below < last + 1000f) continue;
                Reported[id] = below;
                Plugin.Log.LogWarning($"fall watch: {Describe(rb)} is {below:0} m below the ground, falling at {-rb.velocity.y:0} m/s");
            }
        }

        // on the ground right above, at rest
        private static void Rescue(Rigidbody rb, float ground, float below)
        {
            var go = rb.gameObject;
            float lift = Clearance;
            var cs = go.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger && c.attachedRigidbody == rb).ToArray();
            if (cs.Length > 0) lift += rb.position.y - cs.Min(c => c.bounds.min.y);
            var to = new Vector3(rb.position.x, ground + lift, rb.position.z);
            Plugin.Log.LogWarning($"fall watch: {Describe(rb)} fell through the ground ({below:0} m below, {-rb.velocity.y:0} m/s); put back on it");
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            go.transform.position = to;
            rb.position = to;
            Reported.Remove(rb.GetInstanceID());
        }

        private static string Describe(Rigidbody rb)
        {
            var es3 = rb.GetComponentInParent<ES3Prefab>();
            var t = es3 != null ? CustomPrefabs.All.FirstOrDefault(x => x.PrefabId == es3.prefabId) : null;
            return $"'{Path(rb.transform)}' (mass {rb.mass:0.#}, layer {rb.gameObject.layer}, tag {rb.tag}"
                + (es3 != null ? $", prefab {es3.prefabId}" : "") + (t != null ? $", template '{t.Id}'" : "") + ")";
        }

        private static bool Ground(Vector3 at, out float y)
        {
            foreach (var t in Terrain.activeTerrains)
            {
                var o = t.GetPosition(); var s = t.terrainData.size;
                if (at.x >= o.x && at.x <= o.x + s.x && at.z >= o.z && at.z <= o.z + s.z) { y = t.SampleHeight(at) + o.y; return true; }
            }
            y = 0f;
            return false;
        }

        private static string Path(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }
    }
}
