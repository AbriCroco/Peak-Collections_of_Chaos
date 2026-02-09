using HarmonyLib;
using System;
using Photon.Pun;
using Chaos.Manager;
using Chaos.Effects;

namespace Chaos.Patches
{

    [HarmonyPatch(typeof(Item), "SetItemInstanceDataRPC")]
    class SetItemInstanceDataRPCPatch
    {
        static bool IsDynamite(Item item)
        {
            if (item == null) return false;
            if (item.data == null) return false;
            return item.itemID == ModItemIDs.Dynamite;
        }
        static void Postfix(Item __instance, ItemInstanceData instanceData)
        {
            if (!PhotonNetwork.IsMasterClient)
                return;

            if (!IsDynamite(__instance))
                return;

            if (__instance != null && instanceData != null)
            {
                Guid guid = instanceData.guid;
                DynamiteManager.RegisterDynamiteItem(instanceData.guid, __instance.photonView);
            }
        }
    }

    [HarmonyPatch(typeof(Item), "OnDestroy")]
    class OnDestroyPatch
    {
        static void Prefix(Item __instance)
        {
            if (!PhotonNetwork.IsMasterClient)
                return;

            var data = __instance?.data;
            var pv = __instance?.photonView;

            if (data != null && pv != null)
            {
                Guid? guid = __instance?.data.guid;

                if (guid.HasValue)
                {
                    DynamiteManager.UnregisterDynamiteItem(guid.Value, pv);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Dynamite), nameof(Dynamite.OnInstanceDataSet))]
    class DynamiteOnInstanceDataSetPatch
    {
        static void Postfix(Dynamite __instance)
        {
            if (__instance.item?.data.guid != null)
            {
                Guid guid = __instance.item.data.guid;
                string key = CatchDynamiteEffect.DynamiteFuseKeys.Key(guid);

                if (PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(key, out var value))
                {
                    __instance.startingFuseTime = (float)value;
                }
            }
        }
    }
}
