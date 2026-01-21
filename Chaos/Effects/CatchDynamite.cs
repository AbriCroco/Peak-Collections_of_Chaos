using System;
using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;
using Chaos.Utils;
using Chaos.Networking;
using Chaos.Manager;
using Chaos.Patches;

namespace Chaos.Effects
{
    public class CatchDynamiteEffect : IEffect
    {
        public byte Id => EffectIds.CatchDynamite;
        public string IntroMessage => "Is that a dynamite?!";
        public int CountdownSeconds => 3;

        public static readonly List<Player> DynamiteVictims = new();

        public static readonly float FuseTime = 10f;

        public void Apply(string? triggererName = null)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            // select all alive player
            var all = UnityEngine.Object.FindObjectsByType<Character>(FindObjectsSortMode.None);
            var candidates = new List<Character>();
            foreach (var c in all)
            {
                if (c == null) continue;
                if (c.isBot) continue;
                if (c.data.dead) continue;
                if (c.data.fullyPassedOut) continue;
                candidates.Add(c);
            }

            if (candidates.Count == 0)
            {
                ModLogger.Log("[CatchDynamiteEffect] No valid candidates.");
                return;
            }

            var victims = new List<Character>();

            // make a random list of a maximum of 1/2 victims
            int maxVictims = Mathf.Max(1, candidates.Count / 2);
            int victimCount = UnityEngine.Random.Range(1, maxVictims + 1);

            for (int i = 0; i < victimCount; i++)
            {
                int index = UnityEngine.Random.Range(0, candidates.Count);
                victims.Add(candidates[index]);
                candidates.RemoveAt(index);
            }

            if (!ModItemIDs.Initialized)
            {
                ModLogger.Log("[CatchDynamiteEffect] ModItemIDs not initialized yet.");
                return;
            }

            ushort dynamiteID = ModItemIDs.Dynamite;

            // Add Dynamite to each victim
            foreach (var v in victims)
            {
                var player = GetPlayerFromCharacter(v);
                if (player != null)
                {
                    DynamiteVictims.Add(player);

                    var instanceData = new ItemInstanceData(Guid.NewGuid());
                    ItemInstanceDataHandler.AddInstanceData(instanceData);

                    // set a custom fuse (seconds) and mark as lit
                    instanceData.RegisterEntry(DataEntryKey.Fuel, new FloatItemData { Value = FuseTime });
                    instanceData.RegisterEntry(DataEntryKey.FlareActive, new BoolItemData { Value = true });

                    SpecialItems.SetModSpawned(instanceData, true);
                    player.AddItem(ModItemIDs.Dynamite, instanceData, out var slot);
                }
                else
                {
                    ModLogger.Log($"[CatchDynamiteEffect] No Player found for character {v.name}");
                }
            }
        }

        private Player? GetPlayerFromCharacter(Character character)
        {
            foreach (var player in PlayerHandler.GetAllPlayers())
            {
                if (player != null && player.character == character)
                    return player;
            }
            return null;
        }
    }
}
