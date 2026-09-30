using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Adds a sense of speed: the FOV widens and a light camera motion blur fades in as the car speeds up.
// Put this on the Main Camera. The car and the Global Volume are found automatically if left empty.
// Needs: URP, "Post Processing" ticked on the camera (for the blur). FOV works without it.
[RequireComponent(typeof(Camera))]
public class SpeedEffects : MonoBehaviour
{
    public CarController car;
    public Volume volume;                   // your Global Volume

    [Header("Ramp")]
    public float startSpeedKmh = 30f;       // effects begin here
    public float fullSpeedKmh = 130f;       // effects at full strength here
    public float response = 4f;             // higher = reacts faster

    [Header("Field of view")]
    public float fovBoost = 18f;            // degrees added at full speed (set 0 to disable)

    [Header("Motion blur (costs some GPU, turn off if FPS drops)")]
    public bool useMotionBlur = true;
    [Range(0f, 1f)]
    public float maxBlur = 0.3f;            // "just a bit"

    Camera cam;
    Rigidbody carBody;
    float baseFov;
    float amount;                           // 0..1 smoothed speed factor
    MotionBlur blur;

    void Start()
    {
        cam = GetComponent<Camera>();
        baseFov = cam.fieldOfView;

        if (car == null) car = FindAnyObjectByType<CarController>();
        if (car != null) carBody = car.GetComponent<Rigidbody>();

        if (useMotionBlur)
        {
            if (volume == null) volume = FindAnyObjectByType<Volume>();
            if (volume != null)
            {
                // volume.profile is a runtime copy, so your saved profile asset isn't modified
                if (!volume.profile.TryGet(out blur))
                    blur = volume.profile.Add<MotionBlur>(false);
                blur.intensity.Override(0f);
                blur.active = true;
            }
            else
            {
                Debug.LogWarning("SpeedEffects: no Volume found, motion blur disabled.");
            }
        }
    }

    void LateUpdate()
    {
        if (carBody == null) return;

        float kmh = carBody.linearVelocity.magnitude * 3.6f;
        float target = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(startSpeedKmh, fullSpeedKmh, kmh));
        amount = Mathf.Lerp(amount, target, 1f - Mathf.Exp(-response * Time.deltaTime));

        cam.fieldOfView = baseFov + fovBoost * amount;

        if (blur != null)
            blur.intensity.Override(maxBlur * amount);
    }
}