using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SigmaMod
{
    // Where templates are loaded from: every mod folder in BepInEx/plugins (this one included).
    //   - <mod>/sigma.json, if present, names the folders:   { "name": "My Mod", "assets": ["models", "parts"] }
    //     ("assets" is a path or a list of paths, relative to the mod folder; "name" defaults to the folder name)
    //   - otherwise <mod>/sigma_assets/ is loaded if it exists.
    // Every .glb under those folders (subfolders included) is loaded.
    internal static class AssetSources
    {
        public const string DirName = "sigma_assets", Manifest = "sigma.json";

        public sealed class Source
        {
            public string Mod;   // display name of the mod that ships it
            public string Dir;   // full path
        }

        public static List<Source> Find()
        {
            var list = new List<Source>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(BepInEx.Paths.PluginPath)) return list;
            foreach (var modDir in Directory.GetDirectories(BepInEx.Paths.PluginPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string mod = Path.GetFileName(modDir);
                string manifest = Path.Combine(modDir, Manifest);
                var dirs = new List<string>();
                if (File.Exists(manifest))
                {
                    try
                    {
                        if (!(Json.Parse(File.ReadAllText(manifest)) is Dictionary<string, object> m)) throw new FormatException("must be an object");
                        if (m.TryGetValue("name", out var nv) && nv is string ns && ns.Trim().Length > 0) mod = ns.Trim();
                        if (m.TryGetValue("assets", out var av))
                        {
                            if (av is string one) dirs.Add(one);
                            else if (av is List<object> many) dirs.AddRange(many.OfType<string>());
                        }
                        if (dirs.Count == 0) Plugin.Log.LogWarning($"{manifest}: no \"assets\" folders listed");
                    }
                    catch (Exception e) { Plugin.Log.LogError($"{manifest}: {e.Message}"); continue; }
                }
                else if (Directory.Exists(Path.Combine(modDir, DirName))) dirs.Add(DirName);

                foreach (var d in dirs)
                {
                    string full = Path.GetFullPath(Path.Combine(modDir, d));
                    if (!Directory.Exists(full)) { Plugin.Log.LogWarning($"{manifest}: asset folder '{d}' does not exist"); continue; }
                    if (seen.Add(full)) list.Add(new Source { Mod = mod, Dir = full });
                }
            }
            return list;
        }
    }
}
