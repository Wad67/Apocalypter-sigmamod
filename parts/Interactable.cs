using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.UI;

namespace SigmaMod.Parts
{
    // What every part the player handles shares: being looked at (the camera's ray through the middle of the screen, as
    // the game picks items; trigger volumes and vehicles' and items' bodies that aren't parts are seen through, anything
    // else solid stops it), the game's
    // four HUD lines while it is (written every frame, cleared after), and the mouse buttons on it while the cursor is the
    // game's. Clicks on it are its own: other mods' hands see OnMouseDown and leave them alone.
    public abstract class Interactable : MonoBehaviour
    {
        public float reach = 2f;            // m from the camera
        public string label = "";           // the HUD's first line

        // the HUD's other lines (null: empty)
        protected virtual string Line2 => null;
        protected virtual string Line3 => null;
        protected virtual string Use => null;
        protected virtual string Title => label;
        protected virtual void Click(int button) { }

        public bool Pointed => _pointed == this;

        private static Interactable _pointed, _shown;
        private static int _frame = -1;
        private static readonly RaycastHit[] Hits = new RaycastHit[32];
        private const int VehicleLayer = 8, ItemLayer = 9;

        protected virtual void Update()
        {
            Look();
            if (_pointed != this) return;
            Hud(Title, Line2, Line3, Use);
            if (Cursor.lockState != CursorLockMode.Locked) return;
            if (Input.GetMouseButtonDown(0)) Click(0);
            if (Input.GetMouseButtonDown(1)) Click(1);
        }

        protected virtual void OnDisable()
        {
            if (_pointed == this) _pointed = null;
            if (_shown == this) { Hud("", null, null, null); _shown = null; }
        }

        // Unity's sign that clicking this does something
        private void OnMouseDown() { }

        // once a frame, whichever part asks first: the part looked at, if any
        private static void Look()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            Interactable found = null;
            var cam = Camera.main;
            if (cam != null)
            {
                var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                int n = Physics.RaycastNonAlloc(ray, Hits, 4f, ~0, QueryTriggerInteraction.Collide);
                Array.Sort(Hits, 0, n, HitDistance.Instance);
                for (int i = 0; i < n; i++)
                {
                    var c = Hits[i].collider;
                    if (c == null) continue;
                    var part = c.GetComponent<Interactable>();
                    if (part != null && part.isActiveAndEnabled) { if (Hits[i].distance <= part.reach) found = part; break; }
                    // something solid in front stops it; a vehicle's or an item's own body doesn't (the game's pick only
                    // sees layer 0: a vehicle's levers are inside its body)
                    if (!c.isTrigger && !IsPlayer(c) && c.gameObject.layer != VehicleLayer && c.gameObject.layer != ItemLayer) break;
                }
            }
            if (found != _shown && _shown != null) Hud("", null, null, null);
            _pointed = _shown = found;
        }

        private static GameObject _player;
        private static bool IsPlayer(Collider c)
        {
            if (_player == null) _player = FsmVariables.GlobalVariables.FindFsmGameObject("Player")?.Value;
            return _player != null && c.transform.IsChildOf(_player.transform);
        }

        private sealed class HitDistance : IComparer<RaycastHit>
        {
            public static readonly HitDistance Instance = new HitDistance();
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }

        // ---------------------------------------------------------------- the game's HUD lines
        private static readonly string[] Names = { "UI_ItemName", "UI_ItemName_2", "UI_ItemName_3", "UI_ItemUse" };
        private static readonly Text[] Texts = new Text[4];

        protected static void Hud(string name, string line2, string line3, string use)
        {
            Set(0, name); Set(1, line2); Set(2, line3); Set(3, use);
        }

        private static void Set(int i, string s)
        {
            if (Texts[i] == null)
            {
                var go = FsmVariables.GlobalVariables.FindFsmGameObject(Names[i])?.Value;
                Texts[i] = go != null ? go.GetComponent<Text>() : null;
                if (Texts[i] == null) return;
            }
            s = s ?? "";
            if (Texts[i].text != s) Texts[i].text = s;
        }

        // ---------------------------------------------------------------- helpers for parts
        // the vehicle (or whatever) the part belongs to: the root its FSMs and events are about
        protected GameObject Root => (GetComponentInParent<NWH.VehiclePhysics2.VehicleController>()?.gameObject) ?? transform.root.gameObject;

        // the game's events to every FSM on the root and below (PlayMaker's "send to children")
        protected void Broadcast(string ev)
        {
            if (string.IsNullOrEmpty(ev)) return;
            foreach (var fsm in Root.GetComponentsInChildren<PlayMakerFSM>()) fsm.SendEvent(ev);
        }

        protected void Play(AudioClip clip)
        {
            if (clip != null) AudioSource.PlayClipAtPoint(clip, transform.position, 1f);
        }
    }

    // A member reached by a dotted path from an object ("input.Throttle" on a VehicleController), set and read by reflection
    public sealed class MemberPath
    {
        private readonly string _path;
        private object _owner;
        private object _at;
        private MemberInfo _last;

        public MemberPath(string path) { _path = path; }

        public bool Set(object owner, object value)
        {
            if (!Resolve(owner)) return false;
            try
            {
                if (_last is FieldInfo f) f.SetValue(_at, Convert(value, f.FieldType));
                else if (_last is PropertyInfo p) p.SetValue(_at, Convert(value, p.PropertyType), null);
                return true;
            }
            catch { return false; }
        }

        public object Get(object owner)
        {
            if (!Resolve(owner)) return null;
            try { return _last is FieldInfo f ? f.GetValue(_at) : ((PropertyInfo)_last).GetValue(_at, null); }
            catch { return null; }
        }

        private bool Resolve(object owner)
        {
            if (owner == null || string.IsNullOrEmpty(_path)) return false;
            if (ReferenceEquals(owner, _owner) && _last != null) return true;
            _owner = owner; _last = null;
            object at = owner;
            var parts = _path.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                var t = at.GetType();
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                MemberInfo m = (MemberInfo)t.GetProperty(parts[i], F) ?? t.GetField(parts[i], F);
                if (m == null) return false;
                if (i == parts.Length - 1) { _at = at; _last = m; return true; }
                at = m is FieldInfo f ? f.GetValue(at) : ((PropertyInfo)m).GetValue(at, null);
                if (at == null) return false;
            }
            return false;
        }

        private static object Convert(object v, Type t)
        {
            if (v == null || t.IsInstanceOfType(v)) return v;
            if (t == typeof(bool)) return System.Convert.ToSingle(v) > 0.5f;
            return System.Convert.ChangeType(v, t);
        }
    }
}
