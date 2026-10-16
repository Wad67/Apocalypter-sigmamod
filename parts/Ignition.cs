using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SigmaMod.Parts
{
    // An ignition key: a click turns it on or off; held (on), it cranks the starter for as long as it is held. Its lamps
    // show off, on, and the starter; the dashboard is told as it turns (the game's Activate / Deactivate, so its gauges
    // come on). With no engine fitted there is no starter: holding the key does nothing. It works the game's engine
    // itself: held, the starter turns the engine, which starts if it has fuel; off, the
    // engine stops; out of fuel, or the engine taken out, it dies with the key still on. Something that runs the engine
    // itself (Engine Sim's simulated drivetrain) sets `external` and reads KeyOn and Cranking instead.
    //   key (what turns), offAngle / onAngle / crankAngle (degrees about its X), off / on / start (lamp objects),
    //   dashboard, fuel (the tank: LiquidAmount's Liquid), engineMount (the engine is fitted while it has a child),
    //   toggle / stop (clips: the key, the engine stopping), StarterClip (the starter, looped while cranking)
    public sealed class KeySwitch : Interactable
    {
        public Transform key;
        public float offAngle = -30f, onAngle = 30f, crankAngle = 45f, holdToCrank = 0.25f;
        public GameObject off, on, start, dashboard, fuel, engineMount;
        public AudioClip toggle, stop, StarterClip;
        public bool external;

        public bool KeyOn { get; private set; }
        public bool Cranking { get; private set; }
        public AudioSource Audio { get; private set; }

        private bool _pressed, _pressedWhileOn, _startTried;
        private float _pressTime;
        private FsmFloat _liquid;
        private NWH.VehiclePhysics2.VehicleController _vc;

        protected override string Title => KeyOn ? "Hold: start   Click: off" : "Ignition   Hold: start";

        private void OnEnable()
        {
            _vc = GetComponentInParent<NWH.VehiclePhysics2.VehicleController>();
            Audio = GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();
            Audio.playOnAwake = false; Audio.spatialBlend = 1f;
            var tank = fuel != null ? fuel.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "LiquidAmount") : null;
            _liquid = tank != null ? tank.FsmVariables.FindFsmFloat("Liquid") : null;
            Show();
        }

        private bool Fuelled => _liquid == null || _liquid.Value >= 0.01f;
        private bool EngineFitted => engineMount == null || engineMount.transform.childCount > 0;
        private NWH.VehiclePhysics2.Powertrain.EngineComponent Engine => _vc != null && _vc.powertrain != null ? _vc.powertrain.engine : null;

        protected override void Update()
        {
            base.Update();   // hover and the HUD; the key's own button handling is here
            bool pointed = Pointed && Cursor.lockState == CursorLockMode.Locked;
            if (!_pressed && pointed && Input.GetMouseButtonDown(0))
            {
                _pressed = true; _pressedWhileOn = KeyOn; _pressTime = Time.time; _startTried = false;
                if (!KeyOn) Turn(true);   // off -> on at once; held on, it goes on to the starter
            }
            if (_pressed)
            {
                // the starter is the engine's: with none fitted, holding the key turns nothing
                if (Input.GetMouseButton(0)) Cranking = KeyOn && EngineFitted && Time.time - _pressTime >= holdToCrank;
                else
                {
                    if (!Cranking && _pressedWhileOn && Time.time - _pressTime < holdToCrank) Turn(false);   // a click: off
                    _pressed = false; Cranking = false;
                }
            }
            if (Cranking && !EngineFitted) Cranking = false;   // taken out while cranking
            if (!external) Work();
            Show();
        }

        // the game's engine, when nothing else runs it
        private void Work()
        {
            var e = Engine;
            if (e == null) return;
            if (Cranking && !e.IsRunning && !e.starterActive && !_startTried && Fuelled && EngineFitted) { _startTried = true; e.StartEngine(); }
            if (e.IsRunning && (!KeyOn || !Fuelled || !EngineFitted)) { e.StopEngine(); Play(stop); }
            bool starter = Cranking && !e.IsRunning;
            if (starter && !Audio.isPlaying && StarterClip != null) { Audio.clip = StarterClip; Audio.loop = true; Audio.volume = 0.7f; Audio.Play(); }
            if (!starter && Audio.isPlaying && Audio.clip == StarterClip) Audio.Stop();
        }

        private void Turn(bool on)
        {
            KeyOn = on;
            Play(toggle);
            if (dashboard != null) foreach (var fsm in dashboard.GetComponentsInChildren<PlayMakerFSM>()) fsm.SendEvent(on ? "Activate" : "Deactivate");
        }

        private void Show()
        {
            if (key != null)
            {
                var a = key.localEulerAngles;
                float x = Cranking ? crankAngle : KeyOn ? onAngle : offAngle;
                if (Mathf.Abs(Mathf.DeltaAngle(a.x, x)) > 0.01f) key.localEulerAngles = new Vector3(x, a.y, a.z);
            }
            if (off != null && off.activeSelf != !KeyOn) off.SetActive(!KeyOn);
            if (on != null && on.activeSelf != (KeyOn && !Cranking)) on.SetActive(KeyOn && !Cranking);
            if (start != null && start.activeSelf != Cranking) start.SetActive(Cranking);
        }
    }
}
