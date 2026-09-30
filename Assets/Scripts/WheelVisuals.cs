using UnityEngine;

// Moves the visual tire meshes with the suspension, spins them, and steers the front pair.
// Put this on the same object as CarController (the "Car" object).
public class WheelVisuals : MonoBehaviour
{
    public CarController car;

    [Header("Visual tire meshes")]
    public Transform tireFL;
    public Transform tireFR;
    public Transform tireRL;   // the "BL Tire" mesh
    public Transform tireRR;   // the "BR Tire" mesh

    [Header("Spin")]
    public Vector3 spinAxis = Vector3.right;                 // the axle, in the tire's own local space. Try (0,0,1) or (0,1,0) if it wobbles
    public float[] spinSign = { 1f, 1f, 1f, 1f };           // set an entry to -1 if that tire spins backwards

    [Header("Smoothing")]
    public float springFollow = 25f;

    Transform[] tires;
    Transform[] markers;
    Quaternion[] baseRot;          // each tire's rotation relative to the car at start
    readonly float[] spin = new float[4];
    readonly float[] shown = new float[4];

    void Start()
    {
        if (car == null) car = GetComponent<CarController>();

        tires = new[] { tireFL, tireFR, tireRL, tireRR };
        markers = new[] { car.wheelFL, car.wheelFR, car.wheelRL, car.wheelRR };
        baseRot = new Quaternion[4];

        for (int i = 0; i < 4; i++)
        {
            baseRot[i] = Quaternion.Inverse(car.transform.rotation) * tires[i].rotation;
            shown[i] = car.suspensionTravel * 0.5f;
        }
    }

    void LateUpdate()
    {
        float k = 1f - Mathf.Exp(-springFollow * Time.deltaTime);
        float spinRate = car.ForwardSpeed / car.wheelRadius * Mathf.Rad2Deg;

        for (int i = 0; i < 4; i++)
        {
            bool front = i < 2;

            // Tire centre hangs below the marker by the current spring length, so it touches the ground.
            shown[i] = Mathf.Lerp(shown[i], car.springLength[i], k);
            Vector3 pos = markers[i].position - car.transform.up * shown[i];

            bool locked = !front && car.Handbrake;   // handbrake locks the rear tires
            if (!locked) spin[i] = Mathf.Repeat(spin[i] + spinRate * Time.deltaTime, 360f);

            float steer = front ? car.SteerAngle : 0f;
            Quaternion rot = car.transform.rotation
                           * Quaternion.AngleAxis(steer, Vector3.up)
                           * baseRot[i]
                           * Quaternion.AngleAxis(spin[i] * spinSign[i], spinAxis);

            tires[i].SetPositionAndRotation(pos, rot);
        }
    }
}