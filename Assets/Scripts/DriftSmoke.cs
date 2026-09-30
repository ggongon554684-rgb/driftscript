using UnityEngine;

// Drift / wheelspin smoke. Builds its own particle systems and material, so no assets are needed.
// Put this on the same object as CarController (the "Car" object).
public class DriftSmoke : MonoBehaviour
{
    public CarController car;

    [Header("When to smoke")]
    public bool rearWheelsOnly = true;
    public float slideStart = 2.5f;     // m/s of sliding before smoke begins
    public float slideFull = 8f;        // m/s of sliding for full smoke

    [Header("Look")]
    public float maxRate = 60f;         // particles per second per tire at full slide
    public Color smokeColor = new Color(0.92f, 0.92f, 0.92f, 1f);
    public Vector2 lifetime = new Vector2(0.8f, 1.6f);
    public Vector2 startSize = new Vector2(0.5f, 1.0f);

    readonly ParticleSystem[] systems = new ParticleSystem[4];
    Material mat;

    void Start()
    {
        if (car == null) car = GetComponent<CarController>();
        mat = MakeMaterial();

        for (int i = 0; i < 4; i++)
        {
            if (rearWheelsOnly && i < 2) continue;   // 0 and 1 are the front wheels
            systems[i] = MakeSystem(i);
        }
    }

    void LateUpdate()
    {
        for (int i = 0; i < 4; i++)
        {
            ParticleSystem ps = systems[i];
            if (ps == null) continue;

            bool active = car.grounded[i];
            float t = active ? Mathf.InverseLerp(slideStart, slideFull, car.slide[i]) : 0f;

            var emission = ps.emission;
            emission.rateOverTime = t * maxRate;

            if (active) ps.transform.position = car.contact[i] + Vector3.up * 0.05f;
        }
    }

    ParticleSystem MakeSystem(int index)
    {
        var go = new GameObject("DriftSmoke_" + index);
        go.transform.SetParent(transform, false);

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;   // smoke stays behind the car
        main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime.x, lifetime.y);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 1.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(startSize.x, startSize.y);
        main.startColor = smokeColor;
        main.gravityModifier = -0.05f;                                 // drifts upward slowly
        main.maxParticles = 300;

        var emission = ps.emission;
        emission.rateOverTime = 0f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.15f;

        // Fade in quickly, then fade out.
        var colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.7f, 0.7f, 0.7f), 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.6f, 0.15f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = gradient;

        // Puffs grow as they age.
        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.4f, 1f, 1.8f));

        var psr = go.GetComponent<ParticleSystemRenderer>();
        psr.renderMode = ParticleSystemRenderMode.Billboard;
        psr.material = mat;

        ps.Play();
        return ps;
    }

    Material MakeMaterial()
    {
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");

        var m = new Material(shader);
        m.mainTexture = MakeSoftCircle(64);
        return m;
    }

    // A soft round blob, so the smoke isn't made of hard-edged squares.
    Texture2D MakeSoftCircle(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        float c = (size - 1) / 2f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(c, c)) / c;
                float a = Mathf.Clamp01(1f - d);
                a *= a;
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        tex.Apply();
        return tex;
    }
}