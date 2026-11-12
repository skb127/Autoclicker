# How the WindMouse Algorithm Works (`Core/WindMouse.cs`)

This document explains in simple, intuitive terms how `WindMouse.cs` generates human-like mouse cursor movements step by step.

---

## Core Idea (The Physics Metaphor)

Instead of teleporting the cursor from `(startX, startY)` to `(destX, destY)` in a single jump or moving along a rigid straight line, **WindMouse** models the cursor like a small puck sliding across a table toward a magnet while a gentle lateral breeze blows:

1. **Gravity (`Gravity = 9.0`) — Pull Toward the Target:**
   At every step, a steady force pulls the cursor directly toward the destination coordinate. If only gravity existed, the cursor would travel in a perfectly straight line.
2. **Wind (`Wind = 3.0`) — Natural Hand Wobble:**
   A random lateral force pushes the cursor slightly to the sides. Instead of changing abruptly on every pixel (which would look like noisy zig-zags), the wind retains part of its previous momentum (`wind / √3`) and adds a small random nudge (`random / √5`). This produces **smooth, natural curved arcs**.
3. **Braking / Fine-Aiming Zone (`TargetArea = 12.0 px`):**
   When a person brings the mouse close to a button (within `12 px`), they stop making wide arcs and slow down to land cleanly on the target. Inside this radius:
   - No new random wind is added, and existing wind decays.
   - The maximum step size (`maxStep`) gradually shrinks so the cursor settles smoothly onto the destination without overshooting.
4. **Speed Cap (`MaxStep = 15.0 px`):**
   If the combined gravity and wind forces push the cursor too fast in a single step, its velocity is clipped to a random value between `maxStep / 2` and `maxStep`.

---

## Step-by-Step Loop in `GeneratePoints`

```text
While distance to destination >= 1 pixel:
  1. Are we still outside the braking zone (distance >= TargetArea)?
       - YES (Cruising phase): decay previous wind and add a fresh random lateral push.
       - NO  (Braking phase):  dampen wind and shrink maxStep to decelerate smoothly.
  2. Add both forces to the current velocity (velocityX, velocityY):
       - Wind force (lateral drift)
       - Gravity force pulling toward the target: Gravity * (dest - current) / distance
  3. If total speed exceeds maxStep, clip velocity to [maxStep / 2, maxStep].
  4. Advance position:
       currentX += velocityX
       currentY += velocityY
  5. Round to integer pixel (X, Y) and append to the path (skipping duplicate pixels).
When finished:
  Ensure the final coordinate in the list is the exact destination (destX, destY).
```

---

## Per-Step Timing (`GenerateTrajectory`)

In addition to the curved path, realistic movement depends on **how long** the stroke takes:

- **Total Duration (Fitts's Law):** In human ergonomics, the time needed to point at a target grows logarithmically with distance:
  $$\text{Duration} = \text{Base} + \text{Slope} \cdot \log_2\left(\frac{\text{Distance}}{16} + 1\right)$$
  A short move (`50 px`) takes roughly `~180 ms` at normal speed, whereas crossing the screen (`1000 px`) takes roughly `~450 ms`, with a natural $\pm 10\%$ random variation per stroke.
- **Velocity Profile (Ease-In / Ease-Out):** The total duration is distributed across the path points with slightly longer pauses at the very start (hand acceleration) and end (fine aiming before clicking), and faster movement through the middle of the stroke.
