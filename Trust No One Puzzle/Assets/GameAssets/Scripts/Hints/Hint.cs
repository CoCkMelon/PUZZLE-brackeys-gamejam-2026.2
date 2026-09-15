using TMPro;
using UnityEngine;

namespace GameAssets.Scripts.Hints
{
    public class Hint : MonoBehaviour
    {
        [SerializeField] private TMP_Text hintText;

        public string HintText
        {
            get => hintText != null ? hintText.text : "";
            set { if (hintText != null) hintText.text = value; }
        }

        private void Reset()
        {
            if (hintText == null) hintText = GetComponentInChildren<TMP_Text>();
        }
    }
}
