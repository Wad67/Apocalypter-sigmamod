using System.Linq;
using UnityEngine;

namespace SigmaMod.Parts
{
    // A lever set in steps with the mouse (left: step on, right: step back), writing a property every frame and turning
    // its pivot with it. The throttle, the clutch, the steering, a belt clutch.
    //   target (a component) and property (a dotted path on it: "input.Throttle"), position (now), min (0; -1 for steering),
    //   step (per click; negative: the left button lowers it), pivot and angle (degrees about its X at full travel),
    //   more / less (what the buttons do, for the HUD)
    public sealed class Lever : Interactable
    {
        public Component target;
        public string property;
        public float position, min, step = 0.1f, angle = -40f;
        public Transform pivot;
        public string more = "More", less = "Less";

        private MemberPath _member;
        private Vector3 _rest;

        protected override string Line2 => position.ToString("0%");
        protected override string Use => more + ": Left mouse button / " + less + ": Right mouse button";

        private void OnEnable()
        {
            _member = new MemberPath(property);
            if (pivot != null) _rest = pivot.localEulerAngles;
        }

        protected override void Click(int button)
        {
            position = Mathf.Clamp(position + (button == 0 ? step : -step), min, 1f);
        }

        protected override void Update()
        {
            base.Update();
            if (pivot != null) pivot.localEulerAngles = new Vector3(position * angle, _rest.y, _rest.z);
            if (target != null) _member.Set(target, position);
        }
    }

    // A lever with two places, pulled and released with a click (a handbrake), with the game's sounds for each.
    //   target, property (set 1 pulled, 0 released, every frame), on, pivot and angle (degrees pulled), pulled / released (clips)
    public sealed class ToggleLever : Interactable
    {
        public Component target;
        public string property;
        public bool on;
        public Transform pivot;
        public float angle = -30f;
        public AudioClip pulled, released;
        public string use = "Pull / release: Left mouse button";

        private MemberPath _member;
        private Vector3 _rest;

        protected override string Line2 => on ? "On" : "Off";
        protected override string Use => use;

        private void OnEnable()
        {
            _member = new MemberPath(property);
            if (pivot != null) _rest = pivot.localEulerAngles;
        }

        protected override void Click(int button)
        {
            if (button != 0) return;
            on = !on;
            Play(on ? pulled : released);
        }

        protected override void Update()
        {
            base.Update();
            if (pivot != null) pivot.localEulerAngles = new Vector3((on ? 1f : 0f) * angle, _rest.y, _rest.z);
            if (target != null) _member.Set(target, on ? 1f : 0f);
        }
    }

    // A switch, clicked on and off: objects shown while on (a lamp, the switch's up position) or off, and the game's
    // events sent to the vehicle as it changes (the headlights' HeadlightsON / HeadlightsOFF).
    //   on, whenOn / whenOff (objects), eventOn / eventOff, clip (its click), state (true: "On" / "Off" on the HUD)
    public sealed class Switch : Interactable
    {
        public bool on;
        public GameObject[] whenOn = new GameObject[0], whenOff = new GameObject[0];
        public string eventOn, eventOff;
        public AudioClip clip;
        public bool state = true;

        private bool _shown;

        protected override string Line2 => state ? (on ? "On" : "Off") : null;
        protected override string Use => "Switch: Left mouse button";

        private void OnEnable() { Show(); }

        protected override void Click(int button)
        {
            if (button != 0) return;
            on = !on;
            Play(clip);
            Broadcast(on ? eventOn : eventOff);
            Show();
        }

        private void Show()
        {
            foreach (var g in whenOn.Where(g => g != null)) g.SetActive(on);
            foreach (var g in whenOff.Where(g => g != null)) g.SetActive(!on);
            _shown = on;
        }

        protected override void Update()
        {
            base.Update();
            if (_shown != on) Show();   // set from outside (Lua, a save)
        }
    }

    // A readout: the HUD's lines from properties of components (anything with a text, or any value: shown as it prints)
    //   sources (a component per line: name, line 2, line 3, use) and properties (the dotted path on each; empty: the line
    //   is `label` for the name, else empty)
    public sealed class Readout : Interactable
    {
        public Component[] sources = new Component[4];
        public string[] properties = new string[4];

        private MemberPath[] _paths;

        private void OnEnable() { _paths = properties.Select(p => string.IsNullOrEmpty(p) ? null : new MemberPath(p)).ToArray(); }

        private string Line(int i)
        {
            if (_paths == null || i >= _paths.Length || _paths[i] == null || i >= sources.Length || sources[i] == null) return null;
            return _paths[i].Get(sources[i])?.ToString();
        }

        protected override string Title => Line(0) ?? label;
        protected override string Line2 => Line(1);
        protected override string Line3 => Line(2);
        protected override string Use => Line(3);
    }
}
