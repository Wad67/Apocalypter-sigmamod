using UnityEngine;

namespace SigmaMod.Parts
{
    // A lift kit's slot, doing what the game's do (its checkSuspension FSM on the slot, a Suspension FSM on every wheel
    // hinge): with a lift kit fitted, each wheel's spring gets more travel and softer damping and the wheel moves out
    // (a wider track), the lifted meshes show and the stock ones hide; taken off, all back to stock. Stock is what the
    // wheels have when the vehicle is made (its script, its tuning).
    //   wheels (the WheelControllers' objects), lifted / stock (objects shown with and without a kit),
    //   spring (lifted travel, m; 0: stock x 1.55, the game's 0.2 to 0.31), damper (lifted bump and rebound; 0: stock
    //   x 0.5, the game's 3000 to 1500), widen (m outward each side, the game's 0.22)
    public sealed class LiftKit : MonoBehaviour
    {
        public GameObject[] wheels = new GameObject[0];
        public GameObject[] lifted = new GameObject[0], stock = new GameObject[0];
        public float spring = 0f, damper = 0f, widen = 0.22f;

        private NWH.WheelController3D.WheelController[] _wc;
        private float[] _length, _bump, _rebound;
        private Vector3[] _pos;
        private int _state = -1;   // -1 not yet, 0 stock, 1 lifted
        private float _next;

        private void Start()
        {
            int n = wheels.Length;
            _wc = new NWH.WheelController3D.WheelController[n];
            _length = new float[n]; _bump = new float[n]; _rebound = new float[n]; _pos = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                if (wheels[i] == null) continue;
                _wc[i] = wheels[i].GetComponent<NWH.WheelController3D.WheelController>();
                _pos[i] = wheels[i].transform.localPosition;
                if (_wc[i] == null) continue;
                _length[i] = _wc[i].spring.maxLength;
                _bump[i] = _wc[i].damper.bumpRate;
                _rebound[i] = _wc[i].damper.reboundRate;
            }
        }

        private void Update()
        {
            if (_wc == null || Time.time < _next) return;
            _next = Time.time + 0.25f;
            bool fitted = false;
            foreach (Transform c in transform) if (c.CompareTag("vehPart")) { fitted = true; break; }
            int state = fitted ? 1 : 0;
            if (state == _state) return;
            _state = state;
            for (int i = 0; i < wheels.Length; i++)
            {
                if (wheels[i] == null) continue;
                var p = _pos[i];
                if (fitted) p.x += Mathf.Sign(p.x == 0f ? 1f : p.x) * widen;   // outward, on its own side
                wheels[i].transform.localPosition = p;
                var wc = _wc[i];
                if (wc == null) continue;
                wc.spring.maxLength = fitted ? (spring > 0f ? spring : _length[i] * 1.55f) : _length[i];
                wc.damper.bumpRate = fitted ? (damper > 0f ? damper : _bump[i] * 0.5f) : _bump[i];
                wc.damper.reboundRate = fitted ? (damper > 0f ? damper : _rebound[i] * 0.5f) : _rebound[i];
            }
            foreach (var g in lifted) if (g != null) g.SetActive(fitted);
            foreach (var g in stock) if (g != null) g.SetActive(!fitted);
        }
    }
}
