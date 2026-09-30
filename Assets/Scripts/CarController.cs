using UnityEngine;
using UnityEngine.InputSystem;

// Arcade-sim car: raycast suspension + slip-angle tire forces + friction circle.
// Rear-wheel drive. Controls: W/S or Up/Down = throttle/brake/reverse,
// A/D or Left/Right = steer, Space = handbrake, R = reset upright.
// Gamepad: RT/LT = throttle/brake, left stick = steer, A/Cross = handbrake, Y/Triangle = reset.
[RequireComponent(typeof(Rigidbody))]
public class CarController : MonoBehaviour
{
    [Header("Wheel markers (empties at the axle centre of each tire)")]
    public Transform wheelFL;
    public Transform wheelFR;
    public Transform wheelRL;
    public Transform wheelRR;
    public LayerMask groundMask;

    [Header("Suspension")]
    public float wheelRadius = 0.35f;         // metres
    public float suspensionTravel = 0.2f;     // metres of spring length
    public float springStiffness = 30000f;    // N/m
    public float damping = 3000f;             // N per m/s
    public float antiRollStiffness = 6000f;   // N per metre of left/right compression difference (0 = off). Reduces body roll.

    [Header("Engine and brakes (forces are totals for the whole car)")]
    public float engineForce = 7000f;         // N, split over the rear wheels
    public float maxSpeed = 40f;              // m/s (~144 km/h) drive fades out toward this
    public float brakeForce = 12000f;         // N
    public float coastBrake = 1500f;          // N, slows the car when you let go of throttle
    public float driveFadePower = 2f;         // 1 = engine force fades out linearly toward max speed (feels like it stops accelerating). Higher = stays strong longer.

    [Header("Steering")]
    public float steerAngleLowSpeed = 32f;    // degrees
    public float steerAngleHighSpeed = 12f;   // degrees
    public float steerRate = 200f;            // degrees per second
    [Range(0f, 1f)]
    public float driftAssist = 0.4f;          // auto-countersteer strength while sliding (0 = off, 1 = strong)
    public float driftAssistStartAngle = 4f;  // degrees of slide before the assist starts
    public float driftAssistFullAngle = 20f;  // degrees of slide where the assist is at full strength

    [Header("Tires")]
    public float grip = 1.1f;                 // friction coefficient (mu)
    public float handbrakeRearGrip = 0.45f;   // rear grip multiplier while handbrake is held
    public float tireB = 10f;                 // stiffness: how fast grip builds with slip angle
    public float tireC = 1.5f;                // shape: how much grip drops after the peak (1.5 = slides at ~70%)

    [Header("Aero")]
    public float downforce = 1.5f;            // N per (m/s)^2 for the whole car. 0 = off. Adds grip at speed.

    [Header("Resistance")]
    public float dragCoefficient = 0.4f;      // air drag, grows with speed squared
    public float rollingResistance = 20f;     // grows with speed

    [Header("Balance")]
    public float comHeightAboveAxle = 0.1f;   // lower = harder to roll over
    public float forceHeight = 0.3f;          // height above ground where tire forces act (0 = realistic but tippy)

    [Header("Debug")]
    public bool showDebugGUI = true;          // simple speed / drift label (top-left)

    Rigidbody rb;
    Transform[] wheels;
    float steerAngle;
    float throttleIn, steerIn;
    bool handbrake;

    // Read by WheelVisuals and DriftSmoke. Order: FL, FR, RL, RR.
    [HideInInspector] public bool[] grounded = new bool[4];
    [HideInInspector] public float[] springLength = new float[4];   // 0 = fully compressed, suspensionTravel = fully extended
    [HideInInspector] public float[] slide = new float[4];          // how hard each tire is sliding (m/s)
    [HideInInspector] public Vector3[] contact = new Vector3[4];    // where each tire touches the ground
    public float SteerAngle => steerAngle;
    public bool Handbrake => handbrake;
    public float ForwardSpeed { get; private set; }
    public float Speed { get; private set; }              // total speed (m/s)
    public float DriftAngle { get; private set; }         // degrees between nose and travel direction (0 when slow)

    // Read by PhysicsHUD and CarAudio
    public Vector3 TireForceTotal { get; private set; }   // sum of all tire forces (N)
    public float MaxFriction { get; private set; }        // sum of mu * load over all tires (N)
    public float GripUsage { get; private set; }          // 0 = coasting, 1 = every tire at its grip limit
    public float ThrottleInput => throttleIn;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        if (groundMask.value == 0) groundMask = LayerMask.GetMask("Ground");

        wheels = new[] { wheelFL, wheelFR, wheelRL, wheelRR };

        foreach (var w in wheels)
        {
            if (w == null)
            {
                Debug.LogError("CarController: a wheel marker slot is empty. Assign all four.", this);
                enabled = false;
                return;
            }
        }
        Debug.Log("CarController running. Ground mask value = " + groundMask.value);

        // Centre of mass: middle of the four axles, slightly above them.
        Vector3 avg = Vector3.zero;
        foreach (var w in wheels) avg += transform.InverseTransformPoint(w.position);
        avg /= wheels.Length;
        rb.centerOfMass = avg + Vector3.up * comHeightAboveAxle;
    }

    void Update()
    {
        float thr = 0f, str = 0f;
        bool hb = false;

        var kb = Keyboard.current;
        if (kb != null)
        {
            thr = ((kb.wKey.isPressed || kb.upArrowKey.isPressed) ? 1f : 0f)
                - ((kb.sKey.isPressed || kb.downArrowKey.isPressed) ? 1f : 0f);
            str = ((kb.dKey.isPressed || kb.rightArrowKey.isPressed) ? 1f : 0f)
                - ((kb.aKey.isPressed || kb.leftArrowKey.isPressed) ? 1f : 0f);
            hb = kb.spaceKey.isPressed;

            if (kb.rKey.wasPressedThisFrame) ResetCar();
        }

        var pad = Gamepad.current;
        if (pad != null)
        {
            float padThrottle = pad.rightTrigger.ReadValue() - pad.leftTrigger.ReadValue();
            float padSteer = pad.leftStick.x.ReadValue();
            if (Mathf.Abs(padThrottle) > Mathf.Abs(thr)) thr = padThrottle;
            if (Mathf.Abs(padSteer) > Mathf.Abs(str)) str = padSteer;
            hb |= pad.buttonSouth.isPressed;

            if (pad.buttonNorth.wasPressedThisFrame) ResetCar();
        }

        throttleIn = thr;
        steerIn = str;
        handbrake = hb;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        Vector3 vel = rb.linearVelocity;
        float speed = vel.magnitude;
        float carFwdSpeed = Vector3.Dot(vel, transform.forward);
        ForwardSpeed = carFwdSpeed;
        Speed = speed;

        // --- Drift angle. Signed: + means the car travels to the right of where its nose points ---
        Vector3 flatVel = Vector3.ProjectOnPlane(vel, transform.up);
        float signedDrift = 0f;
        if (flatVel.magnitude > 2f)
            signedDrift = Vector3.SignedAngle(transform.forward, flatVel, transform.up);
        DriftAngle = Mathf.Abs(signedDrift);

        // --- Steering: smaller angle at speed, smoothed so keyboard input isn't instant ---
        float maxSteer = Mathf.Lerp(steerAngleLowSpeed, steerAngleHighSpeed, Mathf.Clamp01(speed / maxSpeed));
        float steerTarget = steerIn * maxSteer;

        // Drift assist: nudge the wheels toward the direction of travel while sliding (auto countersteer)
        if (driftAssist > 0f && carFwdSpeed > 3f)
        {
            float assist = Mathf.InverseLerp(driftAssistStartAngle, driftAssistFullAngle, Mathf.Abs(signedDrift)) * driftAssist;
            steerTarget += Mathf.Clamp(signedDrift, -maxSteer, maxSteer) * assist;
        }
        steerTarget = Mathf.Clamp(steerTarget, -maxSteer, maxSteer);
        steerAngle = Mathf.MoveTowards(steerAngle, steerTarget, steerRate * dt);

        // --- Throttle vs brake ---
        bool braking = Mathf.Abs(throttleIn) > 0.01f
                       && Mathf.Abs(carFwdSpeed) > 0.5f
                       && Mathf.Sign(throttleIn) != Mathf.Sign(carFwdSpeed);

        float driveTotal = 0f;
        if (!braking)
        {
            float limit = throttleIn >= 0f ? maxSpeed : maxSpeed * 0.3f;
            driveTotal = throttleIn * engineForce * Mathf.Clamp01(1f - Mathf.Pow(Mathf.Abs(carFwdSpeed) / limit, driveFadePower));
        }

        float brakeTotal = braking ? brakeForce : (Mathf.Abs(throttleIn) < 0.01f ? coastBrake : 0f);

        float rayLen = wheelRadius + suspensionTravel;
        float wheelMass = rb.mass * 0.25f;
        float downPerTire = downforce * speed * speed * 0.25f;   // aero downforce shared over four tires

        Vector3 forceSum = Vector3.zero;
        float usedSum = 0f, maxSum = 0f;

        for (int i = 0; i < wheels.Length; i++)
        {
            Transform w = wheels[i];
            bool front = i < 2;

            bool hitGround = Physics.Raycast(w.position, -transform.up, out RaycastHit hit, rayLen, groundMask,
                                             QueryTriggerInteraction.Ignore);
            Debug.DrawRay(w.position, -transform.up * rayLen, hitGround ? Color.green : Color.red);
            grounded[i] = hitGround;
            slide[i] = 0f;
            springLength[i] = suspensionTravel;
            if (!hitGround) continue; // wheel is in the air

            Vector3 pointVel = rb.GetPointVelocity(hit.point);

            // --- Suspension: spring + damper along the car's up axis ---
            float springLen = hit.distance - wheelRadius;
            springLength[i] = Mathf.Clamp(springLen, 0f, suspensionTravel);
            contact[i] = hit.point;
            float compression = Mathf.Clamp(suspensionTravel - springLen, 0f, suspensionTravel);
            float springVel = Vector3.Dot(pointVel, transform.up);
            float load = Mathf.Max(0f, compression * springStiffness - springVel * damping);
            rb.AddForceAtPosition(transform.up * load, w.position);

            // --- Aero downforce: pushes the car onto the road and adds tire load (more grip at speed) ---
            if (downPerTire > 0f)
                rb.AddForceAtPosition(-transform.up * downPerTire, w.position);
            float tireLoad = load + downPerTire;

            // --- Wheel axes on the ground (front wheels are rotated by the steering angle) ---
            Vector3 fwdDir = front ? Quaternion.AngleAxis(steerAngle, transform.up) * transform.forward
                                   : transform.forward;
            fwdDir = Vector3.ProjectOnPlane(fwdDir, hit.normal).normalized;
            Vector3 rightDir = Vector3.Cross(hit.normal, fwdDir);

            float vLong = Vector3.Dot(pointVel, fwdDir);
            float vLat = Vector3.Dot(pointVel, rightDir);

            // --- Friction budget for this tire ---
            float mu = grip;
            if (!front && handbrake) mu *= handbrakeRearGrip;
            float maxF = mu * tireLoad;

            // --- Lateral force from slip angle (rises, peaks, then drops = sliding) ---
            float slip = Mathf.Atan2(vLat, Mathf.Max(Mathf.Abs(vLong), 1.5f));
            float fy = -maxF * Mathf.Sin(tireC * Mathf.Atan(tireB * slip));
            float fyStop = wheelMass * Mathf.Abs(vLat) / dt;   // never push harder than needed to stop the slide (prevents jitter)
            fy = Mathf.Clamp(fy, -fyStop, fyStop);

            // --- Longitudinal force: drive on rear wheels, braking on all ---
            float fx = 0f;
            if (!front) fx += driveTotal * 0.5f;

            float brake = brakeTotal * 0.25f;
            if (!front && handbrake) brake += brakeForce * 0.25f;
            if (brake > 0f)
                fx -= Mathf.Sign(vLong) * Mathf.Min(brake, wheelMass * Mathf.Abs(vLong) / dt);

            // --- Friction circle: drive/brake and cornering share one grip budget ---
            float mag = Mathf.Sqrt(fx * fx + fy * fy);

            // Slide amount for smoke: sideways slip speed, plus extra when demand exceeds grip (wheelspin / locked brakes)
            float slideAmount = Mathf.Abs(vLat);
            if (mag > maxF && maxF > 1f) slideAmount += (mag / maxF - 1f) * 6f;
            slide[i] = slideAmount;

            if (mag > maxF)
            {
                float s = maxF / mag;
                fx *= s;
                fy *= s;
            }

            Vector3 tireForce = fwdDir * fx + rightDir * fy;
            forceSum += tireForce;
            usedSum += tireForce.magnitude;
            maxSum += maxF;

            Vector3 applyPoint = hit.point + transform.up * forceHeight;
            rb.AddForceAtPosition(tireForce, applyPoint);
        }

        // --- Anti-roll bars (front and rear) ---
        ApplyAntiRoll(0, 1);
        ApplyAntiRoll(2, 3);

        // --- Values for HUD / audio ---
        TireForceTotal = forceSum;
        MaxFriction = maxSum;
        GripUsage = maxSum > 1f ? Mathf.Clamp01(usedSum / maxSum) : 0f;

        // --- Air drag and rolling resistance ---
        Vector3 v = rb.linearVelocity;
        rb.AddForce(-v * v.magnitude * dragCoefficient - v * rollingResistance);
    }

    // Pushes the more-compressed side of an axle back up and the other side down,
    // which resists body roll in corners.
    void ApplyAntiRoll(int left, int right)
    {
        if (antiRollStiffness <= 0f) return;
        if (!grounded[left] || !grounded[right]) return;

        float diff = springLength[right] - springLength[left];   // > 0 means the left side is more compressed
        float f = diff * antiRollStiffness;
        rb.AddForceAtPosition(transform.up * f, wheels[left].position);
        rb.AddForceAtPosition(-transform.up * f, wheels[right].position);
    }

    void ResetCar()
    {
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        steerAngle = 0f;
        transform.position += Vector3.up * 1f;
        transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
    }

    void OnGUI()
    {
        if (!showDebugGUI || rb == null) return;
        GUI.Label(new Rect(10, 10, 300, 25), $"Speed: {Speed * 3.6f:0} km/h");
        GUI.Label(new Rect(10, 30, 300, 25), $"Drift angle: {DriftAngle:0} deg");
    }
}