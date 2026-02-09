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
        private static Dictionary<Guid, int> OwnerChangeBuffer = new();
        private static Dictionary<Guid, HashSet<PhotonView>> DynamiteItems = new();

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
            OwnerChangeBuffer.Clear();
            AddItemHelper.InstanceOwners.Clear();
            CatchDynamiteEffect.availableSlots.Clear();
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
            OwnerChangeBuffer[instanceData.guid] = 60;

            if (tickCoroutine == null)
                tickCoroutine = StartCoroutine(TickRoutine());
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
                        OwnerChanged.Remove(guid);
                        OwnerChangeBuffer.Remove(guid);
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
                        OwnerChanged.Remove(guid);
                        OwnerChangeBuffer.Remove(guid);
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
                            if (OwnerChangeBuffer[guid] > 0)
                            {
                                OwnerChangeBuffer[guid] -= 1;
                                continue;
                            }
                            else
                            {
                                OwnerChanged[guid] = false;
                                OwnerChangeBuffer[guid] = 60;
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
                            tracked.Remove(guid);
                            pendingDrop.Remove(guid);
                            OwnerChanged.Remove(guid);
                            OwnerChangeBuffer.Remove(guid);
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
            float touchRadiusSqr = 1.5f * 1.5f;

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
                        StartCoroutine(DelayedPickupAccepted(p.character.photonView, slot.itemSlotID, 0.2f, idata.guid));
                    }
                    pendingDrop.Remove(idata.guid);
                    yield break;
                }
                yield return null;
            }
        }
        private IEnumerator DelayedPickupAccepted(PhotonView view, byte slotId, float delay, Guid guid)
        {
            yield return new WaitForSecondsRealtime(delay);

            if (!tracked.Contains(guid))
                yield break;

            if (view == null)
                yield break;

            view.RPC("RPCA_Chaos_EquipSlotRPC", view.Owner, slotId);
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

            Vector3 spawnPos = Vector3.zero;
            Transform CharacterHip = owner.character.GetBodypart(BodypartType.Hip).transform;
            Vector3 center = CharacterHip.position;

            Campfire? camp = FindNearestUnlitCampfire(Character.localCharacter.Center);

            if (owner.character == null) return;

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


            if (hasWorldItem)
            {
                TryGetWorldItem(idata.guid, out var pv);
                if (currentItem != null && currentItem.data.guid == idata.guid)
                {
                    owner.character.photonView.RPC("DestroyHeldItemRpc", owner.character.photonView.Owner);
                    owner.character.photonView.RPC("RPCA_Chaos_EquipSlotRPC", owner.character.photonView.Owner, 179);
                }
                if (pv != null)
                {
                    PhotonNetwork.Destroy(pv);
                    ModLogger.Log($"[DynamiteManager] Item detected - Deleting Item.");
                }

            }

            if (IsItemInSlot(owner, slot, idata, shouldExist: true))
            {
                try
                {
                    ModLogger.Log($"[DynamiteManager] Still in slot {slot} - Deleting Item in slot.");
                    owner.EmptySlot(Optionable<byte>.Some (slot.itemSlotID));
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

                        if (CatchDynamiteEffect.availableSlots.TryGetValue(owner.photonView.OwnerActorNr, out var set))
                        {
                            set.Add(slot.itemSlotID);
                        }
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
        }
        public static void RegisterDynamiteItem(Guid guid, PhotonView view)
        {
            if (view == null) return;
            if (!DynamiteItems.TryGetValue(guid, out var views))
            {
                views = new HashSet<PhotonView>();
                DynamiteItems[guid] = views;
            }

            views.Add(view);
        }

        public static void UnregisterDynamiteItem(Guid guid, PhotonView view)
        {
            if (view == null) return;

            if (DynamiteItems.TryGetValue(guid, out var views))
            {
                views.Remove(view);

                if (views.Count == 0)
                {
                    DynamiteItems.Remove(guid);
                }
            }
        }

        public static bool TryGetWorldItem(Guid guid, out PhotonView? view)
        {
            view = null;

            if (!DynamiteItems.TryGetValue(guid, out var views))
                return false;

            PhotonView? dead = null;

            foreach (var v in views)
            {
                if (v == null)
                {
                    dead = v;
                    continue;
                }

                view = v;
                return true;
            }

            if (dead != null)
            {
                views.Remove(dead);
                if (views.Count == 0)
                    DynamiteItems.Remove(guid);
            }

            return false;
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

                if (CatchDynamiteEffect.availableSlots.TryGetValue(character.player.photonView.OwnerActorNr, out var slots))
                    if (slots.Count == 0) continue;

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

            Item currentItem = from.character.data.currentItem;

            if (CatchDynamiteEffect.availableSlots.TryGetValue(from.photonView.OwnerActorNr, out var set))
            {
                set.Add(fromSlot.itemSlotID);
            }

            if (currentItem != null && currentItem.data.guid == idata.guid)
            {
                from.character.photonView.RPC("DestroyHeldItemRpc", from.character.photonView.Owner);
                from.character.photonView.RPC("RPCA_Chaos_EquipSlotRPC", from.character.photonView.Owner, 179);
            }

            if (IsItemInSlot(from, fromSlot, idata, shouldExist: true))
            {
                from.EmptySlot(Optionable<byte>.Some (fromSlot.itemSlotID));
            }

            if (TryGetWorldItem(idata.guid, out var pv) && pv != null)
            {
                PhotonNetwork.Destroy(pv);
            }

            SpecialItems.SetModSpawned(idata, true);
            to.AddItem(ModItemIDs.Dynamite, idata, out var toSlot);

            StartCoroutine(DelayedPickupAccepted(to.character.photonView, toSlot.itemSlotID, 1f, idata.guid));
        }
    }
}
