using UnityEngine;
using UnityEngine.AI;
using System.Collections.Generic;

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

        if (NavMesh.CalculateTriangulation().vertices.Length > 0)
        {
            Debug.Log($"[NavMeshAutoBaker] NavMesh exists: {NavMesh.CalculateTriangulation().vertices.Length} vertices");
            return;
        }

        Debug.Log("[NavMeshAutoBaker] No NavMesh, building via robust method...");
        BuildNavMeshRuntime();
    }

    private void BuildNavMeshRuntime()
    {
        var sources = new List<NavMeshBuildSource>();
        int added = 0;

        GameObject player = null;
        try { player = GameObject.FindWithTag("Player"); } catch {}
        if (player == null) player = GameObject.Find("Player FPP");
        float groundY = player != null ? player.transform.position.y - 1f : 0f;

        var groundSrc = new NavMeshBuildSource();
        groundSrc.shape = NavMeshBuildSourceShape.Box;
        groundSrc.size = new Vector3(200f, 0.2f, 200f);
        groundSrc.transform = Matrix4x4.TRS(new Vector3(0, groundY, 0), Quaternion.identity, Vector3.one);
        groundSrc.area = 0;
        sources.Add(groundSrc);
        added++;
        Debug.Log($"[NavMeshAutoBaker] Added fallback ground plane at y={groundY} size 200x200");

        var colliders = FindObjectsByType<Collider>(FindObjectsSortMode.None);
        foreach (var c in colliders)
        {
            if (c == null || !c.gameObject.activeInHierarchy) continue;
            if ((bakeLayers.value & (1 << c.gameObject.layer)) == 0) continue;
            if (c.GetComponentInParent<CharacterController>() != null) continue;
            try { if (c.transform.root.CompareTag("Player")) continue; } catch {}
            if (c.isTrigger) continue;

            bool isFlat = c.bounds.size.y < 0.6f && c.bounds.size.x > 0.5f && c.bounds.size.z > 0.5f;
            bool isLargeFlat = c.bounds.size.x > 2f && c.bounds.size.z > 2f && c.bounds.size.y < 1f;
            
            if (!isFlat && !isLargeFlat)
            {
                if (c is BoxCollider box)
                {
                    if (box.size.y > 1f) continue;
                }
                else if (c is MeshCollider) continue;
                else
                {
                    if (c.bounds.size.y > 1f && c.bounds.size.x < 3f) continue;
                }
            }

            var src = new NavMeshBuildSource();
            src.shape = NavMeshBuildSourceShape.Box;
            src.size = c.bounds.size;
            src.transform = Matrix4x4.TRS(c.bounds.center, c.transform.rotation, Vector3.one);
            src.area = 0;
            sources.Add(src);
            added++;
        }

        var meshFilters = FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
        foreach (var mf in meshFilters)
        {
            if (mf == null || !mf.gameObject.activeInHierarchy) continue;
            if (mf.sharedMesh == null) continue;
            if ((bakeLayers.value & (1 << mf.gameObject.layer)) == 0) continue;
            if (mf.GetComponentInParent<CharacterController>() != null) continue;
            try { if (mf.transform.root.CompareTag("Player")) continue; } catch {}

            string nameLower = mf.name.ToLower();
            bool likelyFloor = nameLower.Contains("floor") || nameLower.Contains("ground") || nameLower.Contains("plane") || mf.transform.localScale.y < 0.2f;
            if (!likelyFloor) continue;

            try
            {
                if (!mf.sharedMesh.isReadable) continue;
                var src = new NavMeshBuildSource();
                src.shape = NavMeshBuildSourceShape.Mesh;
                src.sourceObject = mf.sharedMesh;
                src.transform = mf.transform.localToWorldMatrix;
                src.area = 0;
                sources.Add(src);
                added++;
            }
            catch { }
        }

        Debug.Log($"[NavMeshAutoBaker] Sources: {added} (including fallback plane)");

        var settings = NavMesh.GetSettingsByID(0);
        if (settings.agentTypeID == 0)
        {
            settings.agentRadius = agentRadius;
            settings.agentHeight = agentHeight;
            settings.agentClimb = agentClimb;
            settings.agentSlope = agentSlope;
        }

        var bounds = new Bounds(Vector3.zero, new Vector3(200, 30, 200));
        if (sources.Count > 0)
        {
            bounds = new Bounds(sources[0].transform.GetColumn(3), Vector3.zero);
            foreach (var src in sources)
                bounds.Encapsulate(src.transform.GetColumn(3));
            bounds.Expand(new Vector3(20, 20, 20));
        }

        _navMeshData = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
        if (_navMeshData != null)
        {
            _navMeshInstance = NavMesh.AddNavMeshData(_navMeshData);
            int verts = NavMesh.CalculateTriangulation().vertices.Length;
            Debug.Log($"[NavMeshAutoBaker] Runtime NavMesh built: {sources.Count} sources, bounds {bounds.size}, valid={_navMeshInstance.valid}, vertices={verts}");
            if (verts == 0)
            {
                Debug.LogWarning("[NavMeshAutoBaker] Vertices 0 - trying even larger plane fallback");
                sources.Clear();
                var big = new NavMeshBuildSource();
                big.shape = NavMeshBuildSourceShape.Box;
                big.size = new Vector3(500, 0.1f, 500);
                big.transform = Matrix4x4.TRS(new Vector3(0, groundY, 0), Quaternion.identity, Vector3.one);
                big.area = 0;
                sources.Add(big);
                bounds = new Bounds(new Vector3(0, groundY, 0), new Vector3(500, 10, 500));
                _navMeshData = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
                if (_navMeshData != null)
                {
                    if (_navMeshInstance.valid) _navMeshInstance.Remove();
                    _navMeshInstance = NavMesh.AddNavMeshData(_navMeshData);
                    verts = NavMesh.CalculateTriangulation().vertices.Length;
                    Debug.Log($"[NavMeshAutoBaker] Fallback large plane built: valid={_navMeshInstance.valid}, vertices={verts}");
                }
            }
        }
        else
        {
            Debug.LogWarning("[NavMeshAutoBaker] Failed to build NavMesh");
        }
    }

    [ContextMenu("Bake Now")] public void BakeNow() => TryBake();
}
