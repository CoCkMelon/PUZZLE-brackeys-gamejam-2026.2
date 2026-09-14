using UnityEngine;

public class MirrorTurnOff : MonoBehaviour
{
    public GameObject target;
    bool needDestroy = false;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void OnTriggerEnter(Collider other)
    {
        if(other.gameObject.tag == "Player"){
            needDestroy = true;
        }
    }
    void LateUpdate()
    {
        if(needDestroy) {
            target.SetActive(false);
            needDestroy = false;
            Destroy(gameObject);
        }
    }
}
