using UnityEngine;

// Chase camera. It lags slightly behind the car's heading, which makes drifts easy to read.
public class FollowCamera : MonoBehaviour
{
    public Transform target;
    public float distance = 7f;
    public float height = 3f;
    public float lookHeight = 1f;
    public float followSharpness = 4f;   // lower = lazier camera, more visible drift angle

    void LateUpdate()
    {
        if (target == null) return;

        Vector3 flatForward = Vector3.ProjectOnPlane(target.forward, Vector3.up).normalized;
        Vector3 desired = target.position - flatForward * distance + Vector3.up * height;

        float k = 1f - Mathf.Exp(-followSharpness * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, desired, k);
        transform.LookAt(target.position + Vector3.up * lookHeight);
    }
}
