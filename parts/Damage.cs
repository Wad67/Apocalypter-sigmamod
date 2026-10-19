using HutongGames.PlayMaker;
using UnityEngine;

namespace SigmaMod.Parts
{
    // A damage zone: an actor (an NPC, an animal: layer 10) that comes into its trigger is hurt the way the game's own
    // zones hurt it, through its Bodypart FSM (Damage, then the Damage event). Two kinds, as the game has them:
    //   by speed, as a car's bumper (the game's CarAttack: over 5 m/s, 3 per m/s): perSpeed > 0, minSpeed;
    //   a fixed hit, as the spikes (the game's SpikeDamage: 4, then a second's rest): damage > 0, cooldown.
    // The speed is that of the body it moves with: its own loose, the vehicle's once fitted to one. onVehicle: only
    // while it is on a vehicle (spikes lying on the ground hurt nobody). sound / volume: played at the hit.
    public sealed class DamageZone : MonoBehaviour
    {
        public float perSpeed = 0f, minSpeed = 5f;
        public float damage = 0f, cooldown = 1f;
        public bool onVehicle = true;
        public AudioClip sound;
        public float volume = 0.7f;

        private const int Actor = 10;
        private float _next;

        private void OnTriggerEnter(Collider other)
        {
            if (other == null || other.gameObject.layer != Actor || Time.time < _next) return;
            if (onVehicle && GetComponentInParent<NWH.VehiclePhysics2.VehicleController>() == null) return;
            float hurt;
            if (perSpeed > 0f)
            {
                var rb = GetComponentInParent<Rigidbody>();
                float speed = rb != null ? rb.velocity.magnitude : 0f;
                if (speed <= minSpeed) return;
                hurt = speed * perSpeed;
            }
            else if (damage > 0f) { hurt = damage; _next = Time.time + cooldown; }
            else return;
            PlayMakerFSM body = null;
            foreach (var f in other.GetComponents<PlayMakerFSM>()) if (f.FsmName == "Bodypart") { body = f; break; }
            if (body == null) return;
            var v = body.FsmVariables.FindFsmFloat("Damage");
            if (v == null) return;
            v.Value = -hurt;
            foreach (var f in other.GetComponents<PlayMakerFSM>()) f.SendEvent("Damage");
            if (sound != null) AudioSource.PlayClipAtPoint(sound, transform.position, volume);
        }
    }
}
