using UnityEngine;
using UnityEngine.InputSystem;

// Speedometer dial + physics panel (bottom-right). Scales with screen height so it stays readable
// at any Game view size. Put this on the Car. F1 = hide/show, F2 = reset the peak impact.
//
// Panel rows (each shows its formula):
//   Kinetic energy   KE = 1/2 m v^2
//   Net force        F = m a
//   Momentum         p = m v
//   Centripetal      Fc = m v^2 / r      (bar: force needed vs. friction available)
//   Friction         f <= mu N           (friction used vs. the maximum the tires can give)
//   Tire state       static (gripping) vs. kinetic (sliding) friction
//   Impulse          J = change in p     (last wall hit, and how much kinetic energy was lost)
[RequireComponent(typeof(Rigidbody))]
public class DriftHUD : MonoBehaviour
{
    public CarController car;
    public bool showHUD = true;
    [Tooltip("Higher = snappier numbers, lower = smoother numbers")]
    public float smoothing = 8f;
    [Tooltip("Speed labels on the dial every N km/h")]
    public float gaugeStepKmh = 20f;
    [Tooltip("Extra size multiplier for the whole HUD")]
    public float uiScale = 0.7f;

    [Header("Tire state")]
    [Tooltip("Sideways slip speed (m/s) above which the tires count as sliding (kinetic friction)")]
    public float slidingThreshold = 2.5f;

    [Header("Impact (wall hits)")]
    [Tooltip("Ignore hits smaller than this impulse (N·s). Filters out bumps and light scrapes.")]
    public float minImpulse = 1500f;
    [Tooltip("Seconds the last impact stays highlighted on the HUD")]
    public float impactHoldTime = 5f;
    [Tooltip("Seconds after a hit before the 'speed after' is measured")]
    public float afterImpactDelay = 0.1f;

    const float DialSize = 340f;     // virtual pixels (layout is 1080 tall, then scaled to the screen)
    const int TexSize = 512;
    const float StartAngle = -135f;  // needle angle at 0 km/h (0 = straight up, clockwise positive)
    const float Sweep = 270f;
    const float G = 9.81f;

    Rigidbody rb;
    Vector3 prevVel;
    float speed, accelLong, turnRate, centripetal;

    // impact data
    float lastImpulse, peakImpulse, lastImpactTime = -999f;
    float preImpactSpeed, postImpactSpeed = -1f;
    bool awaitingAfter;
    public float LastImpulse => lastImpulse;   // other scripts (e.g. crash sounds) can read this

    Texture2D dialTex, hubTex;
    float builtMaxKmh = -1f;
    float uiS = 1f;

    GUIStyle labelStyle, formulaStyle, valueStyle, titleStyle, tickStyle, bigStyle, unitStyle;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (car == null) car = GetComponent<CarController>();
        hubTex = BuildCircle(64);
    }

    void Start()
    {
        prevVel = rb.linearVelocity;
    }

    void OnDestroy()
    {
        if (dialTex != null) Destroy(dialTex);
        if (hubTex != null) Destroy(hubTex);
    }

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame)
            showHUD = !showHUD;

        if (Keyboard.current != null && Keyboard.current.f2Key.wasPressedThisFrame)
            peakImpulse = 0f;   // F2 resets the peak impact
    }

    // Collision.impulse is the total impulse (N·s) the physics engine applied to resolve the hit.
    // impulse J = mass * change in velocity = change in momentum.
    void OnCollisionEnter(Collision c)
    {
        if (c.contactCount == 0) return;

        // Ignore mostly-flat surfaces (ground, kerbs): we only want wall / obstacle crashes
        if (Vector3.Dot(c.GetContact(0).normal, Vector3.up) > 0.7f) return;

        float impulse = c.impulse.magnitude;
        if (impulse < minImpulse) return;

        lastImpulse = impulse;
        lastImpactTime = Time.time;
        peakImpulse = Mathf.Max(peakImpulse, impulse);

        // prevVel was recorded at the end of the last physics step, so it is the speed just before the hit
        preImpactSpeed = prevVel.magnitude;
        postImpactSpeed = -1f;
        awaitingAfter = true;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        Vector3 v = rb.linearVelocity;
        float k = 1f - Mathf.Exp(-smoothing * dt);

        speed = Mathf.Lerp(speed, v.magnitude, k);

        // Acceleration along the car's nose (ignore vertical bumps)
        Vector3 worldAccel = (v - prevVel) / dt;
        worldAccel.y = 0f;
        Vector3 local = transform.InverseTransformDirection(worldAccel);
        accelLong = Mathf.Lerp(accelLong, local.z, k);

        // Centripetal acceleration = speed * how fast the travel direction rotates.
        // Uses the velocity vector (not the nose) so it's correct while drifting.
        Vector3 pv = new Vector3(prevVel.x, 0f, prevVel.z);
        Vector3 cv = new Vector3(v.x, 0f, v.z);
        float targetTurn = 0f;
        if (cv.magnitude > 2f && pv.magnitude > 2f)
            targetTurn = Vector3.SignedAngle(pv, cv, Vector3.up) * Mathf.Deg2Rad / dt;
        turnRate = Mathf.Lerp(turnRate, targetTurn, k);
        centripetal = Mathf.Lerp(centripetal, speed * Mathf.Abs(turnRate), k);

        // Measure the speed a moment after a hit, to work out how much kinetic energy was lost
        if (awaitingAfter && Time.time - lastImpactTime >= afterImpactDelay)
        {
            postImpactSpeed = v.magnitude;
            awaitingAfter = false;
        }

        prevVel = v;
    }

    void OnGUI()
    {
        if (!showHUD || rb == null) return;
        if (Event.current.type != EventType.Repaint) return;   // everything uses fixed rects, so only the Repaint pass is needed

        EnsureStyles();
        EnsureDial();

        uiS = Screen.height / 1080f * uiScale;
        float vw = Screen.width / uiS;
        float vh = Screen.height / uiS;

        Matrix4x4 saved = GUI.matrix;
        Color savedColor = GUI.color, savedContent = GUI.contentColor;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiS, uiS, 1f));
        GUI.color = Color.white;
        GUI.contentColor = Color.white;

        DrawDial(vw, vh);
        DrawPanel(vw, vh);

        GUI.matrix = saved;
        GUI.color = savedColor;
        GUI.contentColor = savedContent;
    }

    // ------------------------------------------------------------------ dial

    void DrawDial(float vw, float vh)
    {
        float margin = 40f;
        float cx = vw - margin - DialSize * 0.5f;
        float cy = vh - margin - DialSize * 0.5f;
        float ratio = DialSize / TexSize;

        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(cx - DialSize * 0.5f, cy - DialSize * 0.5f, DialSize, DialSize), dialTex);

        // Numbers around the dial
        int majors = Mathf.Max(1, Mathf.RoundToInt(builtMaxKmh / gaugeStepKmh));
        int labelEvery = majors > 10 ? 2 : 1;
        float labelR = (TexSize * 0.5f - 60f) * ratio;
        for (int i = 0; i <= majors; i += labelEvery)
        {
            float ang = (StartAngle + Sweep * i / majors) * Mathf.Deg2Rad;
            float px = cx + Mathf.Sin(ang) * labelR;
            float py = cy - Mathf.Cos(ang) * labelR;
            GUI.Label(new Rect(px - 26f, py - 12f, 52f, 24f), (i * gaugeStepKmh).ToString("0"), tickStyle);
        }

        // Needle (RotateAroundPivot wants the pivot in real screen pixels, so multiply by the UI scale)
        float kmh = speed * 3.6f;
        float needleAngle = StartAngle + Sweep * Mathf.Clamp01(kmh / builtMaxKmh);
        float len = (TexSize * 0.5f - 18f) * ratio;

        Matrix4x4 m = GUI.matrix;
        GUIUtility.RotateAroundPivot(needleAngle, new Vector2(cx * uiS, cy * uiS));
        GUI.color = new Color(1f, 0.25f, 0.2f);
        GUI.DrawTexture(new Rect(cx - 2.5f, cy - len, 5f, len + 24f), Texture2D.whiteTexture);
        GUI.matrix = m;

        GUI.color = new Color(0.92f, 0.92f, 0.96f);
        GUI.DrawTexture(new Rect(cx - 13f, cy - 13f, 26f, 26f), hubTex);
        GUI.color = Color.white;

        // Digital speed
        GUI.Label(new Rect(cx - 100f, cy + 34f, 200f, 72f), Mathf.RoundToInt(kmh).ToString(), bigStyle);
        GUI.Label(new Rect(cx - 100f, cy + 102f, 200f, 26f), "km/h", unitStyle);
    }

    // ------------------------------------------------------------------ panel

    void DrawPanel(float vw, float vh)
    {
        const float w = 470f, pad = 18f, rowH = 58f, titleH = 44f;
        const int rows = 7;
        float h = titleH + rows * rowH + pad;
        float x = vw - 40f - DialSize - 28f - w;
        float y = vh - 40f - h;

        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
        GUI.color = Color.white;

        float ix = x + pad;
        float iw = w - pad * 2f;
        float ry = y + 12f;

        // Title row + drift angle
        Text(new Rect(ix, ry, iw * 0.5f, 28f), "PHYSICS", titleStyle);
        float drift = car != null ? car.DriftAngle : 0f;
        Color driftCol = drift > 10f ? new Color(1f, 0.65f, 0.2f) : new Color(0.75f, 0.78f, 0.85f);
        valueStyle.normal.textColor = driftCol;
        Text(new Rect(ix + iw * 0.4f, ry, iw * 0.6f, 28f), $"Drift {drift:0}°", valueStyle);
        valueStyle.normal.textColor = Color.white;
        ry += titleH - 12f;

        float mass = rb.mass;
        float maxSpd = car != null ? car.maxSpeed : 40f;
        float mu = car != null ? car.grip : 1.1f;
        float gripUse = car != null ? car.GripUsage : 0f;
        float maxFriction = car != null ? car.MaxFriction : 0f;
        float usedFriction = gripUse * maxFriction;

        // --- Kinetic energy ---
        float ke = 0.5f * mass * speed * speed;
        float keMax = 0.5f * mass * maxSpd * maxSpd;
        Row(ref ry, ix, iw, "Kinetic energy", "KE = ½ m v²",
            $"{ke / 1000f:0.0} kJ",
            ke / keMax, new Color(0.55f, 0.9f, 1f), false);

        // --- Net force (Newton's 2nd law), along the car's nose ---
        float netForce = mass * accelLong;
        Color accelCol = accelLong >= 0f ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.45f, 0.35f);
        Row(ref ry, ix, iw, "Net force", "F = m a",
            $"{netForce:+#,0;-#,0;0} N   ({accelLong:+0.0;-0.0;0.0} m/s²)",
            accelLong / G, accelCol, true);

        // --- Momentum ---
        float momentum = mass * speed;
        Row(ref ry, ix, iw, "Momentum", "p = m v",
            $"{momentum:N0} kg·m/s",
            momentum / (mass * maxSpd), new Color(0.75f, 0.6f, 1f), false);

        // --- Centripetal force: supplied by tire friction, so its limit is mu m g ---
        float fc = mass * centripetal;
        bool turning = speed > 3f && centripetal > 0.5f;
        float radius = turning ? speed * speed / centripetal : 0f;
        string cText = turning ? $"{fc:N0} N   r = {radius:0} m" : "0 N   (straight)";
        Row(ref ry, ix, iw, "Centripetal force", "Fc = m v² / r",
            cText,
            centripetal / (mu * G), new Color(1f, 0.8f, 0.3f), false);

        // --- Friction: tires can give at most mu * N ---
        Color gripCol = gripUse < 0.8f ? new Color(0.45f, 1f, 0.45f)
                      : gripUse < 0.95f ? new Color(1f, 0.85f, 0.25f)
                      : new Color(1f, 0.3f, 0.3f);
        Row(ref ry, ix, iw, "Friction", $"f ≤ μN     μ = {mu:0.00}",
            $"{usedFriction:N0} / {maxFriction:N0} N",
            gripUse, gripCol, false);

        // --- Tire state: static friction while gripping, kinetic friction (lower) once sliding ---
        float maxSlide = 0f;
        if (car != null)
            for (int i = 0; i < 4; i++)
                if (car.grounded[i]) maxSlide = Mathf.Max(maxSlide, car.slide[i]);
        bool sliding = maxSlide > slidingThreshold;
        Row(ref ry, ix, iw, "Tire state", "μ static > μ kinetic",
            sliding ? "KINETIC (sliding)" : "STATIC (gripping)",
            0f, sliding ? new Color(1f, 0.3f, 0.3f) : new Color(0.45f, 1f, 0.45f), false, false);

        // --- Impulse of the last wall hit, and the kinetic energy it destroyed (inelastic collision) ---
        float age = Time.time - lastImpactTime;
        if (age < impactHoldTime)
        {
            float fade = 1f - age / impactHoldTime;
            Color hit = Color.Lerp(new Color(1f, 0.7f, 0.3f), new Color(1f, 0.2f, 0.2f), fade);

            string lost = "KE lost …";
            if (postImpactSpeed >= 0f && preImpactSpeed > 0.5f)
            {
                float frac = Mathf.Clamp01(1f - (postImpactSpeed * postImpactSpeed) / (preImpactSpeed * preImpactSpeed));
                lost = $"KE lost {frac * 100f:0}%";
            }
            Row(ref ry, ix, iw, "IMPULSE", "J = Δp = m Δv",
                $"{lastImpulse:N0} N·s   {lost}",
                lastImpulse / (mass * 20f), hit, false);
        }
        else
        {
            Row(ref ry, ix, iw, "Impulse", "J = Δp = m Δv",
                peakImpulse > 0f ? $"peak {peakImpulse:N0} N·s" : "none yet",
                0f, new Color(1f, 0.3f, 0.3f), false);
        }
    }

    // Draws text in the style's own color. contentColor must be white here, otherwise the two
    // colors multiply and the text comes out darker than intended.
    static void Text(Rect r, string s, GUIStyle st)
    {
        if (st == null) st = GUI.skin.label;   // never crash on a missing style
        Color oldContent = GUI.contentColor;
        GUI.contentColor = Color.white;
        GUI.Label(r, s, st);
        GUI.contentColor = oldContent;
    }

    // One row: name (left), value (right), formula in small text underneath, then an optional bar.
    // centered = the bar grows left/right from the middle.
    void Row(ref float y, float x, float w, string name, string formula, string value,
             float fill, Color col, bool centered, bool showBar = true)
    {
        Text(new Rect(x, y, w * 0.4f, 28f), name, labelStyle);
        Text(new Rect(x + w * 0.3f, y, w * 0.7f, 28f), value, valueStyle);
        Text(new Rect(x, y + 27f, w, 20f), formula, formulaStyle);

        if (showBar)
        {
            Rect bar = new Rect(x, y + 48f, w, 6f);
            GUI.color = new Color(1f, 1f, 1f, 0.15f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);

            GUI.color = col;
            if (centered)
            {
                float half = w * 0.5f;
                float len = Mathf.Clamp(fill, -1f, 1f) * half;
                Rect r = len >= 0f ? new Rect(x + half, bar.y, len, bar.height)
                                   : new Rect(x + half + len, bar.y, -len, bar.height);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = new Color(1f, 1f, 1f, 0.6f);
                GUI.DrawTexture(new Rect(x + half - 1f, bar.y - 2f, 2f, bar.height + 4f), Texture2D.whiteTexture);
            }
            else
            {
                GUI.DrawTexture(new Rect(x, bar.y, w * Mathf.Clamp01(fill), bar.height), Texture2D.whiteTexture);
            }
        }

        GUI.color = Color.white;
        y += 58f;
    }

    // ------------------------------------------------------------------ styles and textures

    void EnsureStyles()
    {
        if (labelStyle != null && formulaStyle != null && valueStyle != null && titleStyle != null
            && tickStyle != null && bigStyle != null && unitStyle != null) return;
        labelStyle = MakeStyle(19, FontStyle.Normal, TextAnchor.UpperLeft, new Color(0.85f, 0.87f, 0.93f));
        formulaStyle = MakeStyle(15, FontStyle.Italic, TextAnchor.UpperLeft, new Color(0.6f, 0.64f, 0.72f));
        valueStyle = MakeStyle(19, FontStyle.Bold, TextAnchor.UpperRight, Color.white);
        titleStyle = MakeStyle(22, FontStyle.Bold, TextAnchor.UpperLeft, Color.white);
        tickStyle = MakeStyle(16, FontStyle.Normal, TextAnchor.MiddleCenter, new Color(0.88f, 0.9f, 0.95f));
        bigStyle = MakeStyle(64, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);
        unitStyle = MakeStyle(20, FontStyle.Normal, TextAnchor.MiddleCenter, new Color(0.65f, 0.68f, 0.75f));
    }

    static GUIStyle MakeStyle(int size, FontStyle fs, TextAnchor anchor, Color col)
    {
        var s = new GUIStyle(GUI.skin.label)
        {
            fontSize = size,
            fontStyle = fs,
            alignment = anchor,
            wordWrap = false
        };
        s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = col;
        s.onNormal.textColor = s.onHover.textColor = s.onActive.textColor = s.onFocused.textColor = col;
        return s;
    }

    // Rebuilds the dial if the car's max speed changed.
    void EnsureDial()
    {
        float carMaxKmh = (car != null ? car.maxSpeed : 40f) * 3.6f;
        float wanted = Mathf.Max(gaugeStepKmh, Mathf.Ceil(carMaxKmh / gaugeStepKmh) * gaugeStepKmh);
        if (dialTex != null && Mathf.Approximately(wanted, builtMaxKmh)) return;

        if (dialTex != null) Destroy(dialTex);
        dialTex = BuildDial(wanted);
        builtMaxKmh = wanted;
    }

    static float Edge(float v) => Mathf.Clamp01(v + 0.5f);   // soft edge: v = distance inside the shape, in pixels

    static Color Over(Color under, Color top)
    {
        float a = top.a + under.a * (1f - top.a);
        if (a <= 0.0001f) return new Color(0f, 0f, 0f, 0f);
        float ia = under.a * (1f - top.a);
        return new Color((top.r * top.a + under.r * ia) / a,
                         (top.g * top.a + under.g * ia) / a,
                         (top.b * top.a + under.b * ia) / a, a);
    }

    Texture2D BuildDial(float maxKmh)
    {
        int size = TexSize;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        var px = new Color[size * size];

        float c = (size - 1) * 0.5f;
        float R = size * 0.5f - 2f;
        int majors = Mathf.Max(1, Mathf.RoundToInt(maxKmh / gaugeStepKmh));
        const int minors = 4;
        float majorStepDeg = Sweep / majors;
        float minorStepDeg = majorStepDeg / minors;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - c, dy = y - c;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;   // 0 = up, clockwise positive

                // dark disc + thin rim
                Color col = new Color(0.03f, 0.03f, 0.04f, 0.7f * Edge(R - d));
                col = Over(col, new Color(1f, 1f, 1f, 0.35f * Edge(2f - Mathf.Abs(d - (R - 3f)))));

                float t = (a - StartAngle) / Sweep;   // 0..1 along the sweep
                if (t >= -0.02f && t <= 1.02f)
                {
                    // coloured arc (blue -> red near the top end)
                    if (t >= 0f && t <= 1f)
                    {
                        Color arc = Color.Lerp(new Color(0.55f, 0.8f, 1f), new Color(1f, 0.25f, 0.2f),
                                               Mathf.InverseLerp(0.75f, 1f, t));
                        arc.a = Edge(3f - Mathf.Abs(d - (R - 16f)));
                        col = Over(col, arc);
                    }

                    // minor ticks
                    float n = t * majors * minors;
                    float nPix = (n - Mathf.Round(n)) * minorStepDeg * Mathf.Deg2Rad * d;
                    float minorA = Edge(0.9f - Mathf.Abs(nPix)) * Edge(d - (R - 26f)) * Edge((R - 8f) - d) * 0.7f;
                    if (minorA > 0f) col = Over(col, new Color(1f, 1f, 1f, minorA));

                    // major ticks
                    float m = t * majors;
                    float mPix = (m - Mathf.Round(m)) * majorStepDeg * Mathf.Deg2Rad * d;
                    float majorA = Edge(1.8f - Mathf.Abs(mPix)) * Edge(d - (R - 34f)) * Edge((R - 8f) - d);
                    if (majorA > 0f) col = Over(col, new Color(1f, 1f, 1f, majorA));
                }

                px[y * size + x] = col;
            }
        }

        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static Texture2D BuildCircle(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                px[y * size + x] = new Color(1f, 1f, 1f, Edge(size * 0.5f - 1f - d));
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }
}