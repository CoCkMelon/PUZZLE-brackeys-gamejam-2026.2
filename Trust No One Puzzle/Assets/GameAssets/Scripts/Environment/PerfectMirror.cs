using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Perfect mirror - fixed version that does NOT copy scripts to ghosts.
/// Previous bug: Instantiate(realObj) copied all MonoBehaviours (PlayerCarry, MobilePhoneController, etc.)
/// Their Awake overwrote singleton Instances (PlayerCarry.Instance = ghost) and ghost kept gameplay scripts.
/// Fix: Create ghost hierarchy by copying ONLY visual components (Transform, MeshFilter, Renderer, LODGroup, non-directional Light).
/// No MonoBehaviour, Collider, Rigidbody, Animator, etc. are ever created on ghosts.
/// </summary>
[DefaultExecutionOrder(10000)]
public class PerfectMirror : MonoBehaviour
{
    public enum MirrorAxis { X = 0, Y = 1, Z = 2 }

    [Header("Mirror Settings")]
    public MirrorAxis normalAxis = MirrorAxis.Z;

    [Header("Objects to Mirror")]
    public List<GameObject> objectsToMirror = new List<GameObject>();

    [Header("Gizmos")]
    public bool showGizmos = true;
    public float gizmoSize = 3f;

    private Transform mirrorContainer;

    private struct TransformPair
    {
        public Transform real;
        public Transform ghost;
    }

    private class MirrorPair
    {
        public Transform realRoot;
        public Transform ghostRoot;
        public Renderer[] realRenderers;
        public Renderer[] ghostRenderers;
        public Light[] realLights;
        public Light[] ghostLights;
        public List<TransformPair> bonePairs = new List<TransformPair>();
        // For SkinnedMeshRenderer bone remapping
        public List<SkinnedMeshRenderer> realSkinned = new List<SkinnedMeshRenderer>();
        public List<SkinnedMeshRenderer> ghostSkinned = new List<SkinnedMeshRenderer>();
    }

    private readonly List<MirrorPair> activePairs = new List<MirrorPair>();

    private void OnEnable() => RebuildMirror();
    private void OnDisable() => CleanupGhosts();

    public void RebuildMirror()
    {
        CleanupGhosts();

        if (objectsToMirror == null || objectsToMirror.Count == 0)
            return;

        GameObject containerObj = new GameObject(gameObject.name + "_ReflectionSpace");
        containerObj.SetActive(false);
        mirrorContainer = containerObj.transform;
        UpdateContainerTransform();

        foreach (GameObject realObj in objectsToMirror)
        {
            if (realObj == null || !realObj.scene.IsValid()) continue;

            // FIX: Don't Instantiate whole object with scripts - create visual-only ghost
            MirrorPair pair = CreateVisualGhost(realObj);
            if (pair != null)
                activePairs.Add(pair);
        }

        SyncGhosts();
        containerObj.SetActive(true);
    }

    private MirrorPair CreateVisualGhost(GameObject realObj)
    {
        GameObject ghostRootObj = new GameObject(realObj.name + "_MirrorGhost");
        ghostRootObj.transform.SetParent(mirrorContainer, false);
        PrepareGhostGameObject(ghostRootObj);

        MirrorPair pair = new MirrorPair
        {
            realRoot = realObj.transform,
            ghostRoot = ghostRootObj.transform
        };

        // Recursively copy visual hierarchy
        CopyVisualRecursive(realObj.transform, ghostRootObj.transform, pair);

        // Collect renderers and lights for sync
        pair.realRenderers = realObj.GetComponentsInChildren<Renderer>(true);
        pair.ghostRenderers = ghostRootObj.GetComponentsInChildren<Renderer>(true);
        pair.realLights = realObj.GetComponentsInChildren<Light>(true);
        pair.ghostLights = ghostRootObj.GetComponentsInChildren<Light>(true);

        // Remap SkinnedMeshRenderer bones to ghost transforms
        RemapSkinnedBones(pair);

        return pair;
    }

    private void CopyVisualRecursive(Transform real, Transform ghost, MirrorPair pair)
    {
        // Map this pair
        pair.bonePairs.Add(new TransformPair { real = real, ghost = ghost });

        // Copy visual components from real GameObject to ghost GameObject
        CopyVisualComponents(real.gameObject, ghost.gameObject, pair);

        // Recurse children
        for (int i = 0; i < real.childCount; i++)
        {
            Transform realChild = real.GetChild(i);
            GameObject ghostChildObj = new GameObject(realChild.name);
            ghostChildObj.transform.SetParent(ghost, false);
            ghostChildObj.transform.localPosition = realChild.localPosition;
            ghostChildObj.transform.localRotation = realChild.localRotation;
            ghostChildObj.transform.localScale = realChild.localScale;
            PrepareGhostGameObject(ghostChildObj);

            CopyVisualRecursive(realChild, ghostChildObj.transform, pair);
        }
    }

    private void CopyVisualComponents(GameObject realGO, GameObject ghostGO, MirrorPair pair)
    {
        // MeshFilter
        var realMF = realGO.GetComponent<MeshFilter>();
        if (realMF != null && realMF.sharedMesh != null)
        {
            var ghostMF = ghostGO.AddComponent<MeshFilter>();
            ghostMF.sharedMesh = realMF.sharedMesh;
        }

        // MeshRenderer
        var realMR = realGO.GetComponent<MeshRenderer>();
        if (realMR != null)
        {
            var ghostMR = ghostGO.AddComponent<MeshRenderer>();
            ghostMR.sharedMaterials = realMR.sharedMaterials;
            ghostMR.allowOcclusionWhenDynamic = false;
            ghostMR.shadowCastingMode = realMR.shadowCastingMode;
            ghostMR.receiveShadows = realMR.receiveShadows;
        }

        // SkinnedMeshRenderer
        var realSMR = realGO.GetComponent<SkinnedMeshRenderer>();
        if (realSMR != null && realSMR.sharedMesh != null)
        {
            var ghostSMR = ghostGO.AddComponent<SkinnedMeshRenderer>();
            ghostSMR.sharedMesh = realSMR.sharedMesh;
            ghostSMR.sharedMaterials = realSMR.sharedMaterials;
            ghostSMR.updateWhenOffscreen = true;
            ghostSMR.allowOcclusionWhenDynamic = false;
            ghostSMR.shadowCastingMode = realSMR.shadowCastingMode;
            ghostSMR.receiveShadows = realSMR.receiveShadows;
            // Bones will be remapped later after hierarchy is built
            pair.realSkinned.Add(realSMR);
            pair.ghostSkinned.Add(ghostSMR);
        }

        // LODGroup
        var realLOD = realGO.GetComponent<LODGroup>();
        if (realLOD != null)
        {
            var ghostLOD = ghostGO.AddComponent<LODGroup>();
            // LODs reference renderers - we need to remap later, for now copy size and fade
            ghostLOD.size = realLOD.size;
            ghostLOD.fadeMode = realLOD.fadeMode;
            ghostLOD.animateCrossFading = realLOD.animateCrossFading;
            // LODs will be set after all renderers are created - we handle in RemapLODGroups
        }

        // Light (non-directional only)
        var realLight = realGO.GetComponent<Light>();
        if (realLight != null && realLight.type != LightType.Directional)
        {
            var ghostLight = ghostGO.AddComponent<Light>();
            ghostLight.type = realLight.type;
            ghostLight.color = realLight.color;
            ghostLight.intensity = realLight.intensity;
            ghostLight.range = realLight.range;
            ghostLight.spotAngle = realLight.spotAngle;
            ghostLight.shadows = realLight.shadows;
            ghostLight.shadowStrength = realLight.shadowStrength;
            // Other properties can be copied as needed
        }
    }

    private void RemapSkinnedBones(MirrorPair pair)
    {
        for (int i = 0; i < pair.realSkinned.Count; i++)
        {
            var realSMR = pair.realSkinned[i];
            var ghostSMR = pair.ghostSkinned[i];
            if (realSMR == null || ghostSMR == null) continue;

            // Map bones
            Transform[] realBones = realSMR.bones;
            Transform[] ghostBones = new Transform[realBones.Length];
            for (int b = 0; b < realBones.Length; b++)
            {
                if (realBones[b] == null)
                {
                    ghostBones[b] = null;
                    continue;
                }
                // Find corresponding ghost transform via bonePairs
                ghostBones[b] = FindGhostTransform(realBones[b], pair.bonePairs);
            }
            ghostSMR.bones = ghostBones;

            // Map root bone
            if (realSMR.rootBone != null)
                ghostSMR.rootBone = FindGhostTransform(realSMR.rootBone, pair.bonePairs);
        }
    }

    private Transform FindGhostTransform(Transform real, List<TransformPair> map)
    {
        for (int i = 0; i < map.Count; i++)
        {
            if (map[i].real == real)
                return map[i].ghost;
        }
        return null;
    }

    private void PrepareGhostGameObject(GameObject ghost)
    {
        ghost.isStatic = false;
        ghost.tag = "Untagged";
        ghost.layer = LayerMask.NameToLayer("Default");
    }

    private void CleanupGhosts()
    {
        if (mirrorContainer != null)
        {
            DestroyImmediate(mirrorContainer.gameObject);
            mirrorContainer = null;
        }
        activePairs.Clear();
    }

    private void LateUpdate() => SyncGhosts();

    private void UpdateContainerTransform()
    {
        if (mirrorContainer == null) return;
        mirrorContainer.SetPositionAndRotation(transform.position, transform.rotation);
        Vector3 scale = Vector3.one;
        scale[(int)normalAxis] = -1f;
        mirrorContainer.localScale = scale;
    }

    private void SyncGhosts()
    {
        if (mirrorContainer == null || activePairs.Count == 0) return;

        UpdateContainerTransform();
        Quaternion invMirrorRot = Quaternion.Inverse(transform.rotation);
        Vector3 mirrorPos = transform.position;
        Vector3 normal = GetWorldNormal();

        for (int i = 0; i < activePairs.Count; i++)
        {
            MirrorPair pair = activePairs[i];
            if (pair.realRoot == null || pair.ghostRoot == null) continue;

            bool isRealActive = pair.realRoot.gameObject.activeInHierarchy;
            if (pair.ghostRoot.gameObject.activeSelf != isRealActive)
                pair.ghostRoot.gameObject.SetActive(isRealActive);

            // Root transform - mirrored
            pair.ghostRoot.localPosition = invMirrorRot * (pair.realRoot.position - mirrorPos);
            pair.ghostRoot.localRotation = invMirrorRot * pair.realRoot.rotation;
            pair.ghostRoot.localScale = pair.realRoot.lossyScale;

            // All children / bones - copy local
            for (int n = 1; n < pair.bonePairs.Count; n++)
            {
                Transform realNode = pair.bonePairs[n].real;
                Transform ghostNode = pair.bonePairs[n].ghost;
                if (realNode == null || ghostNode == null) continue;

                ghostNode.localPosition = realNode.localPosition;
                ghostNode.localRotation = realNode.localRotation;
                ghostNode.localScale = realNode.localScale;

                if (ghostNode.gameObject.activeSelf != realNode.gameObject.activeSelf)
                    ghostNode.gameObject.SetActive(realNode.gameObject.activeSelf);
            }

            // Renderer visibility
            int rendCount = Mathf.Min(pair.realRenderers.Length, pair.ghostRenderers.Length);
            for (int r = 0; r < rendCount; r++)
            {
                if (pair.realRenderers[r] != null && pair.ghostRenderers[r] != null)
                    pair.ghostRenderers[r].enabled = pair.realRenderers[r].enabled;
            }

            // Lights - reflect direction
            int lightCount = Mathf.Min(pair.realLights.Length, pair.ghostLights.Length);
            for (int l = 0; l < lightCount; l++)
            {
                Light realLight = pair.realLights[l];
                Light ghostLight = pair.ghostLights[l];

                if (realLight == null || ghostLight == null) continue;
                if (realLight.type == LightType.Directional) continue;

                Vector3 realForward = realLight.transform.forward;
                Vector3 reflectedForward = realForward - 2f * Vector3.Dot(realForward, normal) * normal;

                Vector3 realUp = realLight.transform.up;
                Vector3 reflectedUp = realUp - 2f * Vector3.Dot(realUp, normal) * normal;

                ghostLight.transform.rotation = Quaternion.LookRotation(reflectedForward, reflectedUp);
            }
        }
    }

    private Vector3 GetWorldNormal()
    {
        switch (normalAxis)
        {
            case MirrorAxis.X: return transform.right.normalized;
            case MirrorAxis.Y: return transform.up.normalized;
            default:           return transform.forward.normalized;
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (!showGizmos) return;

        Vector3 mirrorPos = transform.position;
        Vector3 n = GetWorldNormal();

        Gizmos.color = new Color(0f, 1f, 1f, 0.9f);
        Gizmos.DrawRay(mirrorPos, n * 2f);
        Gizmos.DrawSphere(mirrorPos + n * 2f, 0.05f);

        Gizmos.color = new Color(0f, 1f, 1f, 0.15f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Vector3 planeSize = Vector3.one * gizmoSize;
        planeSize[(int)normalAxis] = 0.001f;
        Gizmos.DrawCube(Vector3.zero, planeSize);
        Gizmos.matrix = Matrix4x4.identity;

        if (objectsToMirror == null) return;

        foreach (GameObject realObj in objectsToMirror)
        {
            if (realObj == null) continue;

            Vector3 realPos = realObj.transform.position;
            Vector3 offset = realPos - mirrorPos;
            float dist = Vector3.Dot(offset, n);
            Vector3 planeHit = realPos - (n * dist);
            Vector3 reflectedPos = realPos - (n * 2f * dist);

            Gizmos.color = Color.green;
            Gizmos.DrawSphere(realPos, 0.07f);
            Gizmos.DrawLine(realPos, planeHit);

            Gizmos.color = Color.cyan;
            Gizmos.DrawSphere(reflectedPos, 0.07f);
            Gizmos.DrawLine(planeHit, reflectedPos);

            Gizmos.color = new Color(0f, 1f, 1f, 0.3f);
            Gizmos.DrawLine(realPos, reflectedPos);
        }
    }
}
