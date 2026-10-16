using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SigmaMod.Parts
{
    // A fuel tank. What the game and other mods see of it are its FSMs' variables, kept as they are: LiquidAmount's Liquid
    // and LiquidCapacity (a can pours into the tank's volume by itself, mods read the level), and FuelConsumption's
    // FuelConsumption (the fitted engine writes its rate there, litres per second per rpm, negative). This burns the fuel:
    // while the engine turns over (above 30 rpm) the level falls by rpm x that rate, and it stays between empty and full.
    public sealed class FuelTank : MonoBehaviour
    {
        private FsmFloat _liquid, _capacity, _rate;
        private NWH.VehiclePhysics2.VehicleController _vc;

        public float Liquid => _liquid != null ? _liquid.Value : 0f;
        public float Capacity => _capacity != null ? _capacity.Value : 0f;

        private void OnEnable()
        {
            _vc = GetComponentInParent<NWH.VehiclePhysics2.VehicleController>();
            var fsms = GetComponents<PlayMakerFSM>();
            var level = fsms.FirstOrDefault(f => f.FsmName == "LiquidAmount");
            var use = fsms.FirstOrDefault(f => f.FsmName == "FuelConsumption");
            _liquid = level?.FsmVariables.FindFsmFloat("Liquid");
            _capacity = level?.FsmVariables.FindFsmFloat("LiquidCapacity");
            _rate = use?.FsmVariables.FindFsmFloat("FuelConsumption");
        }

        private void Update()
        {
            if (_liquid == null) return;
            float rpm = _vc != null && _vc.powertrain != null && _vc.powertrain.engine != null ? _vc.powertrain.engine.OutputRPM : 0f;
            if (rpm > 30f && _rate != null) _liquid.Value += rpm * _rate.Value * Time.deltaTime;
            float cap = _capacity != null ? _capacity.Value : float.MaxValue;
            _liquid.Value = Mathf.Clamp(_liquid.Value, 0f, cap);
        }
    }

    // A tank's filler cap: looked at, the tank's fuel and level; F opens and closes it. Open, the tank's volume stops
    // taking cans (its collider off), and the siphon's hose end is out: a can of the same fuel held to it draws the tank
    // off into itself, a litre a second, until the tank is empty or the can full. A hose mesh shows while it is open.
    //   tank (the tank: its tag is its fuel), hose (the hose end: a trigger collider), hoseMesh (shown while open),
    //   cap (the cap's sound), filling (the fuel running, looped), rate (litres a second)
    public sealed class Filler : Interactable
    {
        public GameObject tank, hose, hoseMesh;
        public AudioClip cap, filling;
        public float rate = 1f;
        public bool open;

        private FsmFloat _liquid, _capacity;
        private Collider _tankVolume, _hoseEnd;
        private AudioSource _audio;
        private readonly HashSet<Collider> _cans = new HashSet<Collider>();
        private bool _shown;

        protected override string Title => tank != null ? tank.tag : "Fuel";
        protected override string Line2 => _liquid != null ? _liquid.Value.ToString(".0") : null;
        protected override string Line3 => _capacity != null ? "/ " + _capacity.Value.ToString("0") + "L" : null;
        protected override string Use => open ? "Close: F" : "Syphon Fuel: F";

        private void OnEnable()
        {
            var level = tank != null ? tank.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "LiquidAmount") : null;
            _liquid = level?.FsmVariables.FindFsmFloat("Liquid");
            _capacity = level?.FsmVariables.FindFsmFloat("LiquidCapacity");
            _tankVolume = tank != null ? tank.GetComponent<Collider>() : null;
            _hoseEnd = hose != null ? hose.GetComponent<Collider>() : null;
            if (hose != null) (hose.GetComponent<TriggerTouch>() ?? hose.AddComponent<TriggerTouch>()).Touching = _cans;
            _audio = GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false; _audio.spatialBlend = 1f;
            _shown = !open;
            Show();
        }

        protected override void Update()
        {
            base.Update();
            if (Pointed && Cursor.lockState == CursorLockMode.Locked && Input.GetButtonDown("Use"))
            {
                open = !open;
                Play(cap);
            }
            Show();
            Siphon();
        }

        private void Show()
        {
            if (_shown == open) return;
            _shown = open;
            if (_tankVolume != null) _tankVolume.enabled = !open;
            if (_hoseEnd != null) _hoseEnd.enabled = open;
            if (hoseMesh != null) foreach (var r in hoseMesh.GetComponentsInChildren<Renderer>(true)) r.enabled = open;
            if (!open) _cans.Clear();
            if (_audio.isPlaying) _audio.Stop();
        }

        // tank to can, while a can of the tank's fuel touches the hose end
        private void Siphon()
        {
            bool running = false;
            if (open && _liquid != null && _liquid.Value > 0.0001f && tank != null)
            {
                foreach (var c in _cans)
                {
                    if (c == null || !c.CompareTag(tank.tag)) continue;
                    var body = c.attachedRigidbody != null ? c.attachedRigidbody.gameObject : c.gameObject;
                    var can = c.GetComponents<PlayMakerFSM>().Concat(body.GetComponents<PlayMakerFSM>()).FirstOrDefault(f => f.FsmName == "LiquidAmount");
                    var into = can?.FsmVariables.FindFsmFloat("Liquid");
                    var room = can?.FsmVariables.FindFsmFloat("LiquidCapacity");
                    if (into == null || room != null && into.Value >= room.Value) continue;
                    float d = Mathf.Min(rate * Time.deltaTime, _liquid.Value, room != null ? room.Value - into.Value : float.MaxValue);
                    _liquid.Value -= d; into.Value += d;
                    running = d > 0f;
                    break;
                }
            }
            if (running && !_audio.isPlaying && filling != null) { _audio.clip = filling; _audio.loop = true; _audio.Play(); }
            if (!running && _audio.isPlaying && _audio.clip == filling) _audio.Stop();
        }
    }

    // A trigger collider's touching colliders, for the part that owns it
    public sealed class TriggerTouch : MonoBehaviour
    {
        public HashSet<Collider> Touching;
        private void OnTriggerEnter(Collider c) { Touching?.Add(c); }
        private void OnTriggerExit(Collider c) { Touching?.Remove(c); }
        private void OnDisable() { Touching?.Clear(); }
    }
}
