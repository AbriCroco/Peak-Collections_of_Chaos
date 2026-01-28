using System.Collections;
using Photon.Pun;
using UnityEngine;
using Chaos.Utils;

namespace Chaos.Manager
{
    // Minimal Scoutmaster timer:
    // - One-shot check (after DelayAfterLoadSeconds) on MasterClient.
    // - If "PassportSpawner" exists and is active in the scene, DO NOT run the timer (and stop it if running).
    // - If not found, start the randomized timer.
    // - Re-check on master change; stop everything when losing master.
    public class ScoutmasterTimer : MonoBehaviour
    {
        [Header("Timer")]
        public float MinSeconds = 200f;
        public float MaxSeconds = 800f;
        public bool Repeat = true;

        [Header("Check")]
        public float DelayAfterLoadSeconds = 5f;

        private Coroutine? checkCoroutine;
        private Coroutine? timerCoroutine;

        // Called from RandomEventManager after a gameplay scene loads
        public void PassportCheckAfterLoad()
        {
            if (checkCoroutine != null)
            {
                try { StopCoroutine(checkCoroutine); } catch { }
                checkCoroutine = null;
            }

            checkCoroutine = StartCoroutine(PassportCheckAfterDelay());
        }

        // Called from RandomEventManager when master switches
        public void OnMasterChanged(bool thisClientIsNowMaster)
        { 
            if (!thisClientIsNowMaster)
            {
                StopAllInternal();
                return;
            }

            PassportCheckAfterLoad();
        }

        // Called on leave/new game to fully reset
        public void ResetAll()
        {
            StopAllInternal();
        }

        private void StopAllInternal()
        {
            if (checkCoroutine != null)
            {
                try { StopCoroutine(checkCoroutine); } catch { }
                checkCoroutine = null;
            }

            if (timerCoroutine != null)
            {
                try { StopCoroutine(timerCoroutine); } catch { }
                timerCoroutine = null;
            }
        }

        private IEnumerator PassportCheckAfterDelay()
        {
            float delay = Mathf.Max(0f, DelayAfterLoadSeconds);
            if (delay > 0f) yield return new WaitForSeconds(delay);

            if (!PhotonNetwork.IsMasterClient)
            {
                checkCoroutine = null;
                yield break;
            }

            bool passportSpawnerPresent = FindPassportSpawnerPresent();

            if (passportSpawnerPresent)
            {
                // Stop timer if running
                if (timerCoroutine != null)
                {
                    try { StopCoroutine(timerCoroutine); } catch { }
                    timerCoroutine = null;
                }
                ModLogger.Log("[ScoutmasterTimer] PassportSpawner found — not running timer.");
            }
            else
            {
                if (timerCoroutine == null)
                {
                    StartTimer();
                    ModLogger.Log("[ScoutmasterTimer] Starting timer.");
                }
            }

            checkCoroutine = null;
        }

        private bool FindPassportSpawnerPresent()
        {
            try
            {
                // Fast path: direct lookup by name (active objects only)
                var go = GameObject.Find("PassportSpawner");
                if (go != null && go.activeInHierarchy) return true;
            }
            catch { }
            return false;
        }

        private void StartTimer()
        {
            if (!PhotonNetwork.IsMasterClient) return;
            if (timerCoroutine != null) return;

            timerCoroutine = StartCoroutine(ScoutmasterTimerLoop());
        }

        private IEnumerator ScoutmasterTimerLoop()
        {
            do
            {
                float delay = Mathf.Max(0f, UnityEngine.Random.Range(MinSeconds, MaxSeconds));
                float elapsed = 0f;

                while (elapsed < delay)
                {
                    if (!PhotonNetwork.IsMasterClient)
                    {
                        timerCoroutine = null;
                        yield break;
                    }
                    elapsed += Time.deltaTime;
                    yield return null;
                }

                if (!PhotonNetwork.IsMasterClient) continue;

                RandomEventManager.Instance?.HandleExternalScoutmasterTriggerForTesting();

            } while (Repeat);

            timerCoroutine = null;
        }
    }
}
