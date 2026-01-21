using HarmonyLib;
using System;
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
            var fuel = __instance.GetData<FloatItemData>(DataEntryKey.Fuel)?.Value;

            __instance.startingFuseTime = CatchDynamiteEffect.FuseTime;
        }
    }
}
