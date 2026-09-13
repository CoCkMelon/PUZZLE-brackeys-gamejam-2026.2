using UnityEngine;
using UnityEngine.AI;
using System.Collections.Generic;

/// <summary>
/// Runtime NavMesh baker for auto-test scenes.
/// Tries NavMeshSurface from AI Navigation package, falls back to NavMeshBuilder API.
/// Attach to empty GameObject in AutoTest scenes. Auto-bakes on Start.
/// </summary>
public class NavMeshAutoBaker : MonoBehaviour
{
    [Header("Bake Settings")]
    [SerializeField] private bool bakeOnStart = true;
    [SerializeField] private bool bakeOnEnable = false;
    [SerializeField] private LayerMask bakeLayers = ~0;
    [SerializeField] private float agentRadius = 0.5f;
    [SerializeField] private float agentHeight = 2f;
    [SerializeField] private float agentClimb = 0.4f;
    [SerializeField] private float agentSlope = 45f;

    private NavMeshData _navMeshData;
    private NavMeshDataInstance _navMeshInstance;
    private bool _baked;

    private void Start() { if (bakeOnStart) TryBake(); }
    private void OnEnable() { if (bakeOnEnable) TryBake(); }
    private void OnDisable() { if (_navMeshInstance.valid) _navMeshInstance.Remove(); }

    public void TryBake()
    {
        if (_baked && NavMesh.CalculateTriangulation().vertices.Length > 100) return;

        // Try NavMeshSurface via reflection first
        var surfaceType = System.Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation");
        if (surfaceType != null)
        {
            var surface = GetComponent(surfaceType) as MonoBehaviour;
            if (surface != null)
            {
                var buildMethod = surfaceType.GetMethod("BuildNavMesh");
                if (buildMethod != null) { buildMethod.Invoke(surface, null); Debug.Log("[NavMeshAutoBaker] Built NavMesh via NavMeshSurface"); _baked = true; return; }
            }
            var surfaces = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var s in surfaces)
            {
                if (s.GetType().FullName == "Unity.AI.Navigation.NavMeshSurface")
                {
                    var buildMethod = s.GetType().GetMethod("BuildNavMesh");
                    buildMethod?.Invoke(s, null);
                    Debug.Log($"[NavMeshAutoBaker] Built NavMesh via found surface on {s.gameObject.name}");
                    _baked = true;
                    return;
                }
            }
        }

        // Fallback: Check if NavMesh exists
        if (NavMesh.CalculateTriangulation().vertices.Length > 100)
        {
            Debug.Log($"[NavMeshAutoBaker] NavMesh exists: {NavMesh.CalculateTriangulation().vertices.Length} vertices");
            _baked = true;
            return;
        }

        // Runtime build via NavMeshBuilder
        Debug.Log("[NavMeshAutoBaker] No NavMesh, building via NavMeshBuilder API...");
        BuildNavMeshRuntime();
    }

    private void BuildNavMeshRuntime()
    {
        var sources = new List<NavMeshBuildSource>();

        var renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        foreach (var r in renderers)
        {
            if (r == null || !r.gameObject.activeInHierarchy) continue;
            if ((bakeLayers.value & (1 << r.gameObject.layer)) == 0) continue;
            if (r.GetComponentInParent<CharacterController>() != null) continue;
            if (r.transform.root.CompareTag("Player")) continue;
            if (r is ParticleSystemRenderer) continue;

            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                var src = new NavMeshBuildSource();
                src.shape = NavMeshBuildSourceShape.Mesh;
                src.sourceObject = mf.sharedMesh;
                src.transform = mf.transform.localToWorldMatrix;
                src.area = 0;
                sources.Add(src);
            }
        }

        var colliders = FindObjectsByType<Collider>(FindObjectsSortMode.None);
        foreach (var c in colliders)
        {
            if (c == null || !c.gameObject.activeInHierarchy) continue;
            if ((bakeLayers.value & (1 << c.gameObject.layer)) == 0) continue;
            if (c is MeshCollider) continue;
            if (c.GetComponentInParent<CharacterController>() != null) continue;
            if (c.isTrigger) continue;

            var src = new NavMeshBuildSource();
            src.shape = NavMeshBuildSourceShape.Box;
            src.size = c.bounds.size;
            src.transform = Matrix4x4.TRS(c.bounds.center, c.transform.rotation, Vector3.one);
            src.area = 0;
            sources.Add(src);
        }

        var settings = NavMesh.GetSettingsByID(0);
        settings.agentRadius = agentRadius;
        settings.agentHeight = agentHeight;
        settings.agentClimb = agentClimb;
        settings.agentSlope = agentSlope;

        var bounds = new Bounds(Vector3.zero, new Vector3(100, 20, 100));
        if (sources.Count > 0)
        {
            bounds = new Bounds(sources[0].transform.GetColumn(3), Vector3.zero);
            foreach (var src in sources)
                bounds.Encapsulate(src.transform.GetColumn(3));
            bounds.Expand(new Vector3(10, 10, 10));
        }

        _navMeshData = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
        if (_navMeshData != null)
        {
            _navMeshInstance = NavMesh.AddNavMeshData(_navMeshData);
            _baked = _navMeshInstance.valid;
            Debug.Log($"[NavMeshAutoBaker] Runtime NavMesh built: {sources.Count} sources, bounds {bounds.size}, valid={_navMeshInstance.valid}, vertices={NavMesh.CalculateTriangulation().vertices.Length}");
        }
        else
        {
            Debug.LogWarning("[NavMeshAutoBaker] Failed to build NavMesh via NavMeshBuilder");
        }
    }

    [ContextMenu("Bake Now")] public void BakeNow() { _baked = false; TryBake(); }
}
