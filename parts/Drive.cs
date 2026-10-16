using UnityEngine;

namespace SigmaMod.Parts
{
    // A gear shifter: a click shifts up (left) or down (right) through the vehicle's gearbox, the HUD shows the gear, and
    // the fitted gear lever leans to each gear's place in the gate.
    //   target (the VehicleController), lever (what leans: the lever's joint), gate (its angles, degrees: reverse,
    //   neutral, then 1st, 2nd, ...; a gear past the end keeps the last)
    public sealed class GearShifter : Interactable
    {
        public Component target;
        public Transform lever;
        public Vector3[] gate = new Vector3[0];

        private readonly MemberPath _name = new MemberPath("powertrain.transmission.GearName");
        private readonly MemberPath _gear = new MemberPath("powertrain.transmission.Gear");
        private readonly MemberPath _up = new MemberPath("input.ShiftUp"), _down = new MemberPath("input.ShiftDown");

        protected override string Line2 => _name.Get(target)?.ToString();
        protected override string Use => "Up: Left mouse button / Down: Right mouse button";

        protected override void Click(int button) { (button == 0 ? _up : _down).Set(target, true); }

        protected override void Update()
        {
            base.Update();
            if (lever == null || gate.Length == 0 || target == null) return;
            int gear = _gear.Get(target) is int g ? g : 0;
            var a = gate[Mathf.Clamp(gear + 1, 0, gate.Length - 1)];
            if (lever.localEulerAngles != a) lever.localEulerAngles = a;
        }
    }

    // A hub a game wheel is fitted to: the fitted wheel's mesh and material drawn on the rim, and with none fitted a bare
    // hub, the WheelController then a bare hub's size, weight and grip (a fitted wheel sets its own).
    //   rim (what draws it), bareMesh / bareMaterial, radius, mass, grip (the bare hub's)
    public sealed class WheelHub : MonoBehaviour
    {
        public GameObject rim;
        public Mesh bareMesh;
        public Material bareMaterial;
        public float radius = 0.1f, mass = 10f, grip = 0.1f;

        private GameObject _fitted;
        private bool _bare;
        private float _next;

        private void OnEnable() { _next = 0f; _fitted = null; _bare = false; }

        private void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 0.25f;
            GameObject wheel = null;
            foreach (Transform c in transform) if (c.CompareTag("vehPart")) { wheel = c.gameObject; break; }
            var mf = rim != null ? rim.GetComponent<MeshFilter>() : null;
            var mr = rim != null ? rim.GetComponent<MeshRenderer>() : null;
            if (wheel != null)
            {
                if (wheel == _fitted) return;
                _fitted = wheel; _bare = false;
                var wm = wheel.GetComponent<MeshFilter>();
                var wr = wheel.GetComponent<Renderer>();
                if (mf != null && wm != null) mf.sharedMesh = wm.sharedMesh;
                if (mr != null && wr != null) mr.sharedMaterial = wr.sharedMaterial;
                return;
            }
            if (_bare) return;
            _bare = true; _fitted = null;
            if (mf != null) mf.sharedMesh = bareMesh;
            if (mr != null) mr.sharedMaterial = bareMaterial;
            var wc = GetComponent<NWH.WheelController3D.WheelController>();
            if (wc != null) { wc.Radius = radius; wc.Mass = mass; wc.forwardFriction.grip = grip; }
        }
    }
}
