using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Photon.Pun;
using Chaos.Utils;

namespace Chaos.Patches
{
    // Vanilla-preserving patch:
    // - When HoldPlayer begins (first frames of lift), owner emits a small shockwave via RPC (affects other nearby players, not the victim).
    // - After RPCA_Throw (vanilla IThrow) finishes, owner applies a tiny assist impulse if the victim barely moved.
    public static class ScoutmasterThrowPatcher
    {
        // Shockwave tuning
        public static float ShockwaveRadius = 0f;       // "a bit bigger" than rescue proximity, adjust to taste
        public static float ShockwaveBaseImpulse = 0f;  // base knockback impulse (attenuated by distance)
        public static float ShockwaveUpwardFactor = 0f;// slight upward push
        public static float ShockwaveMinInterval = 0f; // per-victim min interval to resist spamming if state flaps

        // Solo testing helper
        public static bool AffectVictimWhenSolo = false;  // false in normal play; set true to "feel" the shockwave when testing alone

        // Throw-assist tuning
        public static float PostThrowCheckSeconds = 0.6f;     // observe displacement for this long after throw
        public static float PostThrowMinDisplacement = 1.25f; // treat anything above this as "worked"
        public static float AssistImpulse = 10.0f;            // small extra impulse if needed
        public static float AssistUpwardFactor = 0.30f;       // upward blend similar to vanilla

        static readonly Harmony harmony = new Harmony("com.chaosmod.scoutmasterthrow.shockwave");

        public static void ApplyPatches()
        {
            int patched = 0;
            try
            {
                var scoutType = FindTypeByName("Scoutmaster");
                if (scoutType == null)
                {
                    ModLogger.Log("[ScoutmasterThrowPatcher] Type 'Scoutmaster' not found. Not patching.");
                    return;
                }

                // 1) Hook HoldPlayer (private) to emit shockwave once per grab
                var hold = scoutType.GetMethod("HoldPlayer", BindingFlags.Instance | BindingFlags.NonPublic);
                if (hold != null)
                {
                    var preHold = new HarmonyMethod(typeof(ScoutmasterThrowPatcher).GetMethod(nameof(Prefix_HoldPlayer), BindingFlags.Static | BindingFlags.NonPublic));
                    harmony.Patch(hold, prefix: preHold);
                    patched++;
                }
                else
                {
                    ModLogger.Log("[ScoutmasterThrowPatcher] Scoutmaster.HoldPlayer not found.");
                }

                // 2) Hook RPCA_Throw (PunRPC) to observe and assist weak throws (owner-only)
                var rpcaThrow = scoutType.GetMethod("RPCA_Throw", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (rpcaThrow != null)
                {
                    var preThrow = new HarmonyMethod(typeof(ScoutmasterThrowPatcher).GetMethod(nameof(Prefix_RPCA_Throw), BindingFlags.Static | BindingFlags.NonPublic));
                    harmony.Patch(rpcaThrow, prefix: preThrow);
                    patched++;
                }
                else
                {
                    ModLogger.Log("[ScoutmasterThrowPatcher] Scoutmaster.RPCA_Throw not found.");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Log("[ScoutmasterThrowPatcher] ApplyPatches exception: " + ex);
            }
        }

        // Emit shockwave once per grab, without blocking vanilla.
        static bool Prefix_HoldPlayer(object __instance)
        {
            try
            {
                var comp = __instance as Component;
                if (comp == null) return true;

                var pv = comp.GetComponent<PhotonView>();
                if (pv == null || !pv.IsMine) return true; // owner-only

                var smChar = comp.GetComponent<Character>();
                if (smChar == null) return true;

                // Need current target (victim) and isThrowing state to detect "first hold frames"
                var smType = __instance.GetType();
                var piCurrentTarget = smType.GetProperty("currentTarget", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var fiIsThrowing = smType.GetField("isThrowing", BindingFlags.Instance | BindingFlags.NonPublic);

                if (piCurrentTarget == null || fiIsThrowing == null) return true;

                var victim = piCurrentTarget.GetValue(__instance) as Character;
                if (victim == null) return true;

                bool isThrowing = false;
                try { var v = fiIsThrowing.GetValue(__instance); isThrowing = v is bool b && b; } catch { }

                // Only consider emitting when holding but before IThrow starts (isThrowing == false)
                if (isThrowing) return true;

                // Controller tracks per-victim emissions and cooldown
                var controller = comp.GetComponent<ShockwaveController>() ?? comp.gameObject.AddComponent<ShockwaveController>();
                controller.Init(smChar, pv);

                controller.TryEmitOncePerGrab(victim);

                return true; // never block vanilla
            }
            catch (Exception ex)
            {
                ModLogger.Log("[ScoutmasterThrowPatcher] Hold prefix exception: " + ex);
                return true;
            }
        }

        // Observe vanilla throw and assist (owner-only) without blocking
        static bool Prefix_RPCA_Throw(object __instance)
        {
            try
            {
                var comp = __instance as Component;
                if (comp == null) return true;

                var pv = comp.GetComponent<PhotonView>();
                if (pv == null || !pv.IsMine) return true; // owner-only

                var obs = comp.GetComponent<ThrowAssistObserver>() ?? comp.gameObject.AddComponent<ThrowAssistObserver>();
                obs.Begin(__instance);

                return true; // do not block vanilla RPCA_Throw
            }
            catch (Exception ex)
            {
                ModLogger.Log("[ScoutmasterThrowPatcher] RPCA_Throw prefix exception: " + ex);
                return true;
            }
        }

        static Type? FindTypeByName(string shortName)
        {
            if (string.IsNullOrEmpty(shortName)) return null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types; try { types = a.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    try { if (t != null && t.Name.Equals(shortName, StringComparison.OrdinalIgnoreCase)) return t; }
                    catch { }
                }
            }
            return null;
        }

        // Emits a networked shockwave once per victim (per grab) on lift start.
        sealed class ShockwaveController : MonoBehaviour
        {
            Character? smChar;
            PhotonView? pv;

            // Per-grab state
            int lastVictimViewId = -1;
            float lastEmitTime = -999f;

            public void Init(Character hostChar, PhotonView view)
            {
                smChar = hostChar;
                pv = view;
            }

            public void TryEmitOncePerGrab(Character victim)
            {
                try
                {
                    if (smChar == null || pv == null) return;
                    var vpv = victim.photonView;
                    int vId = (vpv != null) ? vpv.ViewID : -1;

                    // If victim changed, reset cooldown scope
                    if (vId != lastVictimViewId)
                    {
                        lastVictimViewId = vId;
                        lastEmitTime = -999f;
                    }

                    // Respect minimal interval to avoid edge-case spam
                    if (Time.time - lastEmitTime < Mathf.Max(0.05f, ShockwaveMinInterval)) return;

                    lastEmitTime = Time.time;

                    // Broadcast to all clients; helper on same GO will receive
                    pv.RPC(nameof(ShockwaveHelper.RPCA_Chaos_Shockwave),
                           RpcTarget.All,
                           ShockwaveRadius,
                           ShockwaveBaseImpulse,
                           ShockwaveUpwardFactor,
                           vId,
                           AffectVictimWhenSolo);

                    // Ensure helper exists on this GO (for local receive)
                    var helper = pv.GetComponent<ShockwaveHelper>() ?? pv.gameObject.AddComponent<ShockwaveHelper>();
                    helper.Bind(smChar);
                }
                catch (Exception ex)
                {
                    ModLogger.Log("[ScoutmasterThrowPatcher] TryEmitOncePerGrab exception: " + ex);
                }
            }
        }

        // Executes the shockwave on all clients when invoked via RPC.
        sealed class ShockwaveHelper : MonoBehaviour
        {
            Character? smChar;

            public void Bind(Character hostChar) => smChar = hostChar;

            [PunRPC]
            public void RPCA_Chaos_Shockwave(float radius, float baseImpulse, float upFactor, int victimViewId, bool affectVictimWhenSolo)
            {
                try
                {
                    // Resolve host Character if not bound yet (remote clients)
                    if (smChar == null)
                    {
                        smChar = GetComponent<Character>();
                    }
                    if (smChar == null) return;

                    Vector3 center = smChar.Center;

                    // Count alive players
                    int aliveCount = 0;
                    var all = Character.AllCharacters;
                    if (all == null) return;

                    foreach (var p in all)
                    {
                        if (p == null || p.isBot || p.data == null || p.data.dead || p.data.fullyPassedOut) continue;
                        aliveCount++;
                    }

                    foreach (var p in all)
                    {
                        if (p == null || p.data == null) continue;
                        if (p.isBot || p.data.dead || p.data.fullyPassedOut) continue;

                        var pView = p.photonView;
                        int pId = (pView != null) ? pView.ViewID : -2;

                        bool isVictim = (pId == victimViewId);
                        if (isVictim && !(affectVictimWhenSolo && aliveCount <= 1))
                            continue; // skip victim (unless solo-test mode)

                        float dist = Vector3.Distance(center, p.Center);
                        if (dist > radius) continue;

                        Vector3 dir = (p.Center - center);
                        dir.y = 0f;
                        if (dir.sqrMagnitude < 0.0001f) dir = UnityEngine.Random.insideUnitSphere; // edge case overlap
                        dir.Normalize();

                        float att = Mathf.Clamp01(1f - (dist / Mathf.Max(0.01f, radius)));
                        Vector3 impulse = dir * (baseImpulse * att) + Vector3.up * (baseImpulse * upFactor * att);

                        // Apply to the best available Rigidbody
                        Rigidbody? rb = null;
                        try { rb = p.GetComponentInChildren<Rigidbody>(); } catch { }
                        if (rb == null) try { rb = p.GetComponent<Rigidbody>(); } catch { }

                        if (rb != null)
                        {
                            try { rb.AddForce(impulse, ForceMode.Impulse); } catch { }
                        }

                        // Light “unstick” nudge
                        try { p.data.sinceGrounded = 0f; } catch { }
                    }
                }
                catch (Exception ex)
                {
                    ModLogger.Log("[ScoutmasterThrowPatcher] RPCA_Chaos_Shockwave exception: " + ex);
                }
            }
        }

        // Observes vanilla throw and assists only if throw displacement was too small.
        sealed class ThrowAssistObserver : MonoBehaviour
        {
            bool running = false;

            public void Begin(object scoutInst)
            {
                if (running || scoutInst == null) return;
                running = true;
                StartCoroutine(ObserveAndAssist(scoutInst));
            }

            IEnumerator ObserveAndAssist(object scoutInst)
            {
                try
                {
                    var comp = scoutInst as Component;
                    var smChar = (comp != null) ? comp.GetComponent<Character>() : null;
                    var tf = (comp != null) ? comp.transform : null;
                    var smType = scoutInst.GetType();

                    var fiIsThrowing = smType.GetField("isThrowing", BindingFlags.Instance | BindingFlags.NonPublic);
                    var piCurrentTarget = smType.GetProperty("currentTarget", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    FieldInfo? fiLookDirection = null;

                    if (smChar?.data != null)
                    {
                        var dataType = smChar.data.GetType();
                        fiLookDirection = dataType.GetField("lookDirection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }

                    // Resolve victim safely
                    Character? victim = null;
                    try { victim = piCurrentTarget?.GetValue(scoutInst) as Character; } catch { }

                    // Wait until isThrowing becomes true
                    float up = 2.0f;
                    while (up > 0f && !GetBoolSafe(fiIsThrowing, scoutInst))
                    {
                        up -= Time.deltaTime;
                        yield return null;
                    }

                    // Record pre-throw position
                    Vector3 before = (victim != null) ? SafePos(victim.transform) : Vector3.zero;

                    // Wait window to observe displacement
                    float elapsed = 0f;
                    float win = Mathf.Max(0.05f, PostThrowCheckSeconds);
                    Vector3 after = before;

                    while (elapsed < win)
                    {
                        elapsed += Time.deltaTime;
                        if (victim == null) break;
                        after = SafePos(victim.transform);
                        yield return null;
                    }

                    if (victim == null) yield break;

                    float moved = Vector3.Distance(before, after);
                    if (moved >= Mathf.Max(0.05f, PostThrowMinDisplacement)) yield break;

                    // Assist: small impulse in vanilla throw direction (−lookDirection with slight up)
                    Vector3 dir = Vector3.forward;
                    try
                    {
                        if (smChar?.data != null && fiLookDirection != null)
                        {
                            var ld = (Vector3)fiLookDirection.GetValue(smChar.data);
                            dir = -ld; dir.y = 0f;
                            if (dir.sqrMagnitude < 0.0001f) dir = (tf != null ? -tf.forward : Vector3.back);
                            dir.Normalize();
                            dir.y = AssistUpwardFactor; dir.Normalize();
                        }
                        else
                        {
                            dir = (tf != null ? -tf.forward : Vector3.back); dir.y = AssistUpwardFactor; dir.Normalize();
                        }
                    }
                    catch { dir = Vector3.back; }

                    Rigidbody? rb = null;
                    try { rb = victim.GetComponentInChildren<Rigidbody>(); } catch { }
                    if (rb == null) try { rb = victim.GetComponent<Rigidbody>(); } catch { }

                    if (rb != null)
                    {
                        try
                        {
                            rb.AddForce(dir * Mathf.Max(1f, AssistImpulse), ForceMode.Impulse);
                        }
                        catch { }
                    }
                }
                finally
                {
                    running = false;
                }
            }

            static bool GetBoolSafe(FieldInfo? fi, object inst)
            {
                try { var v = fi?.GetValue(inst); return v is bool b && b; } catch { return false; }
            }

            static Vector3 SafePos(Transform t)
            {
                try { return t.position; } catch { return Vector3.zero; }
            }
        }
    }
}
