using System;
using System.Collections.Generic;
using UnityEngine;

// Runs after all character controllers, animators, and IK have finished for the frame
[DefaultExecutionOrder(10000)]
public class AutoObjectMirror : MonoBehaviour
{
    public enum MirrorAxis { X = 0, Y = 1, Z = 2 }

    [Header("Mirror Settings")]
    [Tooltip("Which local axis points OUT of the mirror glass?")]
    public MirrorAxis normalAxis = MirrorAxis.Z;

    [Header("Objects to Mirror")]
    [Tooltip("Drag active GameObjects from the Scene Hierarchy here.")]
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
        public List<TransformPair> bonePairs = new List<TransformPair>();
    }

    private readonly List<MirrorPair> activePairs = new List<MirrorPair>();

    // ─────────────────────────────────────────────────────────────
    // LIFECYCLE
    // ─────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        RebuildMirror();
    }

    private void OnDisable()
    {
        CleanupGhosts();
    }

    public void RebuildMirror()
    {
        CleanupGhosts();

        if (objectsToMirror == null || objectsToMirror.Count == 0)
            return;

        // Create container INACTIVE so cloned scripts never run Awake()/OnEnable()
        GameObject containerObj = new GameObject(gameObject.name + "_ReflectionSpace");
        containerObj.SetActive(false);

        mirrorContainer = containerObj.transform;
        UpdateContainerTransform();

        foreach (GameObject realObj in objectsToMirror)
        {
            if (realObj == null || !realObj.scene.IsValid()) continue;

            // Instantiate directly inside the inactive container
            GameObject ghostObj = Instantiate(realObj, mirrorContainer, false);
            ghostObj.name = realObj.name + "_MirrorGhost";

            PrepareGhostHierarchy(ghostObj);

            MirrorPair pair = new MirrorPair
            {
                realRoot = realObj.transform,
                ghostRoot = ghostObj.transform,
                realRenderers = realObj.GetComponentsInChildren<Renderer>(true),
                ghostRenderers = ghostObj.GetComponentsInChildren<Renderer>(true)
            };

            // Build a recursive 1:1 map of all bones/children to sync animations perfectly
            MapHierarchy(realObj.transform, ghostObj.transform, pair.bonePairs);
            
            activePairs.Add(pair);
        }

        // Force an immediate sync before activating to prevent 1-frame position glitches
        SyncGhosts();

        // Wake up the visual-only reflection space
        containerObj.SetActive(true);
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

    // ─────────────────────────────────────────────────────────────
    // MIRROR SYNC (LATE UPDATE)
    // ─────────────────────────────────────────────────────────────

    private void LateUpdate()
    {
        SyncGhosts();
    }

    private void UpdateContainerTransform()
    {
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

        for (int i = 0; i < activePairs.Count; i++)
        {
            MirrorPair pair = activePairs[i];

            if (pair.realRoot == null || pair.ghostRoot == null) continue;

            try
            {
                // 1. SYNC ACTIVE STATE
                bool isRealActive = pair.realRoot.gameObject.activeInHierarchy;
                if (pair.ghostRoot.gameObject.activeSelf != isRealActive)
                    pair.ghostRoot.gameObject.SetActive(isRealActive);

                // 2. SYNC ROOT TRANSFORM (Even if inactive, to prevent wrong pop-in positions)
                pair.ghostRoot.localPosition = invMirrorRot * (pair.realRoot.position - mirrorPos);
                pair.ghostRoot.localRotation = invMirrorRot * pair.realRoot.rotation;
                pair.ghostRoot.localScale = pair.realRoot.lossyScale;

                // 3. SYNC ALL CHILDREN / SKELETON BONES (1:1 Local Pose Copy)
                // We start at index 1 to skip the root, which was handled above.
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

                // 4. SYNC RENDERER VISIBILITY
                int rendCount = Mathf.Min(pair.realRenderers.Length, pair.ghostRenderers.Length);
                for (int r = 0; r < rendCount; r++)
                {
                    if (pair.realRenderers[r] != null && pair.ghostRenderers[r] != null)
                    {
                        pair.ghostRenderers[r].enabled = pair.realRenderers[r].enabled;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AutoObjectMirror] Error syncing {pair.realRoot.name}: {e.Message}");
            }
        }
    }

    private void MapHierarchy(Transform real, Transform ghost, List<TransformPair> map)
    {
        map.Add(new TransformPair { real = real, ghost = ghost });

        int childCount = Mathf.Min(real.childCount, ghost.childCount);
        for (int i = 0; i < childCount; i++)
        {
            MapHierarchy(real.GetChild(i), ghost.GetChild(i), map);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // GHOST PREPARATION & COMPONENT STRIPPING
    // ─────────────────────────────────────────────────────────────

    private void PrepareGhostHierarchy(GameObject ghost)
    {
        // 1. Reset Tags & Make Dynamic
        foreach (Transform t in ghost.GetComponentsInChildren<Transform>(true))
        {
            t.gameObject.isStatic = false;
            
            // FIX: Remove inherited tags (like "Player") so the camera actually renders the ghost!
            t.gameObject.layer = LayerMask.NameToLayer("Default"); 
        }

        // 2. Fix Occlusion Culling invisiblity
        foreach (Renderer r in ghost.GetComponentsInChildren<Renderer>(true))
        {
            r.allowOcclusionWhenDynamic = false; // Prevents being culled when behind the mirror wall
            
            if (r is SkinnedMeshRenderer smr)
                smr.updateWhenOffscreen = true; // Prevents negative-scale frustum issues
        }

        // 3. Clean removal order to prevent Unity console dependency warnings
        DestroyComponents<MonoBehaviour>(ghost);
        DestroyComponents<CharacterController>(ghost);
        DestroyComponents<Joint>(ghost);
        DestroyComponents<Collider>(ghost);
        DestroyComponents<Rigidbody>(ghost);

        // 4. General cleanup for anything remaining except visuals
        Component[] comps = ghost.GetComponentsInChildren<Component>(true);
        for (int i = comps.Length - 1; i >= 0; i--)
        {
            Component c = comps[i];
            if (c == null) continue;

            if (c is Transform || c is MeshFilter || c is Renderer || c is LODGroup)
                continue; // Keep visual elements

            DestroyImmediate(c);
        }
    }

    private void DestroyComponents<T>(GameObject ghost) where T : Component
    {
        T[] comps = ghost.GetComponentsInChildren<T>(true);
        for (int i = comps.Length - 1; i >= 0; i--)
        {
            if (comps[i] != null) DestroyImmediate(comps[i]);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // GIZMOS
    // ─────────────────────────────────────────────────────────────

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

            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(planeHit, 0.05f);

            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(planeHit, reflectedPos);
            Gizmos.DrawSphere(reflectedPos, 0.07f);
        }
    }
}