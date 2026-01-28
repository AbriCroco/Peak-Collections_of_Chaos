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
        private static float WaitTime = 5f;
        private static float BoomTime = 0.1f;
        public static DynamiteManager? Instance { get; private set; }

        private static Dictionary<Guid, bool> OwnerChanged = new();
        private static readonly Dictionary<Guid, PhotonView> DynamiteItems = new();

        private static HashSet<Guid> tracked = new HashSet<Guid>();
        private static HashSet<Guid> pendingDrop = new HashSet<Guid>();
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

        public static void ClearDynamiteManager()
        {
            tracked.Clear();
            pendingDrop.Clear();
            OwnerChanged.Clear();
            AddItemHelper.InstanceOwners.Clear();
        }

        // Call from master client when you have inserted the instanceData into inventory and want it ticked.
        public void RegisterInstance(ItemInstanceData instanceData)
        {
            if (!PhotonNetwork.IsMasterClient)
                return;

            if (instanceData == null)
                return;

            if (tracked.Contains(instanceData.guid))
                return;

            ModLogger.Log($"[DynamiteManager] RegisterInstance {instanceData.guid}");

            tracked.Add(instanceData.guid);
            OwnerChanged[instanceData.guid] = false;

            if (tickCoroutine == null)
                tickCoroutine = StartCoroutine(TickRoutine());
        }

        private IEnumerator TickRoutine()
        {
            int buffer = 10;

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

                    Player owner = info.player;

                    if (!idata.TryGetDataEntry<FloatItemData>(DataEntryKey.Fuel, out var fuelEntry))
                    {
                        tracked.Remove(guid);
                        continue;
                    }

                    bool hasWorldItem = TryGetWorldItem(guid, out var pv) && pv != null;

                    if (!hasWorldItem)
                    {
                        fuelEntry.Value -= dt;
                        float percentage = 1000f;

                        if (PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(CatchDynamiteEffect.DynamiteFuseKeys.Key(guid), out var maxFuse))
                        {
                            percentage = fuelEntry.Value / (float)maxFuse;
                        }

                        if (!idata.TryGetDataEntry<FloatItemData>(DataEntryKey.UseRemainingPercentage, out var remainingUse))
                        {
                            remainingUse = idata.RegisterEntry<FloatItemData>(DataEntryKey.UseRemainingPercentage, new FloatItemData { Value = percentage });
                        }
                        else
                        {
                            remainingUse.Value = percentage;
                        }

                        if (Time.frameCount % 30 == 0)
                        {
                            if (owner.view != null)
                            {
                                var inv = IBinarySerializable.ToManagedArray(new InventorySyncData(owner.itemSlots, owner.backpackSlot, owner.tempFullSlot));
                                owner.view.RPC("SyncInventoryRPC", RpcTarget.Others, inv, false);
                            }
                        }
                    }
                    if (hasWorldItem || fuelEntry.Value <= BoomTime)
                    {
                        if (OwnerChanged[guid] == true)
                        {
                            if (buffer > 0)
                            {
                                buffer -= 1;
                                continue;
                            }
                            else
                            {
                                OwnerChanged[guid] = false;
                                buffer = 10;
                            }
                        }

                        if (!pendingDrop.Contains(guid) && pv != null)
                        {
                            pendingDrop.Add(guid);
                            StartCoroutine(BackToSlot(info.player, info.slot, idata));
                        }

                        if (fuelEntry.Value <= BoomTime && IsItemInSlot(owner, info.slot, idata, shouldExist: true))
                        {
                            HandleExplosionForInstance(idata, hasWorldItem);
                            PhotonNetwork.CurrentRoom.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { CatchDynamiteEffect.DynamiteFuseKeys.Key(guid), null } });
                            pendingDrop.Remove(guid);
                            tracked.Remove(guid);
                        }
                    }
                }

                yield return null;
            }

            tickCoroutine = null;
        }

        private IEnumerator BackToSlot(Player p, ItemSlot slot, ItemInstanceData idata)
        {
            float timer = WaitTime;
            float closeRadiusSqr = 10f * 10f;
            float touchRadiusSqr = 1f * 1f;

            if (p == null || p.character == null)
            {
                ModLogger.Log($"[BackToSlot] Null, stopping");
                pendingDrop.Remove(idata.guid);
                yield break;
            }

            Transform ownerHip = p.character.GetBodypart(BodypartType.Hip).transform;

            while (tracked.Contains(idata.guid) && pendingDrop.Contains(idata.guid) && p != null)
            {
                if (AddItemHelper.InstanceOwners.TryGetValue(idata.guid, out var info))
                {
                    if (info.player != p)
                    {
                        ModLogger.Log($"[BackToSlot] Owner Changed, stopping");
                        pendingDrop.Remove(idata.guid);
                        yield break;
                    }
                }

                if (!idata.TryGetDataEntry<FloatItemData>(DataEntryKey.Fuel, out var fuelEntry))
                {
                    pendingDrop.Remove(idata.guid);
                    yield break;
                }

                if (!TryGetWorldItem(idata.guid, out var pv) || pv == null)
                {
                    pendingDrop.Remove(idata.guid);
                    yield break;
                }

                Vector3 dynamitePos = pv.transform.position;
                Vector3 offset = dynamitePos - ownerHip.position;

                bool isInsideZone = offset.sqrMagnitude <= closeRadiusSqr;
                Character? touched = FindTouchedCharacter(dynamitePos, p.character, touchRadiusSqr);

                if (touched != null && OwnerChanged[idata.guid] == false)
                {
                    TransferDynamiteToPlayer(p, touched.player, slot, idata);
                    OwnerChanged[idata.guid] = true;
                    yield break;
                }

                timer = isInsideZone ? WaitTime : timer - Time.deltaTime;

                if (timer <= 0f || fuelEntry.Value <= BoomTime + 0.25f)
                {
                    if (IsItemInSlot(p, slot, idata, shouldExist: true))
                    {
                        pendingDrop.Remove(idata.guid);
                        yield break;
                    }

                    var helper = p.GetComponent<AddItemHelper>();
                    if (helper != null)
                    {
                        PhotonNetwork.Destroy(pv);
                        helper.StartDropThenAdd(p, slot, AddItemHelper.DynamiteItem, idata);
                        p.character.refs.items.EquipSlot(Optionable<byte>.Some(slot.itemSlotID));
                    }
                    pendingDrop.Remove(idata.guid);
                    yield break;
                }
                yield return null;
            }
        }
        private void HandleExplosionForInstance(ItemInstanceData idata, bool hasWorldItem)
        {

            Player? owner = null;
            byte? slotId = null;
            Item? currentItem = null;

            if (AddItemHelper.InstanceOwners.TryGetValue(idata.guid, out var info))
            {
                owner = info.player;
                slotId = info.slot.itemSlotID;
            }

            if (owner == null || slotId == null)
                return;

            ItemSlot slot = info.slot;
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
                    spawnPos = center + CharacterHip.forward * 0.6f;
                }
            }

            if (hasWorldItem)
            {
                TryGetWorldItem(idata.guid, out var pv);
                if (currentItem != null && currentItem.data.guid == idata.guid)
                {
                    owner.EmptySlot(Optionable<byte>.Some(currentSlotId));
                    items.EquipSlot(Optionable<byte>.None);
                    owner.photonView.RPC("DestroyHeldItemRpc", RpcTarget.All);
                }
                if (pv != null)
                {
                    PhotonNetwork.Destroy(pv);
                    ModLogger.Log($"[DynamiteManager] Item detected - Deleting Item.");
                }

            }

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

        private bool IsItemInSlot(Player p, ItemSlot slot, ItemInstanceData idata, bool shouldExist)
        {
            if (p == null || slot == null || idata == null)
                return false;

            bool inSlot = !slot.IsEmpty() && slot.data != null && slot.data.guid == idata.guid;
            return shouldExist ? inSlot : !inSlot;
        }

        private Character? FindTouchedCharacter(Vector3 dynamitePos, Character owner, float touchRadiusSqr)
        {
            foreach (var character in PlayerHandler.GetAllPlayerCharacters())
            {
                if (character == null) continue;
                if (character == owner) continue;
                if (character.isBot) continue;
                if (character.data.dead) continue;
                if (!character.data.fullyConscious) continue;

                var occupiedSlotIds = new HashSet<byte>();
                foreach (var entry in AddItemHelper.InstanceOwners.Values)
                {
                    if (entry.player == character.player)
                    {
                        occupiedSlotIds.Add(entry.slot.itemSlotID);
                    }
                }
                if (occupiedSlotIds.Count == 3) continue;

                var hip = character.GetBodypart(BodypartType.Hip)?.transform;
                if (hip == null) continue;

                Vector3 offset = dynamitePos - hip.position;
                if (offset.sqrMagnitude <= touchRadiusSqr)
                    return character;
            }
            return null;
        }
        private void TransferDynamiteToPlayer(Player from, Player to, ItemSlot fromSlot, ItemInstanceData idata)
        {
            pendingDrop.Remove(idata.guid);

            if (IsItemInSlot(from, fromSlot, idata, shouldExist: true))
            {
                from.EmptySlot(Optionable<byte>.Some(fromSlot.itemSlotID));
                from.character.refs.items.EquipSlot(Optionable<byte>.None);
            }

            Item currentItem = from.character.data.currentItem;

            if (currentItem != null && currentItem.data.guid == idata.guid)
            {
                from.photonView.RPC("DestroyHeldItemRpc", RpcTarget.All);
            }

            if (TryGetWorldItem(idata.guid, out var pv) && pv != null)
            {
                PhotonNetwork.Destroy(pv);
            }

            SpecialItems.SetModSpawned(idata, true);
            to.AddItem(ModItemIDs.Dynamite, idata, out var toSlot);
            if (toSlot != null)
            {
                ModLogger.Log("Item ADDED");
                to.character.refs.items.EquipSlot(Optionable<byte>.Some(toSlot.itemSlotID));
            }
        }
    }
}
