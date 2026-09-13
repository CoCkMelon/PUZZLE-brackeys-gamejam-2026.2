using UnityEngine;
using UnityEngine.Events;

public class Glass : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameObject brokenWindow;

    [Header("Settings")]
    [SerializeField] private float breakThreshold = 5f;
    [SerializeField] private string requiredTag = "Hammer";

    [Header("Audio")]
    [SerializeField] private AudioClip breakSound;

    [Header("Events")]
    public UnityEvent OnBroken;

    private bool _isBroken;

    private void OnCollisionEnter(Collision collision)
    {
        if (_isBroken) return;
        if (!string.IsNullOrEmpty(requiredTag) && !collision.gameObject.CompareTag(requiredTag)) return;
        if (collision.relativeVelocity.magnitude >= breakThreshold)
        {
            BreakGlass(collision.contacts.Length > 0 ? collision.contacts[0].point : transform.position);
        }
    }

    public void BreakGlass(Vector3 impactPoint)
    {
        if (_isBroken) return;
        _isBroken = true;

        if (breakSound != null)
            AudioSource.PlayClipAtPoint(breakSound, transform.position);

        if (brokenWindow != null)
            brokenWindow.SetActive(true);
        else
            Debug.LogWarning($"[Glass] {name}: brokenWindow not assigned", this);

        OnBroken?.Invoke();
        Destroy(gameObject);
    }

    // For hammer via PlayerCarry or direct call
    public void BreakFromHammer() => BreakGlass(transform.position);
}
