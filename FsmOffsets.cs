using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SigmaMod
{
    // Some vanilla positions are numbers inside FSM actions, written at runtime: pedals lerp between
    // two fixed points (Pedal), wheel hinges snap to fixed stock/lifted points (Suspension), the hitch
    // and rear bumper hinge move with the suspension type (checkSuspension). Those would undo a
    // template that moves the object. When a template moves an object by d (in its parent's space),
    // every fixed local position an FSM writes to that object is moved by d as well, so the vanilla
    // behaviour (pedal travel, lift height) stays the same relative to the new place.
    //
    // Handled: SetPosition in Self space with constant x/y/z or vector, and Vector3Lerp /
    // SetVector3Value constants feeding the variable such a SetPosition uses. Fixed rotations are
    // reported, not changed. Edited states are written back with SaveActions, so copies of the
    // template carry them.
    internal static class FsmOffsets
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public;

        public static void Apply(GameObject root, Dictionary<Transform, Vector3> moved, string where)
        {
            int changed = 0;
            var rotated = new HashSet<string>();
            foreach (var fsm in root.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                FsmState[] states;
                try { states = fsm.FsmStates; }
                catch { continue; }

                // pass 1: SetPosition actions on moved objects; collect variables they read
                var varDelta = new Dictionary<string, Vector3>();
                var dirty = new HashSet<FsmState>();
                foreach (var st in states)
                {
                    FsmStateAction[] actions;
                    try { actions = st.Actions; }
                    catch { continue; }
                    foreach (var a in actions)
                    {
                        if (a == null) continue;
                        string type = a.GetType().Name;
                        if (type != "SetPosition" && type != "SetRotation") continue;
                        var target = Target(fsm, Get(a, "gameObject") as FsmOwnerDefault);
                        if (target == null) continue;
                        bool self = Get(a, "space") is Space sp && sp == Space.Self;
                        if (type == "SetRotation")
                        {
                            if (moved.ContainsKey(target.transform) && self) rotated.Add(target.name);
                            continue;
                        }
                        if (!self || !moved.TryGetValue(target.transform, out var d)) continue;
                        bool any = false;
                        any |= Shift(Get(a, "x") as FsmFloat, d.x);
                        any |= Shift(Get(a, "y") as FsmFloat, d.y);
                        any |= Shift(Get(a, "z") as FsmFloat, d.z);
                        if (Get(a, "vector") is FsmVector3 v && !v.IsNone)
                        {
                            if (v.UseVariable) { if (!string.IsNullOrEmpty(v.Name)) varDelta[v.Name] = d; }
                            else { v.Value += d; any = true; }
                        }
                        if (any) { dirty.Add(st); changed++; Plugin.Verbose($"{where}: {Path(fsm)} '{fsm.FsmName}'/{st.Name}: SetPosition of {target.name} moved by {d}"); }
                    }
                }

                // pass 2: constants that feed those variables
                if (varDelta.Count > 0)
                    foreach (var st in states)
                    {
                        foreach (var a in st.Actions)
                        {
                            if (a == null) continue;
                            string type = a.GetType().Name;
                            FsmVector3 store; FsmVector3[] sources;
                            if (type == "Vector3Lerp") { store = Get(a, "storeResult") as FsmVector3; sources = new[] { Get(a, "fromVector") as FsmVector3, Get(a, "toVector") as FsmVector3 }; }
                            else if (type == "SetVector3Value") { store = Get(a, "vector3Variable") as FsmVector3; sources = new[] { Get(a, "vector3Value") as FsmVector3 }; }
                            else continue;
                            if (store == null || string.IsNullOrEmpty(store.Name) || !varDelta.TryGetValue(store.Name, out var d)) continue;
                            bool any = false;
                            foreach (var s in sources)
                                if (s != null && !s.IsNone && !s.UseVariable) { s.Value += d; any = true; }
                            if (any) { dirty.Add(st); changed++; Plugin.Verbose($"{where}: {Path(fsm)} '{fsm.FsmName}'/{st.Name}: {type} into {store.Name} moved by {d}"); }
                        }
                    }

                foreach (var st in dirty) st.SaveActions();
            }
            if (changed > 0) Plugin.Log.LogInfo($"{where}: {changed} fixed FSM positions moved with their objects");
            if (rotated.Count > 0)
                Plugin.Log.LogInfo($"{where}: FSMs set fixed rotations on {string.Join(", ", new List<string>(rotated).ToArray())}; a rotation from the template is overridden there");
        }

        // constant (not a variable, not None) float: add d
        private static bool Shift(FsmFloat f, float d)
        {
            if (f == null || f.IsNone || f.UseVariable || d == 0f) return false;
            f.Value += d;
            return true;
        }

        private static GameObject Target(PlayMakerFSM fsm, FsmOwnerDefault o)
        {
            if (o == null) return null;
            if (o.OwnerOption == OwnerDefaultOption.UseOwner) return fsm.gameObject;
            var g = o.GameObject;
            return g != null && !g.UseVariable ? g.Value : null;
        }

        private static object Get(object a, string field) => a.GetType().GetField(field, Flags)?.GetValue(a);

        private static string Path(PlayMakerFSM fsm)
        {
            var t = fsm.transform;
            string p = t.name;
            while (t.parent != null && t.parent.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }
    }
}
