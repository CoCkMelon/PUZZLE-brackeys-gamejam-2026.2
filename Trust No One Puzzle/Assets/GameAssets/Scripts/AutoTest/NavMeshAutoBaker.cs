using UnityEngine;
using UnityEngine.AI;
using System.Collections.Generic;

/// <summary>
/// Runtime NavMesh baker for auto-test scenes.
/// Tries NavMeshSurface from AI Navigation package, falls back to NavMeshBuilder API.
/// Attach to empty GameObject in AutoTest scenes.
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

    private void Start() { if (bakeOnStart) TryBake(); }
    private void OnEnable() { if (bakeOnEnable) TryBake(); }
    private void OnDisable() { if (_navMeshInstance.valid) _navMeshInstance.Remove(); }

    public void TryBake()
    {
        // Try NavMeshSurface via reflection first
        var surfaceType = System.Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation");
        if (surfaceType != null)
        {
            var surface = GetComponent(surfaceType) as MonoBehaviour;
            if (surface != null)
            {
                var buildMethod = surfaceType.GetMethod("BuildNavMesh");
                if (buildMethod != null) { buildMethod.Invoke(surface, null); Debug.Log("[NavMeshAutoBaker] Built NavMesh via NavMeshSurface"); return; }
            }
            var surfaces = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var s in surfaces)
            {
                if (s.GetType().FullName == "Unity.AI.Navigation.NavMeshSurface")
                {
                    var buildMethod = s.GetType().GetMethod("BuildNavMesh");
                    buildMethod?.Invoke(s, null);
                    Debug.Log($"[NavMeshAutoBaker] Built NavMesh via found surface on {s.gameObject.name}");
                    return;
                }
            }
        }

        // Fallback: Check if NavMesh exists
        if (NavMesh.CalculateTriangulation().vertices.Length > 0)
        {
            Debug.Log($"[NavMeshAutoBaker] NavMesh exists: {NavMesh.CalculateTriangulation().vertices.Length} vertices");
            return;
        }

        // Runtime build via NavMeshBuilder
        Debug.Log("[NavMeshAutoBaker] No NavMesh, building via NavMeshBuilder API...");
        BuildNavMeshRuntime();
    }

    private void BuildNavMeshRuntime()
    {
        // Collect all MeshRenderers and Terrains in bakeLayers
        var sources = new List<NavMeshBuildSource>();
        var markups = new List<NavMeshBuildMarkup>();

        // Add all active renderers
        var renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        foreach (var r in renderers)
        {
            if (r == null || !r.gameObject.activeInHierarchy) continue;
            if ((bakeLayers.value & (1 << r.gameObject.layer)) == 0) continue;
            // Skip player and small dynamic objects
            if (r.GetComponentInParent<CharacterController>() != null) continue;
            if (r.transform.root.CompareTag("Player")) continue;

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

        // Add colliders as box sources for floors/walls
        var colliders = FindObjectsByType<Collider>(FindObjectsSortMode.None);
        foreach (var c in colliders)
        {
            if (c == null || !c.gameObject.activeInHierarchy) continue;
            if ((bakeLayers.value & (1 << c.gameObject.layer)) == 0) continue;
            if (c is MeshCollider) continue; // already handled via mesh
            if (c.GetComponentInParent<CharacterController>() != null) continue;

            var src = new NavMeshBuildSource();
            src.shape = NavMeshBuildSourceShape.Box;
            src.size = c.bounds.size;
            src.transform = Matrix4x4.TRS(c.bounds.center, c.transform.rotation, Vector3.one);
            src.area = 0;
            sources.Add(src);
        }

        var settings = NavMesh.GetSettingsByID(0);
        if (settings.agentTypeID == 0)
        {
            settings.agentRadius = agentRadius;
            settings.agentHeight = agentHeight;
            settings.agentClimb = agentClimb;
            settings.agentSlope = agentSlope;
        }

        var bounds = new Bounds(Vector3.zero, new Vector3(100, 20, 100));
        // Calculate bounds from sources
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
            Debug.Log($"[NavMeshAutoBaker] Runtime NavMesh built: {sources.Count} sources, bounds {bounds.size}, valid={_navMeshInstance.valid}");
        }
        else
        {
            Debug.LogWarning("[NavMeshAutoBaker] Failed to build NavMesh via NavMeshBuilder");
        }
    }

    [ContextMenu("Bake Now")] public void BakeNow() => TryBake();
}
