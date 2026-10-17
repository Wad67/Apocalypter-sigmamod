using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SigmaMod
{
    [BepInPlugin(GUID, NAME, VERSION)]
    // loaded after Apocaspawner when present, so its catalog can list custom prefabs
    [BepInDependency("com.denis.apocalypter.apocaspawner", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.burner.apocalypter.sigmamod";
        public const string NAME = "SigmaMod";
        public const string VERSION = "2.2.0";

        public static ManualLogSource Log;
        public static ConfigEntry<bool> VerboseLogEntry;
        public static ConfigEntry<bool> ExportAllEntry, ExportPlacesEntry, ExportMaterialsEntry;
        public static ConfigEntry<bool> HotReloadEntry;
        public static ConfigEntry<bool> LiveCodeEntry;
        public static ConfigEntry<string> StarterCarEntry;

        private static Runner _runner;

        private void Awake()
        {
            Log = Logger;

            // opt in to the Apocasetter Mods window (the entry itself is hidden there)
            Config.Bind("General", "Apocasetter", true, "Show in the Apocasetter Mods menu.");
            VerboseLogEntry = Config.Bind("Debug", "VerboseLog", false, "Extra diagnostics, an FSM dump and a vehicle dump in BepInEx.");
            StarterCarEntry = Config.Bind("Debug", "StarterCar", "", "The starting car of a new game (a template id or a game car's name); empty: random.");
            ExportAllEntry = Config.Bind("Tools", "ExportAll", false,
                "Export every game object as a .glb template to BepInEx/" + NAME + "-export. Takes several minutes; the game freezes until it finishes.");
            // a button: switching it on starts one export and switches it back off
            ExportAllEntry.Value = false;
            ExportAllEntry.SettingChanged += (s, e) =>
            {
                if (!ExportAllEntry.Value) return;
                ExportAllEntry.Value = false;
                if (_runner != null && !ModelExport.Busy) _runner.StartCoroutine(ModelExport.ExportAll());
            };
            ExportPlacesEntry = Config.Bind("Tools", "ExportPlaces", false,
                "Export every place the terrain generator puts in the world (camps, wrecks, caves...) as a .glb to BepInEx/" + NAME + "-export-places. In a loaded game.");
            ExportPlacesEntry.Value = false;
            ExportPlacesEntry.SettingChanged += (s, e) =>
            {
                if (!ExportPlacesEntry.Value) return;
                ExportPlacesEntry.Value = false;
                if (_runner != null && !ModelExport.Busy) _runner.StartCoroutine(ModelExport.ExportPlaces());
            };
            ExportMaterialsEntry = Config.Bind("Tools", "ExportMaterials", false,
                "Export every game material and terrain layer as swatches in one .glb to BepInEx/" + NAME + "-export-materials. In a loaded game.");
            ExportMaterialsEntry.Value = false;
            ExportMaterialsEntry.SettingChanged += (s, e) =>
            {
                if (!ExportMaterialsEntry.Value) return;
                ExportMaterialsEntry.Value = false;
                if (_runner != null && !ModelExport.Busy) _runner.StartCoroutine(ModelExport.ExportMaterials());
            };
            HotReloadEntry = Config.Bind("Tools", "HotReload", false,
                "Rebuild templates when their .glb or .json changes and swap spawned copies. For modding.");
            LiveCodeEntry = Config.Bind("Dev", "LiveCode", false,
                "Load DLLs from BepInEx/live and reload them when rebuilt. For modding.");
            DropOldSettings();

            var harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(Plugin).Assembly);
            ApocaspawnerBridge.Init(harmony);
            FsmDump.Init();
            CustomPrefabs.Init();

            var go = new GameObject(NAME + " Runner");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            _runner = go.AddComponent<Runner>();

            Log.LogInfo(NAME + " " + VERSION + " loaded");
        }

        // settings from earlier versions stay in the .cfg as orphans unless removed
        private void DropOldSettings()
        {
            var orphans = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Config) as Dictionary<ConfigDefinition, string>;
            if (orphans == null || orphans.Count == 0) return;
            orphans.Clear();
            Config.Save();
        }

        public static void Verbose(string msg)
        {
            if (VerboseLogEntry.Value) Log.LogInfo(msg);
        }
    }

    // main-thread tick and coroutine host, independent of the plugin object's lifetime
    internal class Runner : MonoBehaviour
    {
        private void Update()
        {
            FsmDump.Tick();
            HotReload.Tick();
            LiveCode.Tick();
            Poi.Tick();
            FallWatch.Tick();
        }
    }
}
