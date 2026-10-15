using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ES3Internal;
using MapMagic.Core;
using MapMagic.Nodes;
using UnityEngine;

namespace SigmaMod
{
    // VerboseLog only, with the FSM and vehicle dumps: the terrain generator's graph, to see how the world places
    // things (camps, wrecks, caves, buildings: MapMagic's objects outputs, what feeds them and with what settings).
    // Every MapMagic object: its tile size and globals, then its graph and every graph inside it (biomes,
    // functions): each node with its type, id, whether it is on, and its public settings (prefabs by name and Easy
    // Save id), then the links (each node input and the node feeding it). Written to BepInEx/SigmaMod-mapmagic-graph.txt.
    internal static class GraphDump
    {
        private const int MaxDepth = 3, MaxItems = 40;

        public static void Write()
        {
            var sb = new StringBuilder();
            sb.Append(Plugin.NAME).Append(" MapMagic graph dump, ").Append(DateTime.Now).Append('\n');
            var seen = new HashSet<Graph>();
            foreach (var mm in Resources.FindObjectsOfTypeAll<MapMagicObject>().Where(m => m.gameObject.scene.IsValid()))
            {
                sb.Append("\n######## MapMagic object '").Append(mm.name).Append("' tile size ").Append(mm.tileSize.x).Append(" x ").Append(mm.tileSize.z).Append('\n');
                Fields(sb, mm.globals, "  globals.", 1, null);
                if (mm.graph != null) GraphOut(sb, mm.graph, "main", seen);
            }
            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, Plugin.NAME + "-mapmagic-graph.txt");
            File.WriteAllText(path, sb.ToString());
            Plugin.Log.LogInfo("MapMagic graph dump written: " + path);
        }

        private static void GraphOut(StringBuilder sb, Graph g, string where, HashSet<Graph> seen)
        {
            if (g == null || !seen.Add(g)) return;
            sb.Append("\n==== graph '").Append(g.name).Append("' (").Append(where).Append("), ").Append(g.generators.Length).Append(" nodes\n");
            var subs = new List<KeyValuePair<Graph, string>>();
            foreach (var gen in g.generators)
            {
                if (gen == null) continue;
                sb.Append("\n-- [").Append(gen.id).Append("] ").Append(gen.GetType().FullName).Append(gen.enabled ? "" : " (off)").Append('\n');
                Fields(sb, gen, "     ", 1, subs);
            }
            sb.Append("\n  links (input <- the node feeding it):\n");
            foreach (var kv in g.links)
            {
                var inlet = kv.Key;
                var outlet = kv.Value;
                sb.Append("    [").Append(inlet.Gen != null ? inlet.Gen.id : 0).Append("] ").Append(Short(inlet.Gen)).Append(" ").Append(Short(inlet))
                  .Append("  <-  [").Append(outlet.Gen != null ? outlet.Gen.id : 0).Append("] ").Append(Short(outlet.Gen)).Append(" ").Append(Short(outlet)).Append('\n');
            }
            foreach (var s in subs) GraphOut(sb, s.Key, s.Value, seen);
        }

        private static string Short(object o) => o == null ? "?" : o.GetType().Name;

        // an object's public fields, simple values inline, objects and lists below them
        private static void Fields(StringBuilder sb, object o, string indent, int depth, List<KeyValuePair<Graph, string>> subs)
        {
            if (o == null) return;
            foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                if (f.Name == "id" || f.Name == "enabled") continue;
                if (v is Graph sub)
                {
                    sb.Append(indent).Append(f.Name).Append(" = graph '").Append(sub.name).Append("' (below)\n");
                    subs?.Add(new KeyValuePair<Graph, string>(sub, Short(o) + "." + f.Name));
                    continue;
                }
                Value(sb, f.Name, v, indent, depth, subs);
            }
        }

        private static void Value(StringBuilder sb, string name, object v, string indent, int depth, List<KeyValuePair<Graph, string>> subs)
        {
            if (v == null) { sb.Append(indent).Append(name).Append(" = null\n"); return; }
            var t = v.GetType();
            if (t.IsPrimitive || t.IsEnum || v is string || v is decimal || t.Namespace == "UnityEngine" && t.IsValueType)
            {
                sb.Append(indent).Append(name).Append(" = ").Append(v).Append('\n');
                return;
            }
            if (v is UnityEngine.Object uo)
            {
                sb.Append(indent).Append(name).Append(" = ").Append(Describe(uo)).Append('\n');
                return;
            }
            if (v is IEnumerable list && !(v is string))
            {
                var items = new List<object>();
                foreach (var x in list) { items.Add(x); if (items.Count > MaxItems) break; }
                if (items.Count == 0) { sb.Append(indent).Append(name).Append(" = []\n"); return; }
                bool simple = items.All(x => x == null || x is UnityEngine.Object || x.GetType().IsPrimitive || x.GetType().IsEnum || x is string || x.GetType().IsValueType);
                if (simple)
                {
                    sb.Append(indent).Append(name).Append(" = [")
                      .Append(string.Join(", ", items.Take(MaxItems).Select(x => x == null ? "null" : x is UnityEngine.Object u ? Describe(u) : x.ToString()).ToArray()))
                      .Append(items.Count > MaxItems ? ", ..." : "").Append("]\n");
                    return;
                }
                if (depth >= MaxDepth) { sb.Append(indent).Append(name).Append(" = [").Append(items.Count).Append(" items]\n"); return; }
                for (int i = 0; i < Math.Min(items.Count, MaxItems); i++)
                {
                    sb.Append(indent).Append(name).Append('[').Append(i).Append("] ").Append(Short(items[i])).Append('\n');
                    Fields(sb, items[i], indent + "    ", depth + 1, subs);
                }
                return;
            }
            if (depth >= MaxDepth) { sb.Append(indent).Append(name).Append(" = ").Append(Short(v)).Append('\n'); return; }
            sb.Append(indent).Append(name).Append(": ").Append(Short(v)).Append('\n');
            Fields(sb, v, indent + "    ", depth + 1, subs);
        }

        // a Unity object: its name, and for a game object its Easy Save prefab id
        private static string Describe(UnityEngine.Object o)
        {
            if (o == null) return "null";
            if (o is GameObject go)
            {
                var pf = go.GetComponent<ES3Prefab>();
                return "'" + go.name + "'" + (pf != null ? " es3prefab " + pf.prefabId : "");
            }
            return "'" + o.name + "' (" + o.GetType().Name + ")";
        }
    }
}
