using System;
using HarmonyLib;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Zorro.Core.Serizalization;
using Zorro.Core;
using Photon.Pun;
using Chaos.Manager;
using Chaos.Utils;

namespace Chaos.Patches
{
    [HarmonyPatch(typeof(Player), "AddItem")]
    class AddItemPatch
    {
        static bool Prefix(ushort itemID, ref ItemInstanceData instanceData, ref ItemSlot? slot, Player __instance)
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

            byte[] array = IBinarySerializable.ToManagedArray(new InventorySyncData(__instance.itemSlots, __instance.backpackSlot, __instance.tempFullSlot));
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
        private static Item? _dynamiteItem;
        public static Item DynamiteItem
        {
            get
            {
                if (_dynamiteItem == null)
                {
                    ItemDatabase.TryGetItem(ModItemIDs.Dynamite, out _dynamiteItem);
                }
                return _dynamiteItem;
            }
        }
        public static readonly Dictionary<Guid, (Player player, ItemSlot slot)> InstanceOwners = new();

        public void AddItemWithDrop(Player p, Item itemPrefab, ItemInstanceData instanceData)
        {
            if (!PhotonNetwork.IsMasterClient)
                return;

            if (itemPrefab == DynamiteItem)
            {
                StartCoroutine(SpawnLitDynamite(p, instanceData));
                return;
            }

            var items = p.character.refs.items;
            byte currentSlotId = items.currentSelectedSlot.Value;

            for (int i = 0; i < p.itemSlots.Length; i++)
            {
                if (p.itemSlots[i].IsEmpty())
                {
                    if (p.itemSlots[i].itemSlotID == currentSlotId)
                    {
                        items.EquipSlot(Optionable<byte>.None);
                    }

                    p.itemSlots[i].SetItem(itemPrefab, instanceData);
                    SpecialItems.SetModSpawned(instanceData, false);
                    return;
                }
            }

            int index = UnityEngine.Random.Range(0, 3);
            ItemSlot slotToDrop = p.itemSlots[index];
            if (slotToDrop.itemSlotID == currentSlotId)
            {
                items.EquipSlot(Optionable<byte>.None);
            }

            StartDropThenAdd(p, slotToDrop, itemPrefab, instanceData);
        }

        private IEnumerator SpawnLitDynamite(Player p, ItemInstanceData instanceData)
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

            var items = p.character.refs.items;
            byte currentSlotId = items.currentSelectedSlot.Value;

            for (int i = 0; i < p.itemSlots.Length; i++)
            {
                if (p.itemSlots[i].IsEmpty())
                {
                    if (p.itemSlots[i].itemSlotID == currentSlotId)
                    {
                        items.EquipSlot(Optionable<byte>.None);
                    }

                    p.itemSlots[i].SetItem(DynamiteItem, instanceData);
                    InstanceOwners[instanceData.guid] = (p, p.itemSlots[i]);

                    SpecialItems.SetModSpawned(instanceData, false);
                    ModLogger.Log($"[SpawnLitDynamite] placed instance {instanceData.guid} into slot {i} of player {p.name}");
                    yield break;
                }
            }

            List<ItemSlot> availableSlots = new List<ItemSlot>(p.itemSlots);
            var occupiedSlotIds = new HashSet<byte>();

            foreach (var entry in InstanceOwners.Values)
            {
                if (entry.player == p)
                {
                    occupiedSlotIds.Add(entry.slot.itemSlotID);
                }
            }

            availableSlots.RemoveAll(s => occupiedSlotIds.Contains(s.itemSlotID));

            if (availableSlots.Count == 0)
                yield break;

            int index = UnityEngine.Random.Range(0, availableSlots.Count);
            ItemSlot slotToDrop = availableSlots[index];

            if (slotToDrop.itemSlotID == currentSlotId)
            {
                items.EquipSlot(Optionable<byte>.None);
            }
            StartDropThenAdd(p, slotToDrop, DynamiteItem, instanceData);
        }

        public void StartDropThenAdd(Player p, ItemSlot slotToDrop, Item itemPrefab, ItemInstanceData instanceData)
        {
            StartCoroutine(DropThenAdd(p, slotToDrop, itemPrefab, instanceData));
        }

        private IEnumerator DropThenAdd(Player p, ItemSlot slotToDrop, Item itemPrefab, ItemInstanceData instanceData)
        {
            if (slotToDrop == null || slotToDrop.data == null)
            {
                ModLogger.Log("[DropThenAdd] Slot or data is null!");
                yield break;
            }

            if (!slotToDrop.IsEmpty())
            {
                var items = p.character.refs.items;
                byte currentSlotId = items.currentSelectedSlot.Value;
                ItemSlot currentSlot = p.GetItemSlot(currentSlotId);
                Item currentItem = p.character.data.currentItem;

                Transform obj = p.character.GetBodypart(BodypartType.Hip).transform;
                Vector3 vector = obj.forward;
                Vector3 spawnPos = obj.position + vector * 0.6f;
                p.character.photonView.RPC("DropItemFromSlotRPC", RpcTarget.All, slotToDrop.itemSlotID, spawnPos);

                if (currentItem != null && currentSlot.data == slotToDrop.data)
                {
                    p.photonView.RPC("DestroyHeldItemRpc", RpcTarget.All);
                    //PhotonNetwork.Destroy(currentItem.photonView);
                }

                yield return new WaitUntil(() => slotToDrop.IsEmpty());
            }

            InstanceOwners[instanceData.guid] = (p, slotToDrop);
            slotToDrop.SetItem(itemPrefab, instanceData);

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

            SpecialItems.SetModSpawned(instanceData, false);
        }
    }
}
