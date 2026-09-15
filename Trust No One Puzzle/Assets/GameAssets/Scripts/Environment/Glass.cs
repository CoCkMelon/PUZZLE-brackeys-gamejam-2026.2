using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Breakable glass window - improved for GDD.
/// Features:
/// - Glass material (transparent light blue)
/// - Fracture on destruction (spawns shards with physics)
/// - Can be broken by Hammer tag or direct BreakFromHammer()
/// </summary>
public class Glass : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameObject brokenWindow;

    [Header("Settings")]
    [SerializeField] private float breakThreshold = 2f;
    [SerializeField] private string requiredTag = "Hammer";
    [SerializeField] private bool useGlassMaterial = true;
    [SerializeField] private bool spawnFractureOnBreak = true;
    [SerializeField] private int pieces = 50;
    [SerializeField] private float fractureForce = 5f;

    [Header("Audio")]
    [SerializeField] private AudioClip breakSound;

    [Header("Events")]
    public UnityEvent OnBroken;

    private bool _isBroken;
    private Renderer _renderer;

    private void Awake()
    {
        _renderer = GetComponent<Renderer>();
        if (useGlassMaterial)
        {
            EnsureGlassMaterial();
        }
        // Ensure collider
        if (GetComponent<Collider>() == null)
        {
            gameObject.AddComponent<BoxCollider>();
        }
        if (OnBroken == null) OnBroken = new UnityEvent();
        // Ensure tag for finding
        if (gameObject.tag == "Untagged") 
        {
            try { gameObject.tag = "Glass"; } catch { }
        }
    }

    void EnsureGlassMaterial()
    {
        try
        {
            if (_renderer == null) _renderer = GetComponent<Renderer>();
            if (_renderer == null) return;

            // Try to find or create glass material
            Material glassMat = null;
            // Try URP Lit transparent
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            Shader standard = Shader.Find("Standard");
            Shader shader = urpLit != null ? urpLit : standard;

            if (shader != null)
            {
                glassMat = new Material(shader);
                // Transparent setup
                if (urpLit != null)
                {
                    glassMat.SetFloat("_Surface", 1); // Transparent
                    glassMat.SetFloat("_Blend", 0);
                    glassMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    glassMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    glassMat.SetFloat("_ZWrite", 0);
                    glassMat.DisableKeyword("_ALPHATEST_ON");
                    glassMat.EnableKeyword("_ALPHABLEND_ON");
                    glassMat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    glassMat.renderQueue = 3000;
                    glassMat.SetColor("_BaseColor", new Color(0.6f, 0.85f, 1f, 0.3f));
                }
                else
                {
                    // Standard shader transparent
                    glassMat.SetFloat("_Mode", 3); // Transparent
                    glassMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    glassMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    glassMat.SetInt("_ZWrite", 0);
                    glassMat.DisableKeyword("_ALPHATEST_ON");
                    glassMat.EnableKeyword("_ALPHABLEND_ON");
                    glassMat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    glassMat.renderQueue = 3000;
                    glassMat.SetColor("_Color", new Color(0.6f, 0.85f, 1f, 0.3f));
                }
                // Metallic/smoothness for glass look
                if (glassMat.HasProperty("_Metallic")) glassMat.SetFloat("_Metallic", 0.1f);
                if (glassMat.HasProperty("_Smoothness")) glassMat.SetFloat("_Smoothness", 0.9f);
                if (glassMat.HasProperty("_Glossiness")) glassMat.SetFloat("_Glossiness", 0.9f);
            }

            if (glassMat != null)
            {
                _renderer.material = glassMat;
                Debug.Log($"[Glass] Applied glass material to {name} (transparent light blue)");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[Glass] Failed to create glass material: {ex.Message}");
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (_isBroken) return;
        if (!string.IsNullOrEmpty(requiredTag) && collision.gameObject.CompareTag(requiredTag))
        {
            // Also check if held item has hammer tag
            var carry = GameAssets.Scripts.Puzzle.PlayerCarry.Instance;
            if (carry != null && carry.IsCarrying)
            {
                // bool hasHammerTag = true;
                // if (carry.HeldItem != null && carry.HeldItem.gameObject.CompareTag(requiredTag)) hasHammerTag = true;
                // if (carry.HeldInteractable != null && carry.HeldInteractable.gameObject.CompareTag(requiredTag)) hasHammerTag = true;
                // if (collision.gameObject == carry.HeldItem?.gameObject || collision.gameObject == carry.HeldInteractable?.gameObject) hasHammerTag = true;
                // if (!hasHammerTag) return;
            }
            else
            {
                return;
            }
            if (collision.relativeVelocity.magnitude >= breakThreshold)
            {
                BreakGlass(collision.contacts.Length > 0 ? collision.contacts[0].point : transform.position);
            }
        }
    }

    public void BreakGlass(Vector3 impactPoint)
    {
        if (_isBroken) return;
        _isBroken = true;

        Debug.Log($"[Glass] {name} broken at {impactPoint} by Hammer - spawning fracture");

        if (breakSound != null)
            AudioSource.PlayClipAtPoint(breakSound, transform.position);

        if (brokenWindow != null)
        {
            brokenWindow.SetActive(true);
            // Ensure broken window has some visual
            var rend = brokenWindow.GetComponent<Renderer>();
            if (rend != null)
            {
                // Make broken window look like shattered frame
                rend.material.color = new Color(0.3f, 0.3f, 0.3f, 0.5f);
            }
            Debug.Log($"[Glass] Activated brokenWindow {brokenWindow.name}");
        }
        else
        {
            Debug.LogWarning($"[Glass] {name}: brokenWindow not assigned, will spawn fracture only", this);
        }

        if (spawnFractureOnBreak)
        {
            SpawnFracture(impactPoint);
        }

        OnBroken?.Invoke();
        // Delay destroy to let fracture spawn
        Destroy(gameObject, 0.1f);
    }

    void SpawnFracture(Vector3 impactPoint)
    {
        try
        {
            for (int i = 0; i < pieces; i++)
            {
                var shard = GameObject.CreatePrimitive(PrimitiveType.Cube);
                shard.name = $"GlassShard_{i}";
                // Position near impact
                Vector3 randomOffset = Random.insideUnitSphere * 0.3f;
                randomOffset.y = Mathf.Abs(randomOffset.y) * 0.5f;
                shard.transform.position = transform.position + randomOffset;
                shard.transform.localScale = new Vector3(
                    Random.Range(0.05f, 0.2f),
                    Random.Range(0.05f, 0.2f),
                    Random.Range(0.01f, 0.05f)
                );
                shard.transform.rotation = Random.rotation;

                var rend = shard.GetComponent<Renderer>();
                if (rend != null)
                {
                    // Glass shard material - transparent with slight blue
                    var mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                    if (mat.HasProperty("_BaseColor"))
                        mat.SetColor("_BaseColor", new Color(0.7f, 0.9f, 1f, 0.4f));
                    else if (mat.HasProperty("_Color"))
                        mat.SetColor("_Color", new Color(0.7f, 0.9f, 1f, 0.4f));
                    rend.material = mat;
                }

                var rb = shard.AddComponent<Rigidbody>();
                rb.mass = 0.1f;
                rb.linearDamping = 0.3f;
                rb.angularDamping = 0.5f;

                // Explode outward from impact
                Vector3 dir = (shard.transform.position - impactPoint).normalized;
                if (dir == Vector3.zero) dir = Random.onUnitSphere;
                dir.y = Mathf.Abs(dir.y) + 0.2f;
                rb.AddForce(dir * fractureForce + Random.insideUnitSphere * 1f, ForceMode.Impulse);
                rb.AddTorque(Random.insideUnitSphere * 3f, ForceMode.Impulse);

                // Auto destroy after few seconds
                Destroy(shard, 5f);
            }
            Debug.Log($"[Glass] Spawned {pieces} fracture shards");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[Glass] Fracture spawn failed: {ex.Message}");
        }
    }

    // For hammer via PlayerCarry or direct call
    public void BreakFromHammer() => BreakGlass(transform.position);

    // Also allow trigger with hammer
    private void OnTriggerEnter(Collider other)
    {
        if (_isBroken) return;
        if (!string.IsNullOrEmpty(requiredTag) && !other.CompareTag(requiredTag)) return;
        BreakGlass(other.transform.position);
    }
}
