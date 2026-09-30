using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Physics telemetry HUD. Attach to the Car (same object as the Rigidbody).
/// Shows velocity, acceleration, g-force, momentum, kinetic energy,
/// centripetal acceleration/force, turn radius and tire grip usage.
/// Press F1 to toggle.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class TelemetryHUD : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Estimated tire/road friction coefficient (asphalt ~1.0, wet ~0.6, ice ~0.1)")]
    public float frictionCoefficient = 1.0f;
    [Tooltip("Higher = snappier numbers, lower = smoother numbers")]
    public float smoothing = 8f;
    [Tooltip("Below this speed (m/s) turn-based values are ignored (too noisy)")]
    public float minSpeedForTurnMath = 2f;
    public bool showHUD = true;

    Rigidbody rb;
    Vector3 prevVelocity;

    // Smoothed outputs
    float speed;            // m/s
    float longAccel;        // m/s^2 along car forward
    float latAccel;         // m/s^2 along car right
    float pathTurnRate;     // rad/s (rate the velocity vector rotates)
    float centripetalAccel; // m/s^2
    float radius;           // m
    float gripUsage;        // 0..1+ (1 = at friction limit)

    // Peaks
    float peakSpeed, peakG, peakCentripetal;

    GUIStyle boxStyle, labelStyle;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    void Start()
    {
        prevVelocity = rb.linearVelocity;
    }

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame)
            showHUD = !showHUD;

        if (Keyboard.current != null && Keyboard.current.f2Key.wasPressedThisFrame)
            peakSpeed = peakG = peakCentripetal = 0f; // reset peaks
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        Vector3 v = rb.linearVelocity;
        float k = 1f - Mathf.Exp(-smoothing * dt); // frame-rate independent smoothing

        // --- Speed ---
        speed = Mathf.Lerp(speed, v.magnitude, k);

        // --- Acceleration (world -> car local) ---
        Vector3 worldAccel = (v - prevVelocity) / dt;
        worldAccel.y = 0f; // ignore vertical bumps/gravity
        Vector3 localAccel = transform.InverseTransformDirection(worldAccel);
        longAccel = Mathf.Lerp(longAccel, localAccel.z, k);
        latAccel = Mathf.Lerp(latAccel, localAccel.x, k);

        // --- Centripetal acceleration ---
        // a_c = speed * (rate the velocity direction rotates). Using the velocity
        // vector (not car heading) makes this correct while sliding sideways.
        float targetTurnRate = 0f;
        if (v.magnitude > minSpeedForTurnMath && prevVelocity.magnitude > minSpeedForTurnMath)
        {
            float angleDeg = Vector3.SignedAngle(prevVelocity, v, Vector3.up);
            targetTurnRate = angleDeg * Mathf.Deg2Rad / dt;
        }
        pathTurnRate = Mathf.Lerp(pathTurnRate, targetTurnRate, k);
        float targetCentripetal = speed * Mathf.Abs(pathTurnRate);
        centripetalAccel = Mathf.Lerp(centripetalAccel, targetCentripetal, k);
        radius = Mathf.Abs(pathTurnRate) > 0.05f ? speed / Mathf.Abs(pathTurnRate) : float.PositiveInfinity;

        // --- Friction / grip usage ---
        // Horizontal force the tires must supply = m * |a|. Limit = mu * m * g.
        float horizAccel = new Vector2(longAccel, latAccel).magnitude;
        float maxAccel = frictionCoefficient * Physics.gravity.magnitude;
        gripUsage = Mathf.Lerp(gripUsage, horizAccel / maxAccel, k);

        // --- Peaks ---
        peakSpeed = Mathf.Max(peakSpeed, speed);
        peakG = Mathf.Max(peakG, horizAccel / 9.81f);
        peakCentripetal = Mathf.Max(peakCentripetal, centripetalAccel);

        prevVelocity = v;
    }

    void OnGUI()
    {
        if (!showHUD || rb == null) return;

        if (boxStyle == null)
        {
            boxStyle = new GUIStyle(GUI.skin.box);
            boxStyle.normal.background = MakeTex(new Color(0f, 0f, 0f, 0.6f));
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };
            labelStyle.normal.textColor = Color.white;
        }

        float mass = rb.mass;
        float g = 9.81f;
        float totalAccel = new Vector2(longAccel, latAccel).magnitude;
        Vector3 momentum = mass * rb.linearVelocity;
        float kineticEnergy = 0.5f * mass * speed * speed;
        float centripetalForce = mass * centripetalAccel;
        float frictionForce = mass * totalAccel;
        float frictionLimit = frictionCoefficient * mass * g;

        string gripColor = gripUsage < 0.8f ? "#7CFC00" : (gripUsage < 1.0f ? "#FFD700" : "#FF4444");
        string radiusText = float.IsInfinity(radius) ? "straight" : $"{radius:0.0} m";

        string text =
            "<b>TELEMETRY</b>  (F1 hide, F2 reset peaks)\n" +
            $"Speed:            {speed * 3.6f:0} km/h  ({speed:0.0} m/s)\n" +
            $"Accel (forward):  {longAccel:0.0} m/s²  ({longAccel / g:0.00} g)\n" +
            $"Accel (lateral):  {latAccel:0.0} m/s²  ({latAccel / g:0.00} g)\n" +
            $"Mass:             {mass:0} kg\n" +
            $"Momentum:         {momentum.magnitude:0} kg·m/s\n" +
            $"Kinetic energy:   {kineticEnergy / 1000f:0.0} kJ\n" +
            "<b>CORNERING</b>\n" +
            $"Turn radius:      {radiusText}\n" +
            $"Centripetal acc:  {centripetalAccel:0.0} m/s²\n" +
            $"Centripetal F:    {centripetalForce:0} N\n" +
            $"Tire force:       {frictionForce:0} / {frictionLimit:0} N\n" +
            $"Grip usage:       <color={gripColor}>{gripUsage * 100f:0}%</color>\n" +
            "<b>PEAKS</b>\n" +
            $"Top speed: {peakSpeed * 3.6f:0} km/h | Max g: {peakG:0.00} | Max a_c: {peakCentripetal:0.0}";

        float w = 380f, h = 320f;
        Rect r = new Rect(Screen.width - w - 10f, 10f, w, h);
        GUI.Box(r, GUIContent.none, boxStyle);
        GUI.Label(new Rect(r.x + 10f, r.y + 8f, r.width - 20f, r.height - 16f), text, labelStyle);
    }

    static Texture2D MakeTex(Color c)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }
}