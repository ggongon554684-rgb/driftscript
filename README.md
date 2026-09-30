# Drift Game Backend Report

## 1. Purpose and architecture

The project is a Unity driving game built around an arcade-simulation car. The backend is split into one physics controller and several presentation systems:

- `CarController` is the source of truth for movement, suspension, tire forces, grip, drifting, braking, and driving input.
- `DriftHUD` and `TelemetryHUD` read physics values and display calculations.
- `CarAudio` converts speed, throttle, tire slip, and collision impulse into sound.
- `DriftSmoke` converts tire slip into particle emission.
- `WheelVisuals` positions, steers, and spins the visible tire meshes.
- `FollowCamera` follows the car and looks at it from behind.
- `SpeedEffects` changes field of view and motion blur as speed increases.
- `BackgroundMusic` plays a looping music track.

Most gameplay calculations run in `FixedUpdate`, which matches Unity's fixed-time Rigidbody simulation. Visual, audio, and camera updates run in `Update` or `LateUpdate` so they can react smoothly to the latest physics state.

## 2. Main physics class: CarController

File: `Assets/Scripts/CarController.cs`

`CarController` inherits from `MonoBehaviour` and requires a `Rigidbody`. It models a rear-wheel-drive car using four downward raycasts instead of Unity WheelColliders. The four wheels are ordered as front-left, front-right, rear-left, rear-right.

### Main responsibilities

1. Read keyboard and gamepad input.
2. Calculate steering, throttle, braking, and reverse behavior.
3. Detect whether each wheel is touching the ground.
4. Calculate suspension load and aerodynamic downforce.
5. Calculate each tire's longitudinal and lateral forces.
6. Limit the combined tire force using a friction circle.
7. Apply forces to the Rigidbody at the wheel positions.
8. Publish values used by the HUD, audio, smoke, and wheel visuals.

### Initialization: `Awake`

- Gets the Rigidbody and enables interpolation and continuous collision detection.
- Uses the `Ground` layer if no ground mask was assigned.
- Stores the four wheel marker transforms.
- Disables the component if a wheel marker is missing.
- Calculates the center of mass as the average wheel position plus `comHeightAboveAxle`.

Lowering the center of mass makes the car less likely to roll over. The Rigidbody still receives normal Unity gravity and collision responses.

### Input: `Update`

Keyboard controls are:

- `W`/up arrow: throttle
- `S`/down arrow: brake or reverse
- `A`/left arrow and `D`/right arrow: steering
- `Space`: handbrake
- `R`: reset the car upright

Gamepad input uses the right and left triggers for throttle/brake, the left stick for steering, the south button for the handbrake, and the north button for reset. When both keyboard and gamepad are connected, the input with the greater absolute value is used for throttle and steering.

### Speed and drift angle: `FixedUpdate`

The velocity comes from `Rigidbody.linearVelocity`.

```text
speed = |velocity|
forwardSpeed = velocity dot carForward
```

The total speed includes sideways movement. `ForwardSpeed` only measures movement along the car's nose, which is important for distinguishing forward driving from a sideways drift.

The drift angle is the absolute signed angle between the car's forward direction and its horizontal travel direction. It is set to zero below 2 m/s to avoid noisy angles while nearly stationary.

### Steering calculation

The maximum steering angle decreases as speed approaches `maxSpeed`:

```text
maxSteer = lerp(steerAngleLowSpeed, steerAngleHighSpeed,
                clamp01(speed / maxSpeed))
```

This gives large steering authority at low speed and more stable steering at high speed. The actual steering angle moves toward the requested angle at `steerRate` degrees per second, preventing instant visual and physics changes.

When the car is moving forward and its drift angle exceeds `driftAssistStartAngle`, countersteer assistance is added:

```text
assist = inverseLerp(startAngle, fullAngle, abs(driftAngle)) * driftAssist
steerTarget += clamp(signedDrift, -maxSteer, maxSteer) * assist
```

The result is clamped to the available steering range.

### Throttle, reverse, and braking

An input opposite to the current forward motion is treated as braking. Otherwise, it produces engine force:

```text
driveTotal = throttle * engineForce *
             clamp01(1 - (abs(forwardSpeed) / limit)^driveFadePower)
```

The forward limit is `maxSpeed`; reverse uses 30% of that limit. This fades engine force toward the top speed rather than abruptly stopping acceleration.

Braking uses `brakeForce` when the player presses the opposite direction. With no throttle, `coastBrake` supplies a smaller slowing force.

### Suspension and wheel-ground detection

Each wheel casts a ray along `-transform.up` with length:

```text
rayLength = wheelRadius + suspensionTravel
```

If the ray hits the ground layer, the script calculates spring length and compression:

```text
springLength = hitDistance - wheelRadius
compression = suspensionTravel - springLength
```

The spring and damper load is:

```text
load = max(0, compression * springStiffness - springVelocity * damping)
```

The load is applied upward at the wheel marker. Negative load is discarded so the suspension does not pull the car toward the ground.

### Aerodynamic downforce

Downforce grows with the square of speed and is divided equally among the four wheels:

```text
downforcePerWheel = downforce * speed^2 / 4
tireLoad = suspensionLoad + downforcePerWheel
```

Because the maximum tire force is based on tire load, downforce increases available grip at high speed.

### Tire coordinate system and slip

The front wheel direction is rotated by the steering angle. Each wheel direction is projected onto the ground plane so it follows slopes. The script then measures the contact-point velocity in the tire's forward and sideways axes:

```text
vLong = pointVelocity dot wheelForward
vLat  = pointVelocity dot wheelRight
```

The slip angle is:

```text
slipAngle = atan2(vLat, max(abs(vLong), 1.5))
```

The 1.5 m/s lower bound keeps the calculation stable when a wheel is nearly stopped.

### Tire grip and lateral force

The available force for one tire is:

```text
maxTireForce = mu * tireLoad
```

`mu` is `grip`. While the handbrake is held, rear-wheel grip is multiplied by `handbrakeRearGrip`, making the rear easier to slide.

The lateral force uses a simplified, shaped tire curve:

```text
lateralForce = -maxTireForce * sin(tireC * atan(tireB * slipAngle))
```

`tireB` controls how quickly force rises with slip. `tireC` controls the curve shape and allows force to fall after the peak, representing a sliding tire.

The force is also limited to the amount needed to stop the current lateral velocity during this physics step:

```text
maxStoppingForce = wheelMass * abs(vLat) / dt
```

This prevents excessive corrective forces and reduces jitter.

### Longitudinal tire force

The rear wheels receive half of the total drive force each. Braking is distributed across all four wheels. Rear handbrake braking adds extra braking force to the rear pair.

Braking is limited so a wheel does not reverse its contact-point velocity in one physics step:

```text
brakeForceUsed = min(requestedBrake, wheelMass * abs(vLong) / dt)
longitudinalForce -= sign(vLong) * brakeForceUsed
```

### Friction circle

Acceleration and cornering share the same tire grip budget. The combined requested force is:

```text
requestedMagnitude = sqrt(longitudinalForce^2 + lateralForce^2)
```

If it exceeds the tire limit, both components are scaled equally:

```text
scale = maxTireForce / requestedMagnitude
longitudinalForce *= scale
lateralForce *= scale
```

This is the key drift behavior: a tire using much of its grip to accelerate or brake has less grip available for cornering.

The final force is transformed back into world space and applied at a point slightly above the ground contact:

```text
tireForce = wheelForward * longitudinalForce
          + wheelRight * lateralForce
AddForceAtPosition(tireForce, contactPoint + up * forceHeight)
```

Applying the force above the ground creates controllable body rotation and weight-transfer behavior.

### Slide amount, grip telemetry, and resistance

The tire slide value starts as sideways slip speed. Extra slide is added when requested force exceeds available grip, representing wheelspin or locked braking:

```text
slide = abs(vLat)
if requestedMagnitude > maxTireForce:
    slide += (requestedMagnitude / maxTireForce - 1) * 6
```

The controller publishes:

```text
TireForceTotal = sum of applied tire forces
MaxFriction    = sum of (mu * tireLoad)
GripUsage      = clamp01(sum of tire force magnitudes / MaxFriction)
```

Air drag and rolling resistance are applied to the whole car:

```text
resistance = -velocity * speed * dragCoefficient
           - velocity * rollingResistance
```

The first term grows approximately with speed squared; the second grows linearly with speed.

### Anti-roll bars

`ApplyAntiRoll` compares spring lengths on the left and right side of an axle. It applies an upward force to the more-compressed side and an equal downward force to the other side:

```text
force = (rightSpringLength - leftSpringLength) * antiRollStiffness
```

The front and rear axles are handled separately. This reduces body roll without changing the wheel positions themselves.

### Reset and debug display

`ResetCar` clears linear and angular velocity, resets steering, raises the car by one meter, and preserves its current yaw while removing pitch and roll. `OnGUI` displays speed and drift angle in the top-left when `showDebugGUI` is enabled.

## 3. DriftHUD

File: `Assets/Scripts/DriftHUD.cs`

`DriftHUD` is the main physics dashboard. It requires a Rigidbody and normally sits on the car. It smooths speed, forward acceleration, and centripetal acceleration, then draws a speedometer and seven physics rows.

### Measurements

- Kinetic energy: `KE = 0.5 * mass * speed^2`
- Net longitudinal force: `F = mass * acceleration`
- Momentum: `p = mass * speed`
- Centripetal force: `Fc = mass * centripetalAcceleration`
- Turn radius: `r = speed^2 / centripetalAcceleration`
- Tire friction: `used = GripUsage * MaxFriction`
- Tire limit: `maximum = mu * normalLoad`
- Collision impulse: `J = change in momentum = mass * change in velocity`

The collision handler ignores mostly upward contact normals, filtering out ground and kerb contacts. It records impulses above `minImpulse`, stores the pre-impact speed, and measures the post-impact speed after `afterImpactDelay`.

The percentage of kinetic energy lost is calculated from speed:

```text
lostFraction = clamp01(1 - postSpeed^2 / preSpeed^2)
```

The HUD uses `F1` to toggle visibility and `F2` to reset the peak impact value. It generates its dial texture and circular hub at runtime, builds GUI styles once, and scales the complete layout relative to a 1080-pixel virtual height.

## 4. TelemetryHUD

File: `Assets/Scripts/TelemetryHUD.cs`

`TelemetryHUD` is a smaller alternative telemetry panel. It uses the Rigidbody directly and reports speed, longitudinal/lateral acceleration, momentum, kinetic energy, turn radius, centripetal acceleration, centripetal force, tire force, grip usage, and peak values.

Acceleration is estimated from velocity change:

```text
acceleration = (currentVelocity - previousVelocity) / fixedDeltaTime
```

The vertical component is removed, and the result is transformed into the car's local coordinates. Turn rate is derived from the signed angle between consecutive velocity vectors. Centripetal acceleration is then:

```text
centripetalAcceleration = speed * abs(turnRate)
turnRadius = speed / abs(turnRate)
```

At low turn rate the radius is displayed as `straight`. The HUD smooths values with frame-rate-independent interpolation:

```text
k = 1 - exp(-smoothing * dt)
smoothed = lerp(smoothed, target, k)
```

## 5. Physics metric calculation ownership

These values are split between the car simulation and the HUDs. `CarController` produces the tire forces and wheel-slip data that affect the car; the HUDs derive displayed metrics from Rigidbody state and those published values.

- **Acceleration:** `DriftHUD` and `TelemetryHUD` estimate acceleration from the change in Rigidbody velocity over `Time.fixedDeltaTime`. `DriftHUD` reports longitudinal acceleration; `TelemetryHUD` reports longitudinal and lateral acceleration.
- **Kinetic energy:** `DriftHUD` and `TelemetryHUD` calculate `0.5 * mass * speed^2` for display.
- **Momentum:** `DriftHUD` and `TelemetryHUD` calculate mass multiplied by Rigidbody velocity and display its magnitude.
- **Centripetal force:** `DriftHUD` and `TelemetryHUD` derive centripetal acceleration from the change in travel direction, then calculate force as `mass * centripetalAcceleration`.
- **Tire friction and grip:** `CarController` calculates tire force limits (`grip * tireLoad`), applies the friction-circle limit, and publishes grip usage. `DriftHUD` displays the controller's used and maximum friction. `TelemetryHUD` estimates friction force and grip usage from measured acceleration and its configured friction coefficient.
- **Tire state (gripping/sliding):** `CarController` calculates each wheel's `slide` value. `DriftHUD` labels tires as static/gripping or kinetic/sliding when the maximum grounded-wheel slide crosses `slidingThreshold`.
- **Collision impulse:** Unity supplies the collision impulse in `Collision.impulse`. `DriftHUD.OnCollisionEnter` filters small or ground impacts and stores the accepted impulse for display; it does not calculate the impulse itself. `CarAudio` can also read collision impulse to set crash volume.

The HUD calculations are measurements and estimates for display; they do not replace the Rigidbody simulation. The car's motion changes when Unity integrates the forces applied by `CarController` during physics steps.

## 6. CarAudio

File: `Assets/Scripts/CarAudio.cs`

`CarAudio` creates looping and one-shot AudioSources for engine, tire squeal, wind, and crashes. It can use layered recordings or generate placeholder clips at runtime.

### Engine calculation

Speed is normalized against `maxSpeed`. A fake gearbox divides the normalized engine range into `gears` sections:

```text
engineNorm = clamp01(speed / (maxSpeed * 0.9))
gearPosition = engineNorm * gears
gear = floor(gearPosition)
inGear = clamp01(gearPosition - gear)
```

The target RPM is blended between idle and gear RPM, with a small low-speed throttle bump. RPM is smoothed using exponential interpolation. In single-loop mode, RPM controls pitch and volume. In layered mode, nearby RPM layers crossfade, and on-throttle/off-throttle clips crossfade using `onMix`.

Tire squeal uses the maximum grounded tire slide and maps it from `screechStartSlide` to `screechFullSlide`. Wind volume uses squared normalized speed, making it grow more noticeably at high speed. Wall crash volume is based on collision impulse:

```text
strength = clamp01(impulse / (mass * 15))
volume = crashVolume * lerp(0.3, 1.0, strength)
```

Placeholder audio is synthesized from harmonic sine waves, filtered deterministic noise, and an exponentially decaying noise/thump crash.

## 7. DriftSmoke

File: `Assets/Scripts/DriftSmoke.cs`

`DriftSmoke` creates up to four particle systems at runtime. By default it enables only the rear pair. For each grounded tire, sideways slide is mapped to emission rate:

```text
slideFactor = inverseLerp(slideStart, slideFull, tireSlide)
emissionRate = slideFactor * maxRate
```

Particles simulate in world space so smoke remains behind the moving car. The script also generates a soft circular texture, fade-in/fade-out color curve, growth over lifetime, and a billboard material without requiring a smoke asset.

## 8. WheelVisuals

File: `Assets/Scripts/WheelVisuals.cs`

`WheelVisuals` updates visible tire meshes from the physics wheel markers. It:

- Follows suspension compression using exponential smoothing.
- Places each visual tire below its marker by the current spring length.
- Applies front steering only to the front pair.
- Spins tires using forward speed divided by wheel radius.
- Stops rear tire spin while the handbrake is active.
- Applies per-wheel spin direction corrections with `spinSign`.

The spin rate follows the rolling relationship:

```text
angularSpeedDegreesPerSecond = forwardSpeed / wheelRadius * radiansToDegrees
```

## 9. FollowCamera

File: `Assets/Scripts/FollowCamera.cs`

`FollowCamera` is a chase camera. It projects the target's forward direction onto the horizontal plane, places the camera behind and above the car, then interpolates toward that position:

```text
desiredPosition = targetPosition - flatForward * distance + up * height
k = 1 - exp(-followSharpness * deltaTime)
cameraPosition = lerp(cameraPosition, desiredPosition, k)
```

The camera looks at the target plus `lookHeight`. The lag makes the car's drift angle easier to see.

## 10. SpeedEffects

File: `Assets/Scripts/SpeedEffects.cs`

`SpeedEffects` runs on the main camera and finds the car and URP Volume automatically when they are not assigned. Speed is mapped from `startSpeedKmh` to `fullSpeedKmh` with `InverseLerp` and `SmoothStep`:

```text
targetAmount = smoothStep(0, 1, inverseLerp(start, full, speedKmh))
amount = lerp(amount, targetAmount, 1 - exp(-response * deltaTime))
```

The smoothed amount controls both effects:

```text
fieldOfView = baseFov + fovBoost * amount
motionBlurIntensity = maxBlur * amount
```

Motion blur is added to a runtime copy of the volume profile, so the saved profile asset is not modified.

## 11. BackgroundMusic

File: `Assets/Scripts/BackgroundMusic.cs`

`BackgroundMusic` adds a 2D looping AudioSource in `Start`, assigns the configured clip and volume, and begins playback. It is independent of the car physics.

## 12. Runtime data flow

```text
Input System
    -> CarController.Update
    -> throttle, steering, handbrake

Rigidbody velocity + wheel raycasts
    -> CarController.FixedUpdate
    -> suspension, tire slip, forces, grip, drift angle

CarController published values
    -> DriftHUD / TelemetryHUD
    -> CarAudio
    -> DriftSmoke
    -> WheelVisuals

Car Rigidbody velocity
    -> FollowCamera
    -> SpeedEffects
```

The important ownership rule is that visual and audio scripts do not calculate their own car physics. They consume values from `CarController`, which keeps the gameplay behavior centralized.

## 13. Design summary and limitations

The implementation is an arcade-simulation hybrid rather than a full vehicle simulator. It uses physically meaningful concepts such as force, momentum, energy, suspension load, slip angle, downforce, and friction limits, but it intentionally simplifies several areas:

- Engine output is a configurable force, not a torque curve or transmission simulation.
- Gear behavior in audio is synthetic and does not change the actual drivetrain.
- Tire behavior is a compact slip-angle curve, not a complete tire model.
- Suspension is raycast-based and has no separate wheel collision geometry.
- Grip is summarized by a friction circle per tire.
- The HUD estimates some values from frame-to-frame velocity rather than reading a dedicated force sensor.

These choices keep the backend predictable and tunable for drifting while preserving the calculations that make the car feel grounded.