using UnityEngine;
using System.Collections.Generic;
using System;
using System.Reflection;

public class MirrorRoom2 : MonoBehaviour
{
 //public LayerMask includedLayers;
 public int maxDepth = 4;
 Transform _root;
 //BoxCollider _box;
 

 public List<Transform> srcObjects = new List<Transform>();
 public Dictionary<Transform, Transform> refMap = new Dictionary<Transform, Transform>();
 public List<Transform> refObjects = new List<Transform>();
 
 private void OnEnable()
 {
     print("sf");
  CreateMirrorRoot();
  _root.localScale = new Vector3(1,1,1);
  CreateReflections();
  _root.localScale = new Vector3(-1,1,1);
 }
 private void OnDisable()
 {
  DestroyReflections();
 }
 private void CreateMirrorRoot()
 {
  if (_root != null) return;
  GameObject root = new GameObject("MirrorRoot");
  _root = root.transform;
  _root.SetPositionAndRotation(transform.position, transform.rotation);
  // Set scale after setting children
 }
 
 // Not updating mirror root
 
 private void UpdateReflections()
 {
  foreach(var pair in refMap)
  {
   pair.Value.position = pair.Key.position;
   pair.Value.rotation = pair.Key.rotation;
  }
 }
 private void DestroyReflections()
 {
  foreach(Transform tr in refObjects)
  {
   Destroy(tr.gameObject);
  }
  refMap.Clear();
  refObjects.Clear();
 }


 // private void CopyVisualComponents(GameObject src, GameObject refl)
 // {
 //  refl.transform = src.transform;
 //  srcMeshFilter = src.meshFilter;
 //  if(srcMeshFilter) {
 //   MeshRenderer clone = refl.AddComponent<MeshRenderer>();
 //   
 //  }
 //  refl.meshRenderer = src.meshRenderer;
 //  refl.material = src.material;
 // }
    private void CopyComponentValues(Component source, Component destination)
    {
        Type type = source.GetType();
        // Adjust binding flags to include private fields if needed
        FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        
        foreach (FieldInfo field in fields)
        {
            field.SetValue(destination, field.GetValue(source));
        }
    }
 private void CreateReflections()
 {
  foreach(var srcTr in srcObjects)
  {
   GameObject src = srcTr.gameObject;
   GameObject refl = new GameObject(src.name + "_Reflection");

           refl.transform.position = src.transform.position;
        refl.transform.rotation = src.transform.rotation;
        MeshFilter originalMesh = src.GetComponent<MeshFilter>();
        if (originalMesh != null)
        {
            MeshFilter clonedMesh = refl.AddComponent<MeshFilter>();
            CopyComponentValues(originalMesh, clonedMesh);
        }
   //CopyVisualComponents(src, refl);
   refl.transform.parent = _root;
   refObjects.Add(refl.transform);
   refMap.Add(srcTr,refl.transform);
  }
 }

 private void LateUpdate()
 {
  UpdateReflections();
 }
}
