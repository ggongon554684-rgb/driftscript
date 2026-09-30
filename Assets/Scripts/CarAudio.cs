using UnityEngine;

// Car sounds: layered engine (idle / low / med / high / maxRPM, each with on/off-throttle versions),
// tire screech, wind, and wall crashes.
// Put this on the Car. Any clip slot left empty gets a simple generated placeholder,
// so it makes noise straight away and you can drop in real recordings later.
[RequireComponent(typeof(Rigidbody))]
public class CarAudio : MonoBehaviour
{
    public CarController car;

    [Header("Clips (optional - placeholders are generated when empty)")]
    public AudioClip engineLoop;    // single engine loop, used only if the layered set below is not assigned
    public AudioClip screechLoop;   // tire squeal loop
    public AudioClip windLoop;      // wind / air rush loop
    public AudioClip crashClip;     // one-shot impact

    [Header("Layered engine (optional - overrides Engine Loop)")]
    public AudioClip startupClip;
    public AudioClip idleClip;
    public AudioClip lowOn, lowOff;
    public AudioClip medOn, medOff;
    public AudioClip highOn, highOff;
    public AudioClip maxRpmClip;
    public float layerPitchRange = 0.25f;   // how much each layer bends with revs (lower = less bendy)

    [Header("Volumes")]
    [Range(0f, 1f)] public float engineVolume = 0.6f;
    [Range(0f, 1f)] public float screechVolume = 0.5f;
    [Range(0f, 1f)] public float windVolume = 0.35f;
    [Range(0f, 1f)] public float crashVolume = 0.9f;

    [Header("Engine")]
    public float idlePitch = 0.7f;   // single-loop mode only: pitch at idle
    public float maxPitch = 2.2f;    // single-loop mode only: pitch at the top of each gear
    public int gears = 5;            // fake gearbox: revs climb, drop at each upshift
    public float rpmResponse = 8f;   // how quickly the revs follow

    [Header("Tires")]
    public float screechStartSlide = 1.5f;   // m/s of sideways slip where the squeal starts
    public float screechFullSlide = 6f;      // m/s where it is at full volume

    [Header("Crash")]
    public float minImpulse = 1500f;         // ignore bumps smaller than this (N·s)

    class Layer
    {
        public AudioSource on, off;
        public float center;
    }

    Rigidbody rb;
    AudioSource engine, screech, wind, oneShot;
    Layer[] layers;
    bool layered;
    float onMix;
    float rpm = 0.12f;
    float screechLevel;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (car == null) car = GetComponent<CarController>();

        layered = lowOn != null && medOn != null && highOn != null;
        if (layered)
        {
            layers = new Layer[]
            {
                new Layer { center = 0.10f, on = MakeSource(idleClip != null ? idleClip : medOn, true) },
                new Layer { center = 0.30f, on = MakeSource(lowOn, true),  off = MakeSource(lowOff != null ? lowOff : lowOn, true) },
                new Layer { center = 0.55f, on = MakeSource(medOn, true),  off = MakeSource(medOff != null ? medOff : medOn, true) },
                new Layer { center = 0.80f, on = MakeSource(highOn, true), off = MakeSource(highOff != null ? highOff : highOn, true) },
                new Layer { center = 1.00f, on = MakeSource(maxRpmClip != null ? maxRpmClip : highOn, true) },
            };
        }
        else
        {
            engine = MakeSource(engineLoop != null ? engineLoop : MakeEngineClip(), true);
        }

        screech = MakeSource(screechLoop != null ? screechLoop
                             : MakeNoiseLoop("PlaceholderScreech", 1f, 0.55f, 1100f, 0.25f), true);
        wind = MakeSource(windLoop != null ? windLoop
                          : MakeNoiseLoop("PlaceholderWind", 1.5f, 0.97f, 0f, 0f), true);
        oneShot = MakeSource(null, false);
        oneShot.volume = 1f;

        if (crashClip == null) crashClip = MakeCrashClip();
    }

    void Start()
    {
        if (startupClip != null) oneShot.PlayOneShot(startupClip, engineVolume);
    }

    AudioSource MakeSource(AudioClip clip, bool loop)
    {
        var s = gameObject.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.spatialBlend = 0f;       // 2D: it's the player's own car
        s.loop = loop;
        s.volume = 0f;
        s.clip = clip;
        if (clip != null && loop) s.Play();
        return s;
    }

    void Update()
    {
        if (car == null) return;
        float dt = Time.deltaTime;
        float speedNorm = Mathf.Clamp01(car.Speed / Mathf.Max(1f, car.maxSpeed));

        // ---------- Engine ----------
        // Throttle counts as "driving" only when it pushes the way the car is moving (not braking).
        float drive = car.ForwardSpeed >= -0.5f ? Mathf.Max(0f, car.ThrottleInput)
                                                : Mathf.Max(0f, -car.ThrottleInput);

        // The car tops out around 90% of maxSpeed, so treat that as "full revs"
        float engineNorm = Mathf.Clamp01(car.Speed / (car.maxSpeed * 0.9f));

        float g = engineNorm * gears;
        int gear = Mathf.Min(Mathf.FloorToInt(g), gears - 1);
        float inGear = Mathf.Clamp01(g - gear);                     // 0..1 through the current gear

        // Each gear starts and ends higher than the one before, so speed = higher sound
        float gearLow  = Mathf.Lerp(0.30f, 0.60f, engineNorm);
        float gearHigh = Mathf.Lerp(0.55f, 0.95f, engineNorm);
        float gearRpm  = Mathf.Lerp(gearLow, gearHigh, inGear);

        // Idle when stopped, blend into the gear revs as the car starts moving
        float rpmTarget = Mathf.Lerp(0.12f, gearRpm, Mathf.Clamp01(car.Speed / 3f));

        // small rev bump when pressing throttle at low speed (launching)
        rpmTarget += drive * 0.25f * Mathf.Clamp01(1f - car.Speed / 8f);

        rpmTarget = Mathf.Clamp01(rpmTarget);
        rpm = Mathf.Lerp(rpm, rpmTarget, 1f - Mathf.Exp(-rpmResponse * dt));

        if (layered)
        {
            UpdateLayers(dt, drive);
        }
        else
        {
            engine.pitch = Mathf.Lerp(idlePitch, maxPitch, rpm);
            engine.volume = engineVolume * (0.45f + 0.55f * Mathf.Clamp01(0.5f * rpm + 0.6f * drive));
        }

        // ---------- Tire screech ----------
        float maxSlide = 0f;
        for (int i = 0; i < 4; i++)
            if (car.grounded[i]) maxSlide = Mathf.Max(maxSlide, car.slide[i]);

        float target = Mathf.InverseLerp(screechStartSlide, screechFullSlide, maxSlide)
                       * Mathf.Lerp(0.4f, 1f, Mathf.Clamp01(car.Speed / 4f));
        screechLevel = Mathf.MoveTowards(screechLevel, target, (target > screechLevel ? 8f : 4f) * dt);
        screech.volume = screechVolume * screechLevel;
        screech.pitch = Mathf.Lerp(0.85f, 1.15f, screechLevel);

        // ---------- Wind ----------
        wind.volume = windVolume * speedNorm * speedNorm;
        wind.pitch = 0.8f + 0.6f * speedNorm;
    }

    // Crossfades the engine layers by rpm, and swaps between on-throttle and off-throttle recordings.
    void UpdateLayers(float dt, float drive)
    {
        onMix = Mathf.MoveTowards(onMix, drive > 0.05f ? 1f : 0f, 5f * dt);

        foreach (var L in layers)
        {
            float w = Mathf.Clamp01(1f - Mathf.Abs(rpm - L.center) / 0.25f);   // crossfade weight
            float vol = engineVolume * w;
            float pitch = 1f + (rpm - L.center) * layerPitchRange;

            L.on.pitch = pitch;
            if (L.off != null)
            {
                L.off.pitch = pitch;
                L.on.volume = vol * onMix;
                L.off.volume = vol * (1f - onMix);
            }
            else
            {
                L.on.volume = vol;
            }
        }
    }

    // Wall / obstacle hits. Volume follows how hard the hit was.
    void OnCollisionEnter(Collision c)
    {
        if (c.contactCount == 0) return;
        if (Vector3.Dot(c.GetContact(0).normal, Vector3.up) > 0.7f) return;   // ignore ground and kerbs

        float impulse = c.impulse.magnitude;
        if (impulse < minImpulse) return;

        float strength = Mathf.Clamp01(impulse / (rb.mass * 15f));
        oneShot.pitch = Random.Range(0.9f, 1.1f);
        oneShot.PlayOneShot(crashClip, crashVolume * Mathf.Lerp(0.3f, 1f, strength));
    }

    // ------------------------------------------------------------------ generated placeholders

    // A buzzy synthetic engine note (90 Hz plus harmonics) that loops cleanly.
    static AudioClip MakeEngineClip()
    {
        const int rate = 44100;
        const int period = 490;    // samples per cycle = 90 Hz
        const int cycles = 20;
        int n = period * cycles;
        var data = new float[n];
        float peak = 0.0001f;

        for (int i = 0; i < n; i++)
        {
            float t = i / (float)period;   // time in cycles
            float s = 0f;
            for (int h = 1; h <= 12; h++)
                s += Mathf.Sin(2f * Mathf.PI * h * t) / Mathf.Pow(h, 1.2f);

            // uneven "firing" wobble: 10 full wobbles per loop so the loop point is seamless
            s *= 1f + 0.3f * Mathf.Sin(2f * Mathf.PI * i / n * 10f);
            data[i] = s;
            peak = Mathf.Max(peak, Mathf.Abs(s));
        }
        for (int i = 0; i < n; i++) data[i] *= 0.8f / peak;

        var clip = AudioClip.Create("PlaceholderEngine", n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // Filtered noise (+ optional steady tone) with a crossfaded end so it loops without a click.
    // smooth: 0 = hissy, 0.97 = low rumbling wind.
    static AudioClip MakeNoiseLoop(string name, float seconds, float smooth, float toneHz, float toneAmount)
    {
        const int rate = 44100;
        const int fade = 2048;
        int total = Mathf.RoundToInt(seconds * rate);
        var data = new float[total];
        var rng = new System.Random(12345);
        float y = 0f, peak = 0.0001f;

        for (int i = 0; i < total; i++)
        {
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            y = y * smooth + noise * (1f - smooth);
            float s = y + toneAmount * Mathf.Sin(2f * Mathf.PI * toneHz * i / rate);
            data[i] = s;
            peak = Mathf.Max(peak, Mathf.Abs(s));
        }
        for (int i = 0; i < total; i++) data[i] *= 0.8f / peak;

        int len = total - fade;
        for (int i = 0; i < fade; i++)
        {
            float t = i / (float)fade;
            data[i] = data[i] * t + data[len + i] * (1f - t);
        }

        var trimmed = new float[len];
        System.Array.Copy(data, trimmed, len);
        var clip = AudioClip.Create(name, len, 1, rate, false);
        clip.SetData(trimmed, 0);
        return clip;
    }

    // A short thump + crunch that dies away.
    static AudioClip MakeCrashClip()
    {
        const int rate = 44100;
        int n = rate * 7 / 10;
        var data = new float[n];
        var rng = new System.Random(777);
        float lp = 0f, peak = 0.0001f;

        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Exp(-7f * t);
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            lp = lp * 0.5f + noise * 0.5f;
            float thump = Mathf.Sin(2f * Mathf.PI * 55f * t);
            float s = (lp * 0.7f + thump * 0.9f) * env;
            data[i] = s;
            peak = Mathf.Max(peak, Mathf.Abs(s));
        }
        for (int i = 0; i < n; i++) data[i] *= 0.9f / peak;

        var clip = AudioClip.Create("PlaceholderCrash", n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}