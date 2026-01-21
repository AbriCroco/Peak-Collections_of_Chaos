using System;
using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;
using Zorro.Core;
using Zorro.Core.Serizalization;
using Chaos.Utils;
using Chaos.Patches;
using Chaos.Effects;

namespace Chaos.Manager
{
    // Master-only manager that ticks mod-dynamite instance data (Fuel) and triggers explosion when time runs out.
    public class DynamiteManager : MonoBehaviourPun
    {
        public static DynamiteManager? Instance { get; private set; }

        private static readonly Dictionary<Guid, PhotonView> DynamiteItems = new();

        private HashSet<Guid> tracked = new HashSet<Guid>();
        private Coroutine? tickCoroutine;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // Call from master client when you have inserted the instanceData into inventory and want it ticked.
        public void RegisterInstance(ItemInstanceData instanceData)
        {
            ModLogger.Log($"[DynamiteManager] RegisterInstance {instanceData.guid}");
            if (instanceData == null) return;
            if (!PhotonNetwork.IsMasterClient)
                return;

            if (tracked.Add(instanceData.guid))
            {
                if (tickCoroutine == null)
                    tickCoroutine = StartCoroutine(TickRoutine());
            }
        }

        private IEnumerator TickRoutine()
        {
            while (tracked.Count > 0)
            {
                float dt = Time.deltaTime;
                var toProcess = new List<Guid>(tracked);

                foreach (var guid in toProcess)
                {
                    if (!ItemInstanceDataHandler.TryGetInstanceData(guid, out var idata))
                    {
                        tracked.Remove(guid);
                        continue;
                    }

                    if (!AddItemHelper.InstanceOwners.TryGetValue(guid, out var info))
                    {
                        ModLogger.Log($"[DynamiteManager] Waiting for owner info {guid} frame={Time.frameCount}");
                        continue;
                    }

                    Player? owner = info.player;

                    /*byte? slotId = info.slotId;

                    if (owner == null || slotId == null)
                        continue;

                    ItemSlot slot = owner.GetItemSlot(slotId.Value);
                    if (slot == null || slot.IsEmpty() || slot.data.guid != guid)
                    {
                        ModLogger.Log("[DynamiteManager] Item isn't in slot, stop ticking.");
                        tracked.Remove(guid);
                        continue;
                    }

                    Item currentItem = owner.character.data.currentItem;

                    if (currentItem != null && currentItem.data.guid == guid)
                    {
                        tracked.Remove(guid);1. Possible null reference return. [CS8603]
                        ModLogger.Log($"[DynamiteManager] Stopping coroutine for {idata.guid} (is held)");
                        continue;
                    } */

                    if (!idata.TryGetDataEntry<FloatItemData>(DataEntryKey.Fuel, out var fuelEntry))
                    {
                        tracked.Remove(guid);
                        continue;
                    }

                    bool hasWorldItem = TryGetWorldItem(guid, out var pv) && pv != null;

                    if (!hasWorldItem)
                    {
                        fuelEntry.Value -= dt;
                        float percentage = fuelEntry.Value / CatchDynamiteEffect.FuseTime;

                        if (idata.TryGetDataEntry<FloatItemData>(DataEntryKey.UseRemainingPercentage, out var remainingUse))
                        {
                            remainingUse.Value = Mathf.Clamp01(percentage);
                        }
                        if (Time.frameCount % 10 == 0)
                        {
                            if (owner != null && owner.view != null)
                            {
                                var inv = IBinarySerializable.ToManagedArray(new InventorySyncData(owner.itemSlots, owner.backpackSlot, owner.tempFullSlot));
                                owner.view.RPC("SyncInventoryRPC", RpcTarget.Others, inv, false);
                                ModLogger.Log($"[DynamiteManager] Synced inventory for {guid} percentage={percentage}");
                            }

                        }
                    }

                    if (Time.frameCount % 800 == 0)
                        ModLogger.Log($"[DynamiteManager] ticking {guid} fuel={fuelEntry.Value}");

                    if (fuelEntry.Value <= 0.01f)
                    {
                        HandleExplosionForInstance(idata, hasWorldItem);
                        tracked.Remove(guid);
                    }
                }

                yield return null;
            }

            tickCoroutine = null;
        }
        private void HandleExplosionForInstance(ItemInstanceData idata, bool hasWorldItem)
        {

            Player? owner = null;
            byte? slotId = null;
            Item? currentItem = null;

            if (AddItemHelper.InstanceOwners.TryGetValue(idata.guid, out var info))
            {
                owner = info.player;
                slotId = info.slotId;
            }

            if (owner == null || slotId == null)
                return;

            ItemSlot slot = owner.GetItemSlot(slotId.Value);
            var items = owner.character.refs.items;
            byte currentSlotId = items.currentSelectedSlot.Value;

            Vector3 spawnPos = Vector3.zero;
            Transform CharacterHip = owner.character.GetBodypart(BodypartType.Hip).transform;
            Vector3 center = CharacterHip.position;

            Campfire? camp = FindNearestUnlitCampfire(Character.localCharacter.Center);

            if (owner.character != null)
            {
                currentItem = owner.character.data.currentItem;

                if (camp != null)
                {
                    Vector3 firePos = camp.transform.position;

                    Vector3 toFire = firePos - center;
                    toFire.y = 0f;

                    float maxRadius = 0.6f;

                    Vector3 offset;
                    if (toFire.sqrMagnitude > maxRadius * maxRadius)
                    {
                        offset = toFire.normalized * maxRadius;
                    }
                    else
                    {
                        offset = toFire;
                    }
                    spawnPos = center + offset;
                }
                else
                {
                    spawnPos = center + CharacterHip.forward * 0.7f;
                }
            }

            if (hasWorldItem)
            {
                TryGetWorldItem(idata.guid, out var pv);
                if (currentItem != null && currentItem.data.guid == idata.guid)
                {
                    owner.EmptySlot(Optionable<byte>.Some(currentSlotId));
                    items.EquipSlot(Optionable<byte>.None);

                }
                if (pv != null)
                {
                    PhotonNetwork.Destroy(pv);
                    ModLogger.Log($"[DynamiteManager] Item detected - Deleting Item.");
                }

            }
            else
            {
                if (slot != null && !slot.IsEmpty() && slot.data.guid == idata.guid)
                {
                    try
                    {
                        ModLogger.Log($"[DynamiteManager] Still in slot {slot} - Deleting Item in slot.");
                        slot.EmptyOut();
                        if (currentSlotId == slotId)
                            items.EquipSlot(Optionable<byte>.None);
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Log($"Error emptying slot for explosion: {ex}");
                    }
                }

            }

            if (ItemDatabase.TryGetItem(ModItemIDs.Dynamite, out var dynamitePrefab))
            {
                var instantiated = PhotonNetwork.InstantiateItemRoom(dynamitePrefab.gameObject.name, spawnPos, Quaternion.identity);
                if (instantiated != null)
                {
                    var pv = instantiated.GetComponent<PhotonView>();
                    var item = instantiated.GetComponent<Item>();
                    if (pv != null && item != null)
                    {
                        pv.RPC("SetItemInstanceDataRPC", RpcTarget.AllBuffered, idata);
                        pv.RPC("RPC_Explode", RpcTarget.All);
                        ModLogger.Log($"[DynamiteManager] WE EXPLODEEE.");
                        AddItemHelper.InstanceOwners.Remove(idata.guid);
                    }
                }
                else
                {
                    ModLogger.Log("[DynamiteManager] Failed to instantiate dynamite for explosion.");
                }
            }
            else
            {
                ModLogger.Log("[DynamiteManager] Could not find dynamite prefab in ItemDatabase to spawn explosion.");
            }

            if (owner != null && owner.photonView != null)
            {
                var inv = new InventorySyncData(owner.itemSlots, owner.backpackSlot, owner.tempFullSlot);
                owner.view.RPC("SyncInventoryRPC", RpcTarget.Others, IBinarySerializable.ToManagedArray(inv), false);
                owner.photonView.RPC("RefreshAllCharacterCarryWeightRPC", RpcTarget.All);
            }
        }
        public static void RegisterDynamiteItem(Guid guid, PhotonView view)
        {
            if (view == null) return;
            DynamiteItems[guid] = view;
        }

        public static void UnregisterDynamiteItem(Guid guid, PhotonView view)
        {
            if (view == null) return;

            if (DynamiteItems.TryGetValue(guid, out var current))
            {
                if (current == view)
                {
                    DynamiteItems.Remove(guid);
                }
            }
        }

        public static bool TryGetWorldItem(Guid guid, out PhotonView view)
        {
            return DynamiteItems.TryGetValue(guid, out view);
        }

        public static Campfire? FindNearestUnlitCampfire(Vector3 fromPosition)
        {
            Campfire? nearest = null;
            float bestDistance = float.MaxValue;

            foreach (Campfire campfire in UnityEngine.Object.FindObjectsByType<Campfire>(FindObjectsSortMode.None))
            {
                if (campfire.state != Campfire.FireState.Off)
                    continue;

                float dist = Vector3.Distance(fromPosition, campfire.transform.position);

                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    nearest = campfire;
                }
            }
            return nearest;
        }
    }
}
