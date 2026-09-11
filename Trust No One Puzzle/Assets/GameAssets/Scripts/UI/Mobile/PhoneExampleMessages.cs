using System.Collections.Generic;
using UnityEngine;
using GameAssets.Scripts.Puzzle;

namespace GameAssets.Scripts.UI.Mobile
{
    /// <summary>
    /// 20 example messages that could come to the phone (UI Toolkit messenger).
    /// Use via WrongHintSystem (id lookup) or directly via PuzzleEvents.HintRequested.
    /// Some are true hints, some are deliberately misleading ("Trust No One").
    /// </summary>
    [CreateAssetMenu(fileName = "PhoneExampleMessages", menuName = "Puzzle/Phone Example Messages")]
    public class PhoneExampleMessages : ScriptableObject
    {
        public List<WrongHintSystem.HintEntry> examples = new List<WrongHintSystem.HintEntry>
        {
            new WrongHintSystem.HintEntry { id = "msg-01-wrong-note", text = "Don't trust the note in the top drawer. It's lying to you.", isMisleading = true },
            new WrongHintSystem.HintEntry { id = "msg-02-key-under-plant", text = "The silver key is under the potted plant in the corner. Check again, you missed it.", isMisleading = false, triggerOnDrawerId = "plant-drawer" },
            new WrongHintSystem.HintEntry { id = "msg-03-red-crate-wrong", text = "I saw someone move the red crate to the left slot. That's wrong, put it right.", isMisleading = true, triggerOnWrongSlotId = "slot-red-left" },
            new WrongHintSystem.HintEntry { id = "msg-04-mirror-truth", text = "The mirror shows the truth, but only when the lights are off. Look at the reflection.", isMisleading = false },
            new WrongHintSystem.HintEntry { id = "msg-05-blue-book", text = "Blue book goes in the top drawer, not bottom. Trust me.", isMisleading = true, triggerOnCorrectSlotId = "slot-blue-bottom" },
            new WrongHintSystem.HintEntry { id = "msg-06-toolbox-key", text = "Toolbox needs the rusty silver key, not the gold one. Gold is for the desk.", isMisleading = false },
            new WrongHintSystem.HintEntry { id = "msg-07-code", text = "He said code is 3-1-4, but I swear it's 1-4-3. Don't listen to him.", isMisleading = true },
            new WrongHintSystem.HintEntry { id = "msg-08-behind-mirror", text = "Check behind the mirror. There's a hidden compartment with a note.", isMisleading = false, triggerOnDrawerId = "mirror-secret" },
            new WrongHintSystem.HintEntry { id = "msg-09-third-drawer-trap", text = "Don't open the third drawer. It's trapped and will reset everything.", isMisleading = true },
            new WrongHintSystem.HintEntry { id = "msg-10-ghost-not-real", text = "The ghost in the mirror is not real. It's just light + dust. Ignore it.", isMisleading = false },
            new WrongHintSystem.HintEntry { id = "msg-11-hammer-kitchen", text = "I left the hammer in the kitchen, under the sink. Use it to open the crate.", isMisleading = false },
            new WrongHintSystem.HintEntry { id = "msg-12-placement-green-red-blue", text = "Correct placement is: green left, red right, blue middle. Do it now.", isMisleading = true, triggerOnWrongSlotId = "slot-green-left" },
            new WrongHintSystem.HintEntry { id = "msg-13-code-changed", text = "They changed the code. New code is 7-2-9. Old one won't work.", isMisleading = true },
            new WrongHintSystem.HintEntry { id = "msg-14-trust-no-one", text = "Remember: Trust No One. Even me. Especially me.", isMisleading = false },
            new WrongHintSystem.HintEntry { id = "msg-15-phone-bugged", text = "Your phone is bugged. Don't answer unknown numbers. They're listening.", isMisleading = true },
            new WrongHintSystem.HintEntry { id = "msg-16-reflection-key", text = "Look at the reflection. The real key is on the other side of the room.", isMisleading = false },
            new WrongHintSystem.HintEntry { id = "msg-17-scratch-drawer", text = "The drawer with the scratch opens with the rusty key. Not the shiny one.", isMisleading = false, triggerOnDrawerId = "scratch-drawer" },
            new WrongHintSystem.HintEntry { id = "msg-18-note-under-rug", text = "I hid the spare note under the rug near the door. Don't tell anyone.", isMisleading = false },
            new WrongHintSystem.HintEntry { id = "msg-19-generic-wrong", text = "That slot is wrong. Try the other one. You're messing it up.", isMisleading = true },
            new WrongHintSystem.HintEntry { id = "msg-20-almost-done", text = "You are close. One more correct placement and the desk drawer unlocks.", isMisleading = false, triggerOnCorrectSlotId = "slot-near-complete" },
        };

        // Helper to send all as custom messages for quick testing
        public void SendAllAsCustom()
        {
            foreach (var e in examples)
            {
                if (WrongHintSystem.Instance != null)
                    WrongHintSystem.Instance.SendCustom(e.text, e.isMisleading, e.id);
                else
                    PuzzleEvents.RaiseHint(new HintMessage { text = e.text, isMisleading = e.isMisleading, sourceId = e.id });
            }
        }
    }

    /// <summary>
    /// Simple tester: drops this on any GameObject, assigns the ScriptableObject, and it will
    /// send example messages via triggers or on start for testing the UI Toolkit phone.
    /// </summary>
    public class PhoneExampleMessagesTester : MonoBehaviour
    {
        [SerializeField] private PhoneExampleMessages database;
        [SerializeField] private bool sendOnStart;
        [SerializeField] private float delayBetweenMessages = 1.5f;
        [SerializeField] private bool onlyMisleading;
        [SerializeField] private bool onlyTrue;

        private void Start()
        {
            if (sendOnStart) Invoke(nameof(SendSequence), 1f);
        }

        [ContextMenu("Send All Now")]
        public void SendAllNow()
        {
            if (database == null) { Debug.LogWarning("No database assigned"); return; }
            foreach (var e in database.examples)
            {
                if (onlyMisleading && !e.isMisleading) continue;
                if (onlyTrue && e.isMisleading) continue;
                PuzzleEvents.RaiseHint(new HintMessage { text = e.text, isMisleading = e.isMisleading, sourceId = e.id });
            }
        }

        public void SendSequence()
        {
            if (database == null) return;
            StartCoroutine(SendSequenceCo());
        }

        private System.Collections.IEnumerator SendSequenceCo()
        {
            foreach (var e in database.examples)
            {
                if (onlyMisleading && !e.isMisleading) continue;
                if (onlyTrue && e.isMisleading) continue;
                PuzzleEvents.RaiseHint(new HintMessage { text = e.text, isMisleading = e.isMisleading, sourceId = e.id });
                yield return new WaitForSeconds(delayBetweenMessages);
            }
        }

        // For UnityEvent wiring
        public void SendRandom()
        {
            if (database == null || database.examples.Count == 0) return;
            var e = database.examples[Random.Range(0, database.examples.Count)];
            PuzzleEvents.RaiseHint(new HintMessage { text = e.text, isMisleading = e.isMisleading, sourceId = e.id });
        }

        public void SendById(string id)
        {
            if (database == null) return;
            var e = database.examples.Find(x => x.id == id);
            if (e != null) PuzzleEvents.RaiseHint(new HintMessage { text = e.text, isMisleading = e.isMisleading, sourceId = e.id });
        }
    }
}
