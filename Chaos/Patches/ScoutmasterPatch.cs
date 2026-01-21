using System;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using Chaos.Utils;

// Patch the Scoutmaster RPC so host-initiated retargets with forceForTime == 0
// will actually update client instances even when the local setter is blocked by the forced flag.
namespace Chaos.Patches
{
    [HarmonyPatch(typeof(Scoutmaster), "RPCA_SetCurrentTarget")]
    public static class ScoutmasterPatch
    {
        static void Postfix(Scoutmaster __instance, int targetViewID, float forceForTime)
        {
            try
            {
                // Only intervene for retarget/clear calls (host retargets use forceForTime == 0)
                if (Math.Abs(forceForTime) > 0.0001f)
                    return;

                // Resolve the Character intended by this RPC (null if targetViewID == -1)
                Character expected = null!;
                if (targetViewID != -1)
                {
                    var pv = PhotonNetwork.GetPhotonView(targetViewID);
                    if (pv != null)
                        expected = pv.GetComponent<Character>();
                }

                // Debug: log incoming RPC parameters and current local state
                float forcedUntil = 0f;
                var forcedField = AccessTools.Field(typeof(Scoutmaster), "targetForcedUntil");
                if (forcedField != null)
                {
                    var val = forcedField.GetValue(__instance);
                    if (val is float f) forcedUntil = f;
                }

                if (expected == null) return;


                if (Time.time <= forcedUntil)
                {
                    AccessTools.Field(typeof(Scoutmaster), "_currentTarget").SetValue(__instance, expected);
                }
                else
                {
                    ModLogger.Log("[ScoutmasterPatch] Forced window expired or not active, no bypass required.");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Log("[ScoutmasterPatch] Exception in Postfix: " + ex);
            }
        }
    }
}
