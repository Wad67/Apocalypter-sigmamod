using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace SigmaMod
{
    // Diagnostics (Debug.VerboseLog): for ten minutes after a hot reload, each frame's stages written (and flushed) to
    // BepInEx/SigmaMod-frames.txt, so a freeze leaves the stage it stopped in as the file's last line:
    // fixed< (FixedUpdate begins), fixed> (scripts done, the physics step next), update (the physics step
    // and its callbacks done), late, gui< / gui> (the IMGUI pass, first and last of the scripts drawing with OnGUI), end (rendered).
    internal static class FrameTrace
    {
        private static float _until;
        private static StreamWriter _out;
        private static GameObject _host;

        public static void Start(string why)
        {
            if (!Plugin.VerboseLogEntry.Value) return;
            _until = Time.realtimeSinceStartup + 600f;
            try
            {
                if (_out == null)
                    _out = new StreamWriter(Path.Combine(BepInEx.Paths.BepInExRootPath, Plugin.NAME + "-frames.txt"), false) { AutoFlush = true };
                Mark("trace: " + why);
            }
            catch (Exception e) { Plugin.Log.LogWarning("frame trace: " + e.Message); return; }
            if (_host == null)
            {
                _host = new GameObject(Plugin.NAME + " frame trace");
                UnityEngine.Object.DontDestroyOnLoad(_host);
                _host.AddComponent<Early>();
                _host.AddComponent<Late>();
            }
        }

        internal static bool On => _out != null && Time.realtimeSinceStartup < _until;

        internal static void Mark(string stage)
        {
            if (_out == null) return;
            try { _out.WriteLine($"{DateTime.Now:HH:mm:ss.fff} frame {Time.frameCount} {stage}"); } catch { }
        }

        [DefaultExecutionOrder(-32000)]
        private sealed class Early : MonoBehaviour
        {
            private void FixedUpdate() { if (On) Mark("fixed<"); }
            private void Update() { if (On) Mark("update"); }
            private void OnGUI() { if (On) Mark("gui<"); }
            private void Start() { StartCoroutine(End()); }
            private IEnumerator End()
            {
                var wait = new WaitForEndOfFrame();
                while (true) { yield return wait; if (On) Mark("end"); }
            }
        }

        [DefaultExecutionOrder(32000)]
        private sealed class Late : MonoBehaviour
        {
            private void FixedUpdate() { if (On) Mark("fixed>"); }
            private void LateUpdate() { if (On) Mark("late"); }
            private void OnGUI() { if (On) Mark("gui>"); }
        }
    }
}
