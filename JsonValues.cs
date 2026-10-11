using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SigmaMod
{
    // JSON values -> typed values, for component fields and FSM action parameters.
    //
    // Object references (any UnityEngine.Object field, FsmGameObject, FsmObject, FsmOwnerDefault):
    //   "root"                       the template root
    //   "<sigma_id>"                 a template object by its sigma_id
    //   "/<path>"                    a template object by its path below the root (Blender names)
    //   "<object>:<Component>"       a component on that object, e.g. "root:VehicleController"
    //   "asset:<Type>/<name>"        a loaded game asset by type and name, e.g. "asset:AudioClip/engine_idle"
    //   "asset:<id>"                 a game asset by its es3global id (the export writes those)
    //   "file:<path>"                a file next to the template: .wav (PCM 16/24/32-bit or float) -> AudioClip,
    //                                .png/.jpg -> Texture2D
    //   null                         nothing
    // A GameObject field given a component gets its object; a component field given an object gets the
    // first component of the field's type on it.
    //
    // FSM values (FsmFloat, FsmInt, FsmBool, FsmString, FsmVector3, FsmGameObject, ... ):
    //   "{name}"   use the FSM variable (local, else global) of that name
    //   "{}"       none (an optional parameter left empty)
    // LayerMask fields take a list of layers ([0, 8] or ["Default"]; [] is nothing) or a raw bit mask.
    //   anything else is the constant value.
    // Other [Serializable] classes take a JSON object of their fields.
    internal sealed class JsonValues
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public GameObject Root;
        public Func<string, GameObject> Node;
        public Action<string, GameObject> AddNode;   // an object the .json made, by name
        public string Dir;
        public ES3Internal.ES3ReferenceMgrBase Mgr;
        // events referenced while converting FSM values (collected into the FSM's event list)
        public List<FsmEvent> Events;
        // the file's top-level "fsms": definitions a "playmaker" entry can "use"
        public Dictionary<string, object> Library;
        // objects made while building (collider meshes, loaded files), by a stable key, for save ids
        public List<KeyValuePair<string, UnityEngine.Object>> Created;

        private static readonly Dictionary<string, UnityEngine.Object> Files = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);

        public object Convert(object v, Type t, object current = null)
        {
            if (t == typeof(object)) return v;
            if (typeof(NamedVariable).IsAssignableFrom(t)) return Variable(v, t, current as NamedVariable);
            if (t == typeof(FsmOwnerDefault)) return Owner(v);
            if (t == typeof(FsmEvent)) return Event(v as string);
            if (t == typeof(FsmEventTarget) && v is string ts) return Target(ts);
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return Ref(v as string, t);
            if (v == null) return t.IsValueType ? Activator.CreateInstance(t) : null;
            if (t.IsEnum) return v is double d ? Enum.ToObject(t, (int)d) : Enum.Parse(t, v.ToString(), true);
            if (t == typeof(string)) return v.ToString();
            if (t == typeof(bool)) return v is bool b ? b : bool.Parse(v.ToString());
            if (t == typeof(float)) return System.Convert.ToSingle(v, CultureInfo.InvariantCulture);
            if (t == typeof(double)) return System.Convert.ToDouble(v, CultureInfo.InvariantCulture);
            if (t == typeof(int)) return System.Convert.ToInt32(v, CultureInfo.InvariantCulture);
            if (t == typeof(long)) return System.Convert.ToInt64(v, CultureInfo.InvariantCulture);
            if (t == typeof(byte)) return System.Convert.ToByte(v, CultureInfo.InvariantCulture);
            var arr = v as List<object>;
            float F(int k) => System.Convert.ToSingle(arr[k], CultureInfo.InvariantCulture);
            if (t == typeof(Vector2)) return new Vector2(F(0), F(1));
            if (t == typeof(Vector3)) return new Vector3(F(0), F(1), F(2));
            if (t == typeof(Vector4)) return new Vector4(F(0), F(1), F(2), F(3));
            if (t == typeof(Quaternion)) return arr.Count == 3 ? Quaternion.Euler(F(0), F(1), F(2)) : new Quaternion(F(0), F(1), F(2), F(3));
            if (t == typeof(Color)) return new Color(F(0), F(1), F(2), arr.Count > 3 ? F(3) : 1f);
            if (t == typeof(Rect)) return new Rect(F(0), F(1), F(2), F(3));
            // LayerMask: a bit mask number, or a list of layer numbers / names ([] is nothing)
            if (t == typeof(LayerMask))
            {
                if (arr == null) return (LayerMask)System.Convert.ToInt32(v, CultureInfo.InvariantCulture);
                int mask = 0;
                foreach (var e in arr)
                {
                    int layer = e is string ln ? LayerMask.NameToLayer(ln) : System.Convert.ToInt32(e, CultureInfo.InvariantCulture);
                    if (layer < 0 || layer > 31) throw new Exception($"unknown layer {Json.Write(e)}");
                    mask |= 1 << layer;
                }
                return (LayerMask)mask;
            }
            if (t == typeof(AnimationCurve))
                return new AnimationCurve(arr.Cast<List<object>>().Select(k => new Keyframe(System.Convert.ToSingle(k[0], CultureInfo.InvariantCulture),
                                                                                             System.Convert.ToSingle(k[1], CultureInfo.InvariantCulture))).ToArray());
            if (t.IsArray && arr != null)
            {
                var et = t.GetElementType();
                var a = Array.CreateInstance(et, arr.Count);
                var old = current as Array;
                for (int k = 0; k < arr.Count; k++) a.SetValue(Convert(arr[k], et, old != null && k < old.Length ? old.GetValue(k) : null), k);
                return a;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) && arr != null)
            {
                var l = (IList)Activator.CreateInstance(t);
                foreach (var e in arr) l.Add(Convert(e, t.GetGenericArguments()[0]));
                return l;
            }
            if (v is Dictionary<string, object> obj && (t.IsClass || t.IsValueType))
            {
                var target = current ?? Activator.CreateInstance(t, true);
                foreach (var kv in obj) SetMember(target, kv.Key, kv.Value);
                return target;
            }
            throw new Exception($"cannot convert {Json.Write(v)} to {t.Name}");
        }

        // a field or settable property of obj, by name
        public void SetMember(object obj, string name, object v)
        {
            var t = obj.GetType();
            for (var x = t; x != null; x = x.BaseType)
            {
                var f = x.GetField(name, Flags | BindingFlags.DeclaredOnly);
                if (f != null) { f.SetValue(obj, Convert(v, f.FieldType, f.GetValue(obj))); return; }
            }
            var p = t.GetProperty(name, Flags);
            if (p != null && p.CanWrite) { p.SetValue(obj, Convert(v, p.PropertyType, p.CanRead ? p.GetValue(obj, null) : null), null); return; }
            throw new Exception($"{t.Name} has no '{name}'");
        }

        // ------------------------------------------------ PlayMaker values

        private static bool IsBinding(object v, out string name)
        {
            name = null;
            if (!(v is string s) || s.Length < 2 || s[0] != '{' || s[s.Length - 1] != '}') return false;
            name = s.Substring(1, s.Length - 2).Trim();
            return true;
        }

        private NamedVariable Variable(object v, Type t, NamedVariable current)
        {
            var nv = current ?? (NamedVariable)Activator.CreateInstance(t);
            if (IsBinding(v, out string name))
            {
                nv.UseVariable = true;
                nv.SetName(name); // "" is None (Name has no working setter)
                return nv;
            }
            nv.UseVariable = false;
            nv.SetName("");
            switch (nv)
            {
                case FsmGameObject g: g.Value = Ref(v as string, typeof(GameObject)) as GameObject; break;
                case FsmObject o: o.Value = Ref(v as string, o.ObjectType ?? typeof(UnityEngine.Object)); break;
                case FsmEnum e:
                    if (e.EnumType == null || !e.EnumType.IsEnum) throw new Exception("enum parameter with no enum type");
                    e.Value = (Enum)Convert(v, e.EnumType);
                    break;
                default:
                    var vp = nv.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                             ?? nv.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                    if (vp == null || !vp.CanWrite) throw new Exception($"{t.Name} constants are not supported");
                    vp.SetValue(nv, Convert(v, vp.PropertyType), null);
                    break;
            }
            return nv;
        }

        private FsmOwnerDefault Owner(object v)
        {
            var o = new FsmOwnerDefault();
            if (v == null || v is string s0 && s0.Equals("owner", StringComparison.OrdinalIgnoreCase))
            {
                o.OwnerOption = OwnerDefaultOption.UseOwner;
                return o;
            }
            o.OwnerOption = OwnerDefaultOption.SpecifyGameObject;
            o.GameObject = (FsmGameObject)Variable(v, typeof(FsmGameObject), null);
            return o;
        }

        public FsmEvent Event(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var e = FsmEvent.GetFsmEvent(name);
            if (Events != null && !Events.Any(x => x.Name == e.Name)) Events.Add(e);
            return e;
        }

        // "self", "broadcast", "<object>" (its FSMs), "<object>/<FsmName>"
        private FsmEventTarget Target(string s)
        {
            var t = new FsmEventTarget();
            if (s.Equals("self", StringComparison.OrdinalIgnoreCase)) { t.target = FsmEventTarget.EventTarget.Self; return t; }
            if (s.Equals("broadcast", StringComparison.OrdinalIgnoreCase)) { t.target = FsmEventTarget.EventTarget.BroadcastAll; return t; }
            int slash = s.LastIndexOf('/');
            if (slash > 0)
            {
                t.target = FsmEventTarget.EventTarget.GameObjectFSM;
                t.gameObject = Owner(s.Substring(0, slash));
                t.fsmName = new FsmString { Value = s.Substring(slash + 1) };
                return t;
            }
            t.target = FsmEventTarget.EventTarget.GameObject;
            t.gameObject = Owner(s);
            return t;
        }

        // ------------------------------------------------ references

        public UnityEngine.Object Ref(string s, Type want)
        {
            if (s == null) return null;
            s = s.Trim();
            UnityEngine.Object o;
            if (s.StartsWith("asset:", StringComparison.OrdinalIgnoreCase)) o = Asset(s.Substring(6), want);
            else if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) o = File(s.Substring(5));
            else
            {
                string comp = null;
                int colon = s.LastIndexOf(':');
                if (colon > 0) { comp = s.Substring(colon + 1); s = s.Substring(0, colon); }
                GameObject go;
                if (s.Equals("root", StringComparison.OrdinalIgnoreCase)) go = Root;
                else if (s.StartsWith("/")) go = s.Length == 1 ? Root : Root.transform.Find(s.Substring(1))?.gameObject;
                else go = Node?.Invoke(s);
                if (go == null) throw new Exception($"no object '{s}' in the template (sigma_id, /path or root)");
                if (comp != null)
                {
                    var ct = Types.Component(comp);
                    o = go.GetComponent(ct);
                    if (o == null) throw new Exception($"no {comp} on '{s}'");
                }
                else o = go;
            }
            return Fit(o, want, s);
        }

        private static UnityEngine.Object Fit(UnityEngine.Object o, Type want, string s)
        {
            if (o == null || want == null || want.IsInstanceOfType(o)) return o;
            if (want == typeof(GameObject) && o is Component c) return c.gameObject;
            if (typeof(Component).IsAssignableFrom(want) && o is GameObject g)
            {
                var found = g.GetComponent(want);
                if (found == null) throw new Exception($"'{s}' has no {want.Name}");
                return found;
            }
            throw new Exception($"'{s}' is a {o.GetType().Name}, not a {want.Name}");
        }

        private UnityEngine.Object Asset(string s, Type want)
        {
            if (long.TryParse(s, out long id))
            {
                var o = Mgr != null ? Mgr.Get(id, true) : null;
                if (o == null) throw new Exception($"no game asset with id {id}");
                return o;
            }
            int slash = s.IndexOf('/');
            if (slash < 0) throw new Exception($"asset:'{s}': expected asset:<Type>/<name> or asset:<id>");
            var type = Types.Find(s.Substring(0, slash), typeof(UnityEngine.Object));
            string name = s.Substring(slash + 1);
            var all = Resources.FindObjectsOfTypeAll(type).Where(x => x != null && x.name == name).ToList();
            if (all.Count == 0) all = Resources.LoadAll("", type).Where(x => x != null && x.name == name).ToList(); // in Resources, not loaded yet
            if (all.Count == 0) throw new Exception($"no {type.Name} named '{name}' in the game");
            if (all.Count > 1) Plugin.Verbose($"asset:{s}: {all.Count} matches, using the first");
            return all[0];
        }

        private UnityEngine.Object File(string rel)
        {
            string full = Path.GetFullPath(Path.Combine(Dir ?? "", rel));
            if (Files.TryGetValue(full, out var have) && have != null)
            {
                Created?.Add(new KeyValuePair<string, UnityEngine.Object>("file:" + full, have));
                return have;
            }
            if (!System.IO.File.Exists(full)) throw new Exception($"file '{rel}' not found next to the template");
            UnityEngine.Object o;
            switch (Path.GetExtension(full).ToLowerInvariant())
            {
                case ".wav": o = Wav.Load(full); break;
                case ".png":
                case ".jpg":
                case ".jpeg":
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = Path.GetFileNameWithoutExtension(full) };
                    if (!ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(full))) throw new Exception($"'{rel}' is not a readable image");
                    o = tex;
                    break;
                }
                default: throw new Exception($"'{rel}': only .wav, .png and .jpg files load");
            }
            UnityEngine.Object.DontDestroyOnLoad(o);
            Files[full] = o;
            Created?.Add(new KeyValuePair<string, UnityEngine.Object>("file:" + full, o));
            return o;
        }
    }

    // component and asset types by name: full name, or short name (UnityEngine and the game's own first)
    internal static class Types
    {
        private static readonly Dictionary<string, Type> Cache = new Dictionary<string, Type>();

        public static Type Component(string name) => Find(name, typeof(Component));

        public static Type Find(string name, Type baseType)
        {
            string key = baseType.FullName + "|" + name;
            if (Cache.TryGetValue(key, out var have)) return have;
            var found = new List<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                catch { continue; }
                foreach (var t in types)
                    if (!t.IsAbstract && baseType.IsAssignableFrom(t) && (t.FullName == name || t.Name == name)) found.Add(t);
            }
            if (found.Count == 0) throw new Exception($"no {baseType.Name} type named '{name}'");
            var pick = found.FirstOrDefault(t => t.FullName == name)
                       ?? found.FirstOrDefault(t => t.Namespace != null && t.Namespace.StartsWith("UnityEngine"))
                       ?? found[0];
            if (found.Count > 1 && pick.FullName != name)
                Plugin.Log.LogWarning($"type '{name}' is ambiguous ({string.Join(", ", found.Select(t => t.FullName).ToArray())}); using {pick.FullName}");
            return Cache[key] = pick;
        }
    }

    // PCM / float WAV -> AudioClip
    internal static class Wav
    {
        public static AudioClip Load(string path)
        {
            var b = System.IO.File.ReadAllBytes(path);
            if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A') throw new Exception($"{Path.GetFileName(path)}: not a WAV file");
            int pos = 12, channels = 0, rate = 0, bits = 0, format = 0, dataPos = -1, dataLen = 0;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4);
                if (id == "fmt ")
                {
                    format = BitConverter.ToUInt16(b, pos + 8);
                    channels = BitConverter.ToUInt16(b, pos + 10);
                    rate = BitConverter.ToInt32(b, pos + 12);
                    bits = BitConverter.ToUInt16(b, pos + 22);
                    if (format == 0xFFFE && len >= 26) format = BitConverter.ToUInt16(b, pos + 32); // extensible: sub-format
                }
                else if (id == "data") { dataPos = pos + 8; dataLen = Math.Min(len, b.Length - dataPos); break; }
                pos += 8 + len + (len & 1);
            }
            if (dataPos < 0 || channels == 0) throw new Exception($"{Path.GetFileName(path)}: no fmt/data chunk");
            int bytes = bits / 8, n = dataLen / bytes;
            var s = new float[n];
            for (int i = 0; i < n; i++)
            {
                int p = dataPos + i * bytes;
                if (format == 3 && bits == 32) s[i] = BitConverter.ToSingle(b, p);
                else if (format == 1 && bits == 16) s[i] = BitConverter.ToInt16(b, p) / 32768f;
                else if (format == 1 && bits == 24) s[i] = ((b[p] | b[p + 1] << 8 | (sbyte)b[p + 2] << 16)) / 8388608f;
                else if (format == 1 && bits == 32) s[i] = BitConverter.ToInt32(b, p) / 2147483648f;
                else if (format == 1 && bits == 8) s[i] = (b[p] - 128) / 128f;
                else throw new Exception($"{Path.GetFileName(path)}: format {format}, {bits}-bit not supported (PCM 8/16/24/32 or float 32)");
            }
            var clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), n / channels, channels, rate, false);
            clip.SetData(s, 0);
            return clip;
        }
    }
}
