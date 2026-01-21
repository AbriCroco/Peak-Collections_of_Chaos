using System;
using HarmonyLib;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Zorro.Core.Serizalization;
using Photon.Pun;
using Chaos.Manager;
using Chaos.Utils;

namespace Chaos.Patches
{
    [HarmonyPatch(typeof(Player), "AddItem")]
    class AddItemPatch
    {
        static bool Prefix(
            ushort itemID,
            ref ItemInstanceData instanceData,
            ref ItemSlot? slot,
            Player __instance)
        {
            if (!SpecialItems.IsSpecialItem(itemID))
                return true;

            if (!SpecialItems.IsModSpawned(instanceData) && itemID == ModItemIDs.Dynamite)
                return true;

            if (instanceData == null)
            {
                instanceData = new ItemInstanceData(Guid.NewGuid());
                ItemInstanceDataHandler.AddInstanceData(instanceData);
            }
            if (!PhotonNetwork.IsMasterClient)
            {
                slot = null;
                return false;
            }

            if (!ItemDatabase.TryGetItem(itemID, out var ItemPrefab))
            {
                slot = null;
                return false;
            }

            var helperGO = __instance.gameObject.GetComponent<AddItemHelper>();
            if (helperGO == null)
                helperGO = __instance.gameObject.AddComponent<AddItemHelper>();

            helperGO.AddItemWithDrop(__instance, ItemPrefab, instanceData);
            if (slot == null)
                return false;

            byte[] array = IBinarySerializable.ToManagedArray(
                new InventorySyncData(
                    __instance.itemSlots,
                    __instance.backpackSlot,
                    __instance.tempFullSlot));
            __instance.view.RPC("SyncInventoryRPC", RpcTarget.Others, array, false);

            return false;
        }
    }

    public static class SpecialItems
    {
        private static readonly HashSet<ushort> RestrictedItems = new()
        {
            ModItemIDs.Dynamite
        };

        private static readonly ConditionalWeakTable<ItemInstanceData, ModFlags> table = new();

        public class ModFlags
        {
            public bool ModSpawnedSpecialItem = false;
        }

        public static bool IsSpecialItem(ushort itemID) => RestrictedItems.Contains(itemID);

        public static void SetModSpawned(ItemInstanceData data, bool value) =>
            table.GetOrCreateValue(data).ModSpawnedSpecialItem = value;

        public static bool IsModSpawned(ItemInstanceData data)
        {
            if (data == null) return false;
            return table.TryGetValue(data, out var flags) && flags.ModSpawnedSpecialItem;
        }
    }

    public class AddItemHelper : MonoBehaviour
    {
        public static readonly Dictionary<Guid, (Player player, byte slotId)> InstanceOwners = new();
        public void AddItemWithDrop(Player p, Item itemPrefab, ItemInstanceData instanceData)
        {
            if (itemPrefab.name == "Dynamite")
            {
                StartCoroutine(SpawnLitDynamite(p, itemPrefab, instanceData));
                return;
            }

            for (int i = 0; i < p.itemSlots.Length; i++)
            {
                if (p.itemSlots[i].IsEmpty())
                {
                    p.itemSlots[i].SetItem(itemPrefab, instanceData);
                    var invData = IBinarySerializable.ToManagedArray(new InventorySyncData(p.itemSlots, p.backpackSlot, p.tempFullSlot));
                    var pvPlayer = p.GetComponent<PhotonView>();
                    if (pvPlayer != null)
                    {
                        pvPlayer.RPC("SyncInventoryRPC", RpcTarget.Others, invData, false);
                    }
                    SpecialItems.SetModSpawned(instanceData, false);
                    return;
                }
            }

            int index = UnityEngine.Random.Range(0, 3);
            ItemSlot slotToDrop = p.itemSlots[index];
            StartCoroutine(DropThenAdd(p, slotToDrop, itemPrefab, instanceData));
        }

        private IEnumerator SpawnLitDynamite(Player p, Item itemPrefab, ItemInstanceData instanceData)
        {
            float currentFuse = 30f;
            if (instanceData.TryGetDataEntry<FloatItemData>(DataEntryKey.Fuel, out var fd))
            {
                currentFuse = fd.Value;
            }
            if (!instanceData.TryGetDataEntry<BoolItemData>(DataEntryKey.FlareActive, out var bd))
            {
                instanceData.RegisterEntry(DataEntryKey.FlareActive, new BoolItemData { Value = true });
            }

            if (PhotonNetwork.IsMasterClient)
            {
                if (DynamiteManager.Instance == null)
                {
                    var goMgr = new GameObject("DynamiteManager");
                    DontDestroyOnLoad(goMgr);
                    goMgr.AddComponent<DynamiteManager>();
                    yield return null;
                }
                DynamiteManager.Instance?.RegisterInstance(instanceData);
                ModLogger.Log($"[SpawnLitDynamite] registered instance {instanceData.guid} (fuse={currentFuse})");
            }

            for (int i = 0; i < p.itemSlots.Length; i++)
            {
                if (p.itemSlots[i].IsEmpty())
                {
                    p.itemSlots[i].SetItem(itemPrefab, instanceData);
                    InstanceOwners[instanceData.guid] = (p, p.itemSlots[i].itemSlotID);
                    var invData = IBinarySerializable.ToManagedArray(new InventorySyncData(p.itemSlots, p.backpackSlot, p.tempFullSlot));
                    var pvPlayer = p.GetComponent<PhotonView>();
                    if (pvPlayer != null)
                    {
                        pvPlayer.RPC("SyncInventoryRPC", RpcTarget.Others, invData, false);
                    }
                    SpecialItems.SetModSpawned(instanceData, false);
                    ModLogger.Log($"[SpawnLitDynamite] placed instance {instanceData.guid} into slot {i} of player {p.name}");
                    yield break;
                }
            }

            int index = UnityEngine.Random.Range(0, 3);
            StartCoroutine(DropThenAdd(p, p.itemSlots[index], itemPrefab, instanceData));
        }

        private IEnumerator DropThenAdd(Player p, ItemSlot slotToDrop, Item itemPrefab, ItemInstanceData instanceData)
        {
            if (slotToDrop == null || slotToDrop.data == null)
            {
                ModLogger.Log("[DropThenAdd] Slot or data is null!");
                yield break;
            }

            byte slotIndex = slotToDrop.itemSlotID;

            Transform obj = p.character.GetBodypart(BodypartType.Hip).transform;
            Vector3 vector = obj.forward;
            Vector3 spawnPos = obj.position + vector * 0.6f;
            p.character.photonView.RPC("DropItemFromSlotRPC", RpcTarget.All, slotIndex, spawnPos);

            yield return new WaitUntil(() =>
            {
                bool empty = p.itemSlots[slotIndex].IsEmpty();
                return empty;
            });

            p.itemSlots[slotIndex].SetItem(itemPrefab, instanceData);
            InstanceOwners[instanceData.guid] = (p, p.itemSlots[slotIndex].itemSlotID);

            if (PhotonNetwork.IsMasterClient && itemPrefab.name == "Dynamite")
            {
                if (DynamiteManager.Instance == null)
                {
                    var goMgr = new GameObject("DynamiteManager");
                    DontDestroyOnLoad(goMgr);
                    goMgr.AddComponent<DynamiteManager>();
                    yield return null;
                }
                DynamiteManager.Instance?.RegisterInstance(instanceData);
            }

            var invData = IBinarySerializable.ToManagedArray(new InventorySyncData(p.itemSlots, p.backpackSlot, p.tempFullSlot));
            var pvPlayer = p.GetComponent<PhotonView>();
            if (pvPlayer != null)
            {
                pvPlayer.RPC("SyncInventoryRPC", RpcTarget.Others, invData, false);
            }

            SpecialItems.SetModSpawned(instanceData, false);
            ModLogger.Log($"[DropThenAddThenRegister] placed instance {instanceData.guid} into slot {slotIndex} of player {p.name}");
        }
    }
}
