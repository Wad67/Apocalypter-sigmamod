using HutongGames.PlayMaker;
using UnityEngine;

namespace SigmaMod.Parts
{
    // The driver as others see them: a seated player model shown while someone sits in the seat (the game parents the
    // player to it) and the camera isn't at their head, so it shows in third person and is out of the way in first,
    // whichever camera does the third person.
    //   model (the seated model), seat (where the player sits: sitPos), head (m: the camera this close to the seat is the
    //   driver's own eyes, default 0.5)
    public sealed class SeatedModel : MonoBehaviour
    {
        public GameObject model;
        public Transform seat;
        public float head = 0.5f;

        private GameObject _player;

        private void LateUpdate()
        {
            if (model == null || seat == null) return;
            if (_player == null) _player = FsmVariables.GlobalVariables.FindFsmGameObject("Player")?.Value;
            var cam = Camera.main;
            bool seated = _player != null && _player.transform.IsChildOf(seat);
            bool seen = cam != null && Vector3.Distance(cam.transform.position, seat.position) > head;
            bool show = seated && seen;
            if (model.activeSelf != show) model.SetActive(show);
        }
    }
}
