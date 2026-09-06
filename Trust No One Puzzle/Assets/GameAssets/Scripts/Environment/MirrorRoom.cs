using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public sealed class MirrorRoom : MonoBehaviour
{
    public enum MirrorAxis
    {
        LocalX,
        LocalY,
        LocalZ
    }

    [Header("Selection")]
    [Tooltip("Only visual components on these layers can be selected.")]
    [SerializeField] private LayerMask includedLayers = -1;

    [Tooltip("Inactive source objects are normally ignored. Enable this if inactive branches should be cloned too.")]
    [SerializeField] private bool includeInactiveSources = false;

    [Header("Reflection")]
    [Tooltip("For a default Unity Quad whose surface is local XY, use Local Z.")]
    [SerializeField] private MirrorAxis mirrorAxis = MirrorAxis.LocalZ;

    [Tooltip("Checks bounds overlap, hierarchy membership, and source transforms every LateUpdate.")]
    [SerializeField] private bool updateEveryFrame = true;

    private BoxCollider selectionBox;
    private Transform mirrorPivot;

    // Every required source transform maps to exactly one generated clone transform.
    private readonly Dictionary<Transform, Transform> sourceToClone =
        new Dictionary<Transform, Transform>();

    // Source visual component instance ID -> generated visual component.
    private readonly Dictionary<int, Component> componentClones =
        new Dictionary<int, Component>();

    // Ordered ancestor-first, so parents are synchronized before children.
    private readonly List<Transform> orderedSources =
        new List<Transform>();

    // Snapshot of the visual-component membership used by the current clone hierarchy.
    private Dictionary<Transform, NodeSelection> builtSelection =
        new Dictionary<Transform, NodeSelection>();

    private bool hasBuilt;

    private sealed class NodeSelection
    {
        public readonly Transform source;
        public readonly List<Component> components = new List<Component>();

        public NodeSelection(Transform sourceTransform)
        {
            source = sourceTransform;
        }

        public void Add(Component component)
        {
            if (component == null)
                return;

            if (!components.Contains(component))
                components.Add(component);
        }

        public bool Contains(Component component)
        {
            return components.Contains(component);
        }
    }

    /*
     * Represents the BoxCollider after its complete localToWorldMatrix is applied.
     *
     * For ordinary transforms this is an oriented box.
     * If a parent hierarchy introduces shear, this is an affine parallelepiped.
     * The SAT overlap test below supports both without reading lossyScale.
     */
    private struct WorldBox
    {
        public Vector3 center;
        public Vector3 halfX;
        public Vector3 halfY;
        public Vector3 halfZ;
        public bool valid;

        public static WorldBox FromBoxCollider(BoxCollider box)
        {
            WorldBox result = new WorldBox();

            if (box == null)
                return result;

            Vector3 size = box.size;

            if (size.x <= 0f || size.y <= 0f || size.z <= 0f)
                return result;

            Matrix4x4 matrix = box.transform.localToWorldMatrix;

            result.center = matrix.MultiplyPoint3x4(box.center);
            result.halfX = matrix.MultiplyVector(Vector3.right * (size.x * 0.5f));
            result.halfY = matrix.MultiplyVector(Vector3.up * (size.y * 0.5f));
            result.halfZ = matrix.MultiplyVector(Vector3.forward * (size.z * 0.5f));

            result.valid =
                result.halfX.sqrMagnitude > 0.000000000001f &&
                result.halfY.sqrMagnitude > 0.000000000001f &&
                result.halfZ.sqrMagnitude > 0.000000000001f;

            return result;
        }

        // Exact closest point for an affine box/parallelepiped.
        public Vector3 ClosestPoint(Vector3 point)
        {
            if (!valid)
                return center;

            float bestDistance = float.PositiveInfinity;
            Vector3 bestPoint = center;

            // Each coordinate has three possible states:
            // 0 = fixed at -1, 1 = free, 2 = fixed at +1.
            for (int state = 0; state < 27; state++)
            {
                int stateX = state % 3;
                int stateY = (state / 3) % 3;
                int stateZ = state / 9;

                bool freeX = stateX == 1;
                bool freeY = stateY == 1;
                bool freeZ = stateZ == 1;

                float x = stateX == 0 ? -1f : stateX == 2 ? 1f : 0f;
                float y = stateY == 0 ? -1f : stateY == 2 ? 1f : 0f;
                float z = stateZ == 0 ? -1f : stateZ == 2 ? 1f : 0f;

                Vector3 residual = point - center;

                if (!freeX)
                    residual -= halfX * x;

                if (!freeY)
                    residual -= halfY * y;

                if (!freeZ)
                    residual -= halfZ * z;

                int freeCount = 0;
                int first = -1;
                int second = -1;
                int third = -1;

                if (freeX)
                {
                    first = 0;
                    freeCount++;
                }

                if (freeY)
                {
                    if (first < 0)
                        first = 1;
                    else
                        second = 1;

                    freeCount++;
                }

                if (freeZ)
                {
                    if (first < 0)
                        first = 2;
                    else if (second < 0)
                        second = 2;
                    else
                        third = 2;

                    freeCount++;
                }

                bool solved = true;

                if (freeCount == 1)
                {
                    Vector3 edge = GetEdge(first);
                    float denominator = Vector3.Dot(edge, edge);

                    if (denominator < 0.000000000001f)
                    {
                        solved = false;
                    }
                    else
                    {
                        SetCoefficient(ref x, ref y, ref z, first,
                            Vector3.Dot(edge, residual) / denominator);
                    }
                }
                else if (freeCount == 2)
                {
                    Vector3 a = GetEdge(first);
                    Vector3 b = GetEdge(second);

                    float aa = Vector3.Dot(a, a);
                    float ab = Vector3.Dot(a, b);
                    float bb = Vector3.Dot(b, b);
                    float ar = Vector3.Dot(a, residual);
                    float br = Vector3.Dot(b, residual);

                    float determinant = aa * bb - ab * ab;

                    if (Mathf.Abs(determinant) < 0.000000000001f)
                    {
                        solved = false;
                    }
                    else
                    {
                        float valueA = (ar * bb - ab * br) / determinant;
                        float valueB = (aa * br - ab * ar) / determinant;

                        SetCoefficient(ref x, ref y, ref z, first, valueA);
                        SetCoefficient(ref x, ref y, ref z, second, valueB);
                    }
                }
                else if (freeCount == 3)
                {
                    Vector3 a = GetEdge(first);
                    Vector3 b = GetEdge(second);
                    Vector3 c = GetEdge(third);

                    float determinant = Vector3.Dot(a, Vector3.Cross(b, c));

                    if (Mathf.Abs(determinant) < 0.000000000001f)
                    {
                        solved = false;
                    }
                    else
                    {
                        float valueA = Vector3.Dot(residual, Vector3.Cross(b, c)) / determinant;
                        float valueB = Vector3.Dot(a, Vector3.Cross(residual, c)) / determinant;
                        float valueC = Vector3.Dot(a, Vector3.Cross(b, residual)) / determinant;

                        SetCoefficient(ref x, ref y, ref z, first, valueA);
                        SetCoefficient(ref x, ref y, ref z, second, valueB);
                        SetCoefficient(ref x, ref y, ref z, third, valueC);
                    }
                }

                if (!solved)
                    continue;

                const float tolerance = 1.00001f;

                if (x < -tolerance || x > tolerance ||
                    y < -tolerance || y > tolerance ||
                    z < -tolerance || z > tolerance)
                {
                    continue;
                }

                Vector3 candidate = center + halfX * x + halfY * y + halfZ * z;
                float distance = (candidate - point).sqrMagnitude;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestPoint = candidate;
                }
            }

            return bestPoint;
        }

        private Vector3 GetEdge(int index)
        {
            switch (index)
            {
                case 0:
                    return halfX;

                case 1:
                    return halfY;

                default:
                    return halfZ;
            }
        }

        private static void SetCoefficient(
            ref float x,
            ref float y,
            ref float z,
            int index,
            float value)
        {
            switch (index)
            {
                case 0:
                    x = value;
                    break;

                case 1:
                    y = value;
                    break;

                default:
                    z = value;
                    break;
            }
        }
    }

    private void Awake()
    {
        selectionBox = GetComponent<BoxCollider>();
    }

    private void OnEnable()
    {
        if (selectionBox == null)
            selectionBox = GetComponent<BoxCollider>();

        RebuildMirror();
    }

    private void OnDisable()
    {
        // Keep the generated hierarchy from rendering when this component is disabled.
        if (mirrorPivot != null)
            mirrorPivot.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        DestroyGeneratedPivot();
    }

    private void LateUpdate()
    {
        if (!updateEveryFrame)
            return;

        if (selectionBox == null)
            selectionBox = GetComponent<BoxCollider>();

        if (selectionBox == null)
            return;

        Dictionary<Transform, NodeSelection> currentSelection = ScanSelectedVisuals();
        HashSet<Transform> requiredTransforms = BuildRequiredTransformSet(currentSelection);

        if (!hasBuilt || PlanHasChanged(currentSelection, requiredTransforms))
        {
            BuildMirror(currentSelection, requiredTransforms);
            return;
        }

        SynchronizeExistingHierarchy();
    }

    [ContextMenu("Rebuild Mirror")]
    public void RebuildMirror()
    {
        if (selectionBox == null)
            selectionBox = GetComponent<BoxCollider>();

        if (selectionBox == null)
            return;

        Dictionary<Transform, NodeSelection> selected = ScanSelectedVisuals();
        HashSet<Transform> required = BuildRequiredTransformSet(selected);

        BuildMirror(selected, required);
    }

    private Dictionary<Transform, NodeSelection> ScanSelectedVisuals()
    {
        Dictionary<Transform, NodeSelection> result =
            new Dictionary<Transform, NodeSelection>();

        if (selectionBox == null)
            return result;

        Scene sourceScene = gameObject.scene;

        if (!sourceScene.IsValid() || !sourceScene.isLoaded)
            return result;

        WorldBox volume = WorldBox.FromBoxCollider(selectionBox);

        if (!volume.valid)
            return result;

        GameObject[] sceneRoots = sourceScene.GetRootGameObjects();

        for (int rootIndex = 0; rootIndex < sceneRoots.Length; rootIndex++)
        {
            GameObject root = sceneRoots[rootIndex];

            MeshRenderer[] meshRenderers =
                root.GetComponentsInChildren<MeshRenderer>(includeInactiveSources);

            for (int i = 0; i < meshRenderers.Length; i++)
            {
                MeshRenderer renderer = meshRenderers[i];

                if (IsValidVisualCandidate(renderer) &&
                    BoundsOverlapWorldBox(renderer.bounds, volume))
                {
                    AddSelectedComponent(result, renderer);
                }
            }

            SkinnedMeshRenderer[] skinnedRenderers =
                root.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactiveSources);

            for (int i = 0; i < skinnedRenderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = skinnedRenderers[i];

                if (IsValidVisualCandidate(renderer) &&
                    BoundsOverlapWorldBox(renderer.bounds, volume))
                {
                    AddSelectedComponent(result, renderer);
                }
            }

            SpriteRenderer[] spriteRenderers =
                root.GetComponentsInChildren<SpriteRenderer>(includeInactiveSources);

            for (int i = 0; i < spriteRenderers.Length; i++)
            {
                SpriteRenderer renderer = spriteRenderers[i];

                if (IsValidVisualCandidate(renderer) &&
                    BoundsOverlapWorldBox(renderer.bounds, volume))
                {
                    AddSelectedComponent(result, renderer);
                }
            }

            Light[] lights = root.GetComponentsInChildren<Light>(includeInactiveSources);

            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];

                if (IsValidVisualCandidate(light) &&
                    LightOverlapsWorldBox(light, volume))
                {
                    AddSelectedComponent(result, light);
                }
            }
        }

        return result;
    }

    private bool IsValidVisualCandidate(Component component)
    {
        if (component == null)
            return false;

        Transform candidateTransform = component.transform;

        if (candidateTransform == null)
            return false;

        // Do not mirror the Quad that owns this script.
        if (candidateTransform == transform)
            return false;

        // Do not scan generated output. This prevents self-recursion.
        if (mirrorPivot != null &&
            (candidateTransform == mirrorPivot || candidateTransform.IsChildOf(mirrorPivot)))
        {
            return false;
        }

        if (!includeInactiveSources && !candidateTransform.gameObject.activeInHierarchy)
            return false;

        int layerBit = 1 << candidateTransform.gameObject.layer;

        if ((includedLayers.value & layerBit) == 0)
            return false;

        return true;
    }

    private static void AddSelectedComponent(
        Dictionary<Transform, NodeSelection> selection,
        Component component)
    {
        Transform sourceTransform = component.transform;

        NodeSelection node;

        if (!selection.TryGetValue(sourceTransform, out node))
        {
            node = new NodeSelection(sourceTransform);
            selection.Add(sourceTransform, node);
        }

        node.Add(component);
    }

    private static HashSet<Transform> BuildRequiredTransformSet(
        Dictionary<Transform, NodeSelection> selected)
    {
        HashSet<Transform> required = new HashSet<Transform>();

        foreach (KeyValuePair<Transform, NodeSelection> pair in selected)
        {
            Transform current = pair.Key;

            while (current != null)
            {
                required.Add(current);
                current = current.parent;
            }
        }

        return required;
    }

    private bool PlanHasChanged(
        Dictionary<Transform, NodeSelection> currentSelection,
        HashSet<Transform> requiredTransforms)
    {
        if (mirrorPivot == null)
            return true;

        if (sourceToClone.Count != requiredTransforms.Count)
            return true;

        foreach (Transform source in requiredTransforms)
        {
            Transform clone;

            if (source == null ||
                !sourceToClone.TryGetValue(source, out clone) ||
                clone == null)
            {
                return true;
            }

            Transform expectedParent;

            if (source.parent == null)
            {
                expectedParent = mirrorPivot;
            }
            else
            {
                if (!sourceToClone.TryGetValue(source.parent, out expectedParent))
                    return true;
            }

            if (clone.parent != expectedParent)
                return true;
        }

        if (!SelectionsMatch(currentSelection, builtSelection))
            return true;

        foreach (KeyValuePair<Transform, NodeSelection> pair in currentSelection)
        {
            List<Component> components = pair.Value.components;

            for (int i = 0; i < components.Count; i++)
            {
                Component sourceComponent = components[i];

                if (sourceComponent == null)
                    return true;

                Component cloneComponent;

                if (!componentClones.TryGetValue(
                        sourceComponent.GetInstanceID(),
                        out cloneComponent) ||
                    cloneComponent == null)
                {
                    return true;
                }

                MeshRenderer sourceMeshRenderer = sourceComponent as MeshRenderer;

                if (sourceMeshRenderer != null)
                {
                    MeshRenderer cloneMeshRenderer = cloneComponent as MeshRenderer;

                    if (cloneMeshRenderer == null)
                        return true;

                    bool sourceHasMeshFilter =
                        sourceMeshRenderer.GetComponent<MeshFilter>() != null;

                    bool cloneHasMeshFilter =
                        cloneMeshRenderer.GetComponent<MeshFilter>() != null;

                    if (sourceHasMeshFilter != cloneHasMeshFilter)
                        return true;
                }
            }
        }

        return false;
    }

    private static bool SelectionsMatch(
        Dictionary<Transform, NodeSelection> a,
        Dictionary<Transform, NodeSelection> b)
    {
        if (a.Count != b.Count)
            return false;

        foreach (KeyValuePair<Transform, NodeSelection> pair in a)
        {
            NodeSelection other;

            if (!b.TryGetValue(pair.Key, out other))
                return false;

            if (pair.Value.components.Count != other.components.Count)
                return false;

            for (int i = 0; i < pair.Value.components.Count; i++)
            {
                if (!other.Contains(pair.Value.components[i]))
                    return false;
            }
        }

        return true;
    }

    private void BuildMirror(
        Dictionary<Transform, NodeSelection> selected,
        HashSet<Transform> required)
    {
        EnsureMirrorPivot();
        PrepareNeutralPivot();

        ClearGeneratedChildren();

        sourceToClone.Clear();
        componentClones.Clear();
        orderedSources.Clear();

        foreach (Transform source in required)
            orderedSources.Add(source);

        orderedSources.Sort(CompareTransformDepth);

        // First pass: construct only the transform union.
        for (int i = 0; i < orderedSources.Count; i++)
        {
            Transform source = orderedSources[i];

            if (source == null)
                continue;

            GameObject cloneObject = new GameObject(source.name);
            MoveToMirrorScene(cloneObject);

            Transform clone = cloneObject.transform;
            cloneObject.layer = source.gameObject.layer;

            sourceToClone.Add(source, clone);

            Transform sourceParent = source.parent;
            Transform cloneParent;

            if (sourceParent != null &&
                sourceToClone.TryGetValue(sourceParent, out cloneParent))
            {
                // Parent is already cloned. Copy exact source local TRS.
                clone.SetParent(cloneParent, false);
                CopyLocalTransform(source, clone);
            }
            else
            {
                /*
                 * This is a source scene root.
                 *
                 * The pivot is neutral here. Set the root clone to source world
                 * position/rotation/scale, then parent with worldPositionStays=true.
                 */
                clone.position = source.position;
                clone.rotation = source.rotation;
                clone.localScale = source.localScale;
                clone.SetParent(mirrorPivot, true);
            }
        }

        // Second pass: add only selected visual components.
        foreach (KeyValuePair<Transform, NodeSelection> pair in selected)
        {
            Transform cloneTransform;

            if (!sourceToClone.TryGetValue(pair.Key, out cloneTransform))
                continue;

            List<Component> components = pair.Value.components;

            for (int i = 0; i < components.Count; i++)
                CreateVisualComponent(components[i], cloneTransform);
        }

        // Copy activeSelf, layer, and names only after hierarchy/components exist.
        for (int i = 0; i < orderedSources.Count; i++)
        {
            Transform source = orderedSources[i];
            Transform clone;

            if (source != null && sourceToClone.TryGetValue(source, out clone))
                CopyGameObjectState(source, clone);
        }

        builtSelection = CloneSelectionDictionary(selected);

        // Only now is the hierarchy reflected.
        ApplyMirrorScale();

        hasBuilt = true;
    }

    private void SynchronizeExistingHierarchy()
    {
        if (mirrorPivot == null)
            return;

        // The pivot remains a scene root and retains unit magnitude scale.
        mirrorPivot.position = transform.position;
        mirrorPivot.rotation = transform.rotation;

        for (int i = 0; i < orderedSources.Count; i++)
        {
            Transform source = orderedSources[i];
            Transform clone;

            if (source == null || !sourceToClone.TryGetValue(source, out clone))
                continue;

            if (source.parent == null)
            {
                /*
                 * Do not assign source world transforms directly while the
                 * pivot is negatively scaled. Instead store the transform that
                 * the root would have under the pivot's neutral position/rotation.
                 */
                CopySceneRootTransformUnderNeutralPivot(source, clone);
            }
            else
            {
                CopyLocalTransform(source, clone);
            }

            CopyGameObjectState(source, clone);
        }

        foreach (KeyValuePair<Transform, NodeSelection> pair in builtSelection)
        {
            List<Component> components = pair.Value.components;

            for (int i = 0; i < components.Count; i++)
            {
                Component sourceComponent = components[i];

                if (sourceComponent == null)
                    continue;

                Component cloneComponent;

                if (!componentClones.TryGetValue(
                        sourceComponent.GetInstanceID(),
                        out cloneComponent) ||
                    cloneComponent == null)
                {
                    continue;
                }

                SynchronizeVisualComponent(sourceComponent, cloneComponent);
            }
        }

        ApplyMirrorScale();
    }

    private void EnsureMirrorPivot()
    {
        if (mirrorPivot == null)
        {
            GameObject pivotObject = new GameObject("MirrorRoom Pivot (Generated)");
            MoveToMirrorScene(pivotObject);
            mirrorPivot = pivotObject.transform;
        }

        if (!mirrorPivot.gameObject.activeSelf)
            mirrorPivot.gameObject.SetActive(true);

        if (mirrorPivot.parent != null)
            mirrorPivot.SetParent(null, true);
    }

    private void PrepareNeutralPivot()
    {
        mirrorPivot.position = transform.position;
        mirrorPivot.rotation = transform.rotation;
        mirrorPivot.localScale = Vector3.one;
    }

    private void ApplyMirrorScale()
    {
        switch (mirrorAxis)
        {
            case MirrorAxis.LocalX:
                mirrorPivot.localScale = new Vector3(-1f, 1f, 1f);
                break;

            case MirrorAxis.LocalY:
                mirrorPivot.localScale = new Vector3(1f, -1f, 1f);
                break;

            default:
                // Correct axis for an unrotated default Unity Quad surface.
                mirrorPivot.localScale = new Vector3(1f, 1f, -1f);
                break;
        }
    }

    private void ClearGeneratedChildren()
    {
        if (mirrorPivot == null)
            return;

        for (int i = mirrorPivot.childCount - 1; i >= 0; i--)
        {
            Transform child = mirrorPivot.GetChild(i);

            if (child == null)
                continue;

            child.gameObject.SetActive(false);

            if (Application.isPlaying)
                Destroy(child.gameObject);
            else
                DestroyImmediate(child.gameObject);
        }
    }

    private void DestroyGeneratedPivot()
    {
        if (mirrorPivot == null)
            return;

        GameObject pivotObject = mirrorPivot.gameObject;
        mirrorPivot = null;

        if (Application.isPlaying)
            Destroy(pivotObject);
        else
            DestroyImmediate(pivotObject);
    }

    private void MoveToMirrorScene(GameObject gameObjectToMove)
    {
        Scene mirrorScene = gameObject.scene;

        if (mirrorScene.IsValid() &&
            mirrorScene.isLoaded &&
            gameObjectToMove.scene != mirrorScene)
        {
            SceneManager.MoveGameObjectToScene(gameObjectToMove, mirrorScene);
        }
    }

    private static int CompareTransformDepth(Transform a, Transform b)
    {
        return GetTransformDepth(a).CompareTo(GetTransformDepth(b));
    }

    private static int GetTransformDepth(Transform transformToCheck)
    {
        int depth = 0;
        Transform current = transformToCheck;

        while (current != null)
        {
            depth++;
            current = current.parent;
        }

        return depth;
    }

    private static void CopyLocalTransform(Transform source, Transform destination)
    {
        destination.localPosition = source.localPosition;
        destination.localRotation = source.localRotation;
        destination.localScale = source.localScale;
    }

    private void CopySceneRootTransformUnderNeutralPivot(
        Transform source,
        Transform destination)
    {
        Quaternion pivotRotation = mirrorPivot.rotation;

        destination.localPosition =
            Quaternion.Inverse(pivotRotation) * (source.position - mirrorPivot.position);

        destination.localRotation =
            Quaternion.Inverse(pivotRotation) * source.rotation;

        destination.localScale = source.localScale;
    }

    private static void CopyGameObjectState(Transform source, Transform destination)
    {
        destination.name = source.name;
        destination.gameObject.layer = source.gameObject.layer;

        if (destination.gameObject.activeSelf != source.gameObject.activeSelf)
            destination.gameObject.SetActive(source.gameObject.activeSelf);
    }

    private void CreateVisualComponent(Component sourceComponent, Transform destination)
    {
        MeshRenderer meshRenderer = sourceComponent as MeshRenderer;

        if (meshRenderer != null)
        {
            MeshRenderer clone = destination.gameObject.AddComponent<MeshRenderer>();
            componentClones[meshRenderer.GetInstanceID()] = clone;
            CopyMeshRenderer(meshRenderer, clone);
            return;
        }

        SkinnedMeshRenderer skinnedRenderer = sourceComponent as SkinnedMeshRenderer;

        if (skinnedRenderer != null)
        {
            SkinnedMeshRenderer clone = destination.gameObject.AddComponent<SkinnedMeshRenderer>();
            componentClones[skinnedRenderer.GetInstanceID()] = clone;
            CopySkinnedMeshRenderer(skinnedRenderer, clone);
            return;
        }

        SpriteRenderer spriteRenderer = sourceComponent as SpriteRenderer;

        if (spriteRenderer != null)
        {
            SpriteRenderer clone = destination.gameObject.AddComponent<SpriteRenderer>();
            componentClones[spriteRenderer.GetInstanceID()] = clone;
            CopySpriteRenderer(spriteRenderer, clone);
            return;
        }

        Light light = sourceComponent as Light;

        if (light != null)
        {
            Light clone = destination.gameObject.AddComponent<Light>();
            componentClones[light.GetInstanceID()] = clone;
            CopyLight(light, clone);
        }
    }

    private void SynchronizeVisualComponent(Component sourceComponent, Component cloneComponent)
    {
        MeshRenderer sourceMeshRenderer = sourceComponent as MeshRenderer;
        MeshRenderer cloneMeshRenderer = cloneComponent as MeshRenderer;

        if (sourceMeshRenderer != null && cloneMeshRenderer != null)
        {
            CopyMeshRenderer(sourceMeshRenderer, cloneMeshRenderer);
            return;
        }

        SkinnedMeshRenderer sourceSkinnedRenderer = sourceComponent as SkinnedMeshRenderer;
        SkinnedMeshRenderer cloneSkinnedRenderer = cloneComponent as SkinnedMeshRenderer;

        if (sourceSkinnedRenderer != null && cloneSkinnedRenderer != null)
        {
            CopySkinnedMeshRenderer(sourceSkinnedRenderer, cloneSkinnedRenderer);
            return;
        }

        SpriteRenderer sourceSpriteRenderer = sourceComponent as SpriteRenderer;
        SpriteRenderer cloneSpriteRenderer = cloneComponent as SpriteRenderer;

        if (sourceSpriteRenderer != null && cloneSpriteRenderer != null)
        {
            CopySpriteRenderer(sourceSpriteRenderer, cloneSpriteRenderer);
            return;
        }

        Light sourceLight = sourceComponent as Light;
        Light cloneLight = cloneComponent as Light;

        if (sourceLight != null && cloneLight != null)
            CopyLight(sourceLight, cloneLight);
    }

    private void CopyMeshRenderer(MeshRenderer source, MeshRenderer destination)
    {
        MeshFilter sourceFilter = source.GetComponent<MeshFilter>();
        MeshFilter destinationFilter = destination.GetComponent<MeshFilter>();

        if (sourceFilter != null)
        {
            if (destinationFilter == null)
                destinationFilter = destination.gameObject.AddComponent<MeshFilter>();

            destinationFilter.sharedMesh = sourceFilter.sharedMesh;
        }
        else if (destinationFilter != null)
        {
            destinationFilter.sharedMesh = null;
        }

        CopyRendererCommon(source, destination);

#if UNITY_2018_1_OR_NEWER
        destination.additionalVertexStreams = source.additionalVertexStreams;
#endif
    }

    private void CopySkinnedMeshRenderer(
        SkinnedMeshRenderer source,
        SkinnedMeshRenderer destination)
    {
        CopyRendererCommon(source, destination);

        destination.sharedMesh = source.sharedMesh;
        destination.quality = source.quality;
        destination.updateWhenOffscreen = source.updateWhenOffscreen;
        destination.localBounds = source.localBounds;

        Transform[] sourceBones = source.bones;
        Transform[] clonedBones = null;

        bool allBonesWereCloned = true;

        if (source.rootBone != null && !sourceToClone.ContainsKey(source.rootBone))
            allBonesWereCloned = false;

        if (sourceBones != null)
        {
            clonedBones = new Transform[sourceBones.Length];

            for (int i = 0; i < sourceBones.Length; i++)
            {
                Transform mappedBone;

                if (sourceBones[i] != null &&
                    sourceToClone.TryGetValue(sourceBones[i], out mappedBone))
                {
                    clonedBones[i] = mappedBone;
                }
                else
                {
                    allBonesWereCloned = false;
                    break;
                }
            }
        }

        if (allBonesWereCloned)
        {
            Transform mappedRootBone = null;

            if (source.rootBone != null)
                sourceToClone.TryGetValue(source.rootBone, out mappedRootBone);

            destination.rootBone = mappedRootBone;
            destination.bones = clonedBones;
        }
        else
        {
            /*
             * The strict ancestor-union rule means most skeletons are not
             * fully cloned. Sharing original bones is the least destructive
             * fallback, but does not guarantee a correct mirrored animation.
             */
            destination.rootBone = source.rootBone;
            destination.bones = sourceBones;
        }
    }

    private void CopySpriteRenderer(SpriteRenderer source, SpriteRenderer destination)
    {
        CopyRendererCommon(source, destination);

        destination.sprite = source.sprite;
        destination.color = source.color;
        destination.flipX = source.flipX;
        destination.flipY = source.flipY;
        destination.drawMode = source.drawMode;
        destination.size = source.size;
        destination.tileMode = source.tileMode;
        destination.adaptiveModeThreshold = source.adaptiveModeThreshold;
        destination.maskInteraction = source.maskInteraction;
        destination.spriteSortPoint = source.spriteSortPoint;
    }

    private void CopyLight(Light source, Light destination)
    {
        destination.type = source.type;
        destination.color = source.color;
        destination.intensity = source.intensity;
        destination.range = source.range;
        destination.spotAngle = source.spotAngle;
        destination.cookie = source.cookie;
        destination.cookieSize = source.cookieSize;
        destination.shadows = source.shadows;
        destination.shadowStrength = source.shadowStrength;
        destination.shadowBias = source.shadowBias;
        destination.shadowNormalBias = source.shadowNormalBias;
        destination.shadowNearPlane = source.shadowNearPlane;
        destination.cullingMask = source.cullingMask;
        destination.renderMode = source.renderMode;
        destination.bounceIntensity = source.bounceIntensity;
        destination.flare = source.flare;
        destination.enabled = source.enabled;
    }

    private void CopyRendererCommon(Renderer source, Renderer destination)
    {
        destination.sharedMaterials = source.sharedMaterials;
        destination.enabled = source.enabled;
        destination.shadowCastingMode = source.shadowCastingMode;
        destination.receiveShadows = source.receiveShadows;
        destination.lightProbeUsage = source.lightProbeUsage;
        destination.reflectionProbeUsage = source.reflectionProbeUsage;
        destination.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
        destination.sortingLayerID = source.sortingLayerID;
        destination.sortingOrder = source.sortingOrder;

        Transform mappedProbeAnchor;

        if (source.probeAnchor != null &&
            sourceToClone.TryGetValue(source.probeAnchor, out mappedProbeAnchor))
        {
            destination.probeAnchor = mappedProbeAnchor;
        }
        else
        {
            destination.probeAnchor = source.probeAnchor;
        }
    }

    private static Dictionary<Transform, NodeSelection> CloneSelectionDictionary(
        Dictionary<Transform, NodeSelection> source)
    {
        Dictionary<Transform, NodeSelection> result =
            new Dictionary<Transform, NodeSelection>();

        foreach (KeyValuePair<Transform, NodeSelection> pair in source)
        {
            NodeSelection clone = new NodeSelection(pair.Key);

            for (int i = 0; i < pair.Value.components.Count; i++)
                clone.Add(pair.Value.components[i]);

            result.Add(pair.Key, clone);
        }

        return result;
    }

    private static bool LightOverlapsWorldBox(Light light, WorldBox volume)
    {
        float range = Mathf.Max(0f, light.range);
        Vector3 closestPoint = volume.ClosestPoint(light.transform.position);

        return (light.transform.position - closestPoint).sqrMagnitude <= range * range;
    }

    /*
     * Separating Axis Theorem test:
     * renderer bounds are an AABB, while the BoxCollider is represented by
     * transformed half-edge vectors. This also avoids lossyScale.
     */
    private static bool BoundsOverlapWorldBox(Bounds bounds, WorldBox box)
    {
        Vector3 delta = box.center - bounds.center;
        Vector3 extents = bounds.extents;

        // AABB face normals.
        if (SeparatedOnAxis(Vector3.right, delta, extents, box)) return false;
        if (SeparatedOnAxis(Vector3.up, delta, extents, box)) return false;
        if (SeparatedOnAxis(Vector3.forward, delta, extents, box)) return false;

        // Transformed box face normals.
        if (SeparatedOnAxis(Vector3.Cross(box.halfY, box.halfZ), delta, extents, box)) return false;
        if (SeparatedOnAxis(Vector3.Cross(box.halfZ, box.halfX), delta, extents, box)) return false;
        if (SeparatedOnAxis(Vector3.Cross(box.halfX, box.halfY), delta, extents, box)) return false;

        // Cross products between AABB edges and transformed box edges.
        if (SeparatedOnAxis(Vector3.Cross(Vector3.right, box.halfX), delta, extents, box)) return false;
        if (SeparatedOnAxis(Vector3.Cross(Vector3.right, box.halfY), delta, extents, box)) return false;
        if (SeparatedOnAxis(Vector3.Cross(Vector3.right, box.halfZ), delta, extents, box)) return false;

        if (SeparatedOnAxis(Vector3.Cross(Vector3.up, box.halfX), delta, extents, box)) return false;
        if (SeparatedOnAxis(Vector3.Cross(Vector3.up, box.halfY), delta, extents, box)) return false;
        if (SeparatedOnAxis(Vector3.Cross(Vector3.up, box.halfZ), delta, extents, box)) return false;

        if (SeparatedOnAxis(Vector3.Cross(Vector3.forward, box.halfX), delta, extents, box)) return false;
        if (SeparatedOnAxis(Vector3.Cross(Vector3.forward, box.halfY), delta, extents, box)) return false;
        if (SeparatedOnAxis(Vector3.Cross(Vector3.forward, box.halfZ), delta, extents, box)) return false;

        return true;
    }

    private static bool SeparatedOnAxis(
        Vector3 axis,
        Vector3 centerDelta,
        Vector3 aabbExtents,
        WorldBox box)
    {
        float axisMagnitudeSquared = axis.sqrMagnitude;

        // Parallel edges can produce a zero cross product; that is not a valid SAT axis.
        if (axisMagnitudeSquared < 0.000000000001f)
            return false;

        float aabbRadius =
            aabbExtents.x * Mathf.Abs(axis.x) +
            aabbExtents.y * Mathf.Abs(axis.y) +
            aabbExtents.z * Mathf.Abs(axis.z);

        float boxRadius =
            Mathf.Abs(Vector3.Dot(axis, box.halfX)) +
            Mathf.Abs(Vector3.Dot(axis, box.halfY)) +
            Mathf.Abs(Vector3.Dot(axis, box.halfZ));

        float centerDistance = Mathf.Abs(Vector3.Dot(centerDelta, axis));

        float tolerance = 0.00001f * Mathf.Sqrt(axisMagnitudeSquared);

        return centerDistance > aabbRadius + boxRadius + tolerance;
    }
}
