using UnityEngine;
using UnityEngine.AI;
using System.Collections.Generic;

/// <summary>
/// Runtime NavMesh baker for auto-test scenes.
/// Tries NavMeshSurface from AI Navigation package, falls back to NavMeshBuilder API.
/// Now filters out non-readable meshes to avoid "does not allow read access" warnings.
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
        Debug.Log("[NavMeshAutoBaker] No NavMesh, building via NavMeshBuilder API (readable meshes only)...");
        BuildNavMeshRuntime();
    }

    private void BuildNavMeshRuntime()
    {
        var sources = new List<NavMeshBuildSource>();
        int skippedUnreadable = 0;
        int addedMeshes = 0;
        int addedColliders = 0;

        // Add only readable MeshFilters
        var renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        foreach (var r in renderers)
        {
            if (r == null || !r.gameObject.activeInHierarchy) continue;
            if ((bakeLayers.value & (1 << r.gameObject.layer)) == 0) continue;
            if (r.GetComponentInParent<CharacterController>() != null) continue;
            try { if (r.transform.root.CompareTag("Player")) continue; } catch { /* tag not defined */ }

            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                // CRITICAL FIX: Skip non-readable meshes to avoid warning
                if (!mf.sharedMesh.isReadable)
                {
                    skippedUnreadable++;
                    continue;
                }
                var src = new NavMeshBuildSource();
                src.shape = NavMeshBuildSourceShape.Mesh;
                src.sourceObject = mf.sharedMesh;
                src.transform = mf.transform.localToWorldMatrix;
                src.area = 0;
                sources.Add(src);
                addedMeshes++;
            }
        }

        // Add colliders as box sources for floors/walls - more reliable than meshes
        var colliders = FindObjectsByType<Collider>(FindObjectsSortMode.None);
        foreach (var c in colliders)
        {
            if (c == null || !c.gameObject.activeInHierarchy) continue;
            if ((bakeLayers.value & (1 << c.gameObject.layer)) == 0) continue;
            if (c is MeshCollider) continue; // skip mesh colliders, often non-readable too
            if (c.GetComponentInParent<CharacterController>() != null) continue;
            if (c.isTrigger) continue; // don't bake triggers

            var src = new NavMeshBuildSource();
            src.shape = NavMeshBuildSourceShape.Box;
            src.size = c.bounds.size;
            src.transform = Matrix4x4.TRS(c.bounds.center, c.transform.rotation, Vector3.one);
            src.area = 0;
            sources.Add(src);
            addedColliders++;
        }

        Debug.Log($"[NavMeshAutoBaker] Sources: {addedMeshes} readable meshes, {addedColliders} colliders, skipped {skippedUnreadable} unreadable");

        if (sources.Count == 0)
        {
            Debug.LogWarning("[NavMeshAutoBaker] No valid sources found, creating simple plane NavMesh");
            // Create a simple walkable plane as fallback
            var src = new NavMeshBuildSource();
            src.shape = NavMeshBuildSourceShape.Box;
            src.size = new Vector3(100, 0.1f, 100);
            src.transform = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one);
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
            Debug.Log($"[NavMeshAutoBaker] Runtime NavMesh built: {sources.Count} sources, bounds {bounds.size}, valid={_navMeshInstance.valid}, triangulation vertices={NavMesh.CalculateTriangulation().vertices.Length}");
        }
        else
        {
            Debug.LogWarning("[NavMeshAutoBaker] Failed to build NavMesh via NavMeshBuilder");
        }
    }

    [ContextMenu("Bake Now")] public void BakeNow() => TryBake();
}
