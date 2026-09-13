using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace GameAssets.Scripts.Environment
{
    public class LightFlickerSystem : MonoBehaviour
    {
        [Header("Light Sources")]
        public List<Light> targetLights = new List<Light>();

        [Header("Flicker Settings")]
        [SerializeField] private float minFlickerDuration = 0.05f;
        [SerializeField] private float maxFlickerDuration = 0.2f;
        [SerializeField] private float lightsOutDuration = 6.5f;
        [SerializeField] private bool triggerOnStart = false;

        [Header("Events")]
        public UnityEvent OnLightsWentOut;
        public UnityEvent OnLightsCameBackOn;

        private Coroutine _currentRoutine;
        private bool _areLightsNormallyOn = true;

        private void Start()
        {
            SetLightsState(_areLightsNormallyOn);
            if (triggerOnStart) TriggerRoom2LightsOutSequence();
        }

        public void FlickerOnce()
        {
            if (_currentRoutine == null) _currentRoutine = StartCoroutine(FlickerRoutine(Random.Range(1, 4)));
        }

        public void TriggerRoom2LightsOutSequence()
        {
            if (_currentRoutine != null) StopCoroutine(_currentRoutine);
            _currentRoutine = StartCoroutine(LightsOutSequenceRoutine(lightsOutDuration));
        }

        private IEnumerator FlickerRoutine(int flickers)
        {
            for (int i = 0; i < flickers; i++)
            {
                SetLightsState(false);
                yield return new WaitForSeconds(Random.Range(minFlickerDuration, maxFlickerDuration));
                SetLightsState(true);
                yield return new WaitForSeconds(Random.Range(minFlickerDuration, maxFlickerDuration));
            }
            SetLightsState(_areLightsNormallyOn);
            _currentRoutine = null;
        }

        private IEnumerator LightsOutSequenceRoutine(float durationOut)
        {
            yield return StartCoroutine(FlickerRoutine(3));
            SetLightsState(false);
            OnLightsWentOut?.Invoke();
            yield return new WaitForSeconds(durationOut);
            yield return StartCoroutine(FlickerRoutine(2));
            SetLightsState(true);
            OnLightsCameBackOn?.Invoke();
            _currentRoutine = null;
        }

        private void SetLightsState(bool state)
        {
            foreach (var l in targetLights) { if (l != null) l.enabled = state; }
        }

        public void SetLights(bool state) { _areLightsNormallyOn = state; SetLightsState(state); }
    }
}
