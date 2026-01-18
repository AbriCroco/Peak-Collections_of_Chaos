/*using UnityEngine;
using Photon.Pun;
using Chaos.Manager;

namespace Chaos.Gameplay
{

    public class ModdedDynamiteTag : MonoBehaviour
    {
        public float fuseTime;  // Time remaining on the fuse.
        public bool isLit;      // Whether the dynamite is lit or not.


        void Update()
        {
            if (isLit)
            {
                fuseTime -= Time.deltaTime;
                if (fuseTime <= 0f)
                {
                    Explode();  // Handle the explosion logic here
                }
            }
        }

        void Explode()
        {
            // Add your explosion code here
            PhotonNetwork.Destroy(gameObject);  // Destroy the dynamite after it explodes.
        }
    }

    public class DynamiteThrowing : MonoBehaviour
    {
        public void ThrowDynamite(Player fromPlayer, Player toPlayer)
        {
            // Find the dynamite object in the throwing player's inventory
            var dynamiteGO = fromPlayer.GetItemInInventory(ModItemIDs.Dynamite);
            var dynamiteTag = dynamiteGO.GetComponent<ModdedDynamiteTag>();

            // Pass dynamite to the target player.
            if (toPlayer != null && dynamiteTag != null)
            {
                // Transfer ownership of the dynamite
                toPlayer.AddItem(ModItemIDs.Dynamite, dynamiteTag.fuseTime);

                // Set the dynamite's fuse time (continuing from where it left off)
                dynamiteTag.fuseTime -= 5f;  // Example: decrease by 5 seconds when thrown
                dynamiteTag.isLit = true;

                // Destroy the old dynamite object (it’s been passed to another player)
                PhotonNetwork.Destroy(dynamiteGO);
            }
        }
    }
}*/
