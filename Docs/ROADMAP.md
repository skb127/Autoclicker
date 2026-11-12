# Roadmap & Future Enhancements

This document tracks ideas and enhancements planned for future phases of **Autoclicker**.

---

## 1. Coordinate Picker — Level B Fallback (Frozen Screenshot Background)

- **Context:** Level A uses per-monitor borderless windows (`Height - 1 px` on Linux) with a translucent veil (`#33000000`), an instruction banner, and a live floating `X, Y` badge to avoid compositor fullscreen direct-scanout opacity.
- **Level B (if a compositor still renders XWayland translucent overlays opaque):**
  - Capture a non-interactive snapshot via `org.freedesktop.portal.Screenshot` before opening the overlay and render that frozen desktop image as the background of the picker window so the user always sees the exact desktop contents underneath the crosshair.

---

## 2. Explicit "Change Shared Monitor" Action on Wayland

- `WaylandInputSimulator` already reuses the active portal session and calibrated `AxisTransform` across Start/Stop cycles and automatically resets if pattern coordinates move outside the shared monitor.
- **Enhancement:** Add an explicit *"Change Shared Monitor"* button in the UI so users can manually switch the shared monitor session at any time.

---

## 3. WindMouse Refinements

- **Overshoot & Corrective Micro-Movement:**
  - Occasionally allow the trajectory on fast, long-distance strokes to overshoot the destination by a few pixels (`3–8 px`) and perform a short corrective stroke back onto the target, mimicking how a human hand corrects after a quick flick.
- **Pre-Click Micro-Pause:**
  - Add an optional randomized settling pause (`15–45 ms`) between arriving at the target coordinate and pressing the mouse button down, plus a subtle press-to-release dwell (`15–40 ms`) on standard clicks.
- **Visual Trajectory Preview:**
  - Optional canvas preview inside the Movement expander showing sample generated WindMouse paths for the selected speed preset.

---

## 4. Per-Coordinate Action Overrides

- Currently, Mouse Button (`Left`, `Right`, `Middle`) and Action Type (`Click`, `DoubleClick`, `Hold`, `Drag`) are configured **globally** for the entire pattern.
- **Future extension:** Extend `Coordinate` with an optional per-point override:
  ```csharp
  public record Coordinate(int X, int Y, ClickActionOverride? Override = null);
  ```
  allowing mixed sequences (e.g., *Right-click at point A → Left-click at point B → Hold 500 ms at point C*) while keeping global defaults for points that do not specify an override.

---

## 5. Interval Distribution Selector in UI

- `IntervalProvider` already supports both `Uniform` and `TruncatedGaussian` (Box-Muller centered at the midpoint of `[Min, Max]`).
- Expose a dropdown in the **Interval & Repetitions** section so users can choose between Uniform and Gaussian distributions, or import/export named presets.

---

## 6. Multi-Monitor Enhancements

- **Wayland (`WaylandInputSimulator`):** Support selecting or mapping multiple PipeWire ScreenCast streams simultaneously when a single pattern spans across more than one physical monitor.
- **Windows (`Win32InputSimulator`):** Use `SM_XVIRTUALSCREEN`, `SM_YVIRTUALSCREEN`, `SM_CXVIRTUALSCREEN`, `SM_CYVIRTUALSCREEN` together with `MOUSEEVENTF_VIRTUALDESK` (`0x4000`) so absolute coordinates map across multi-monitor setups with negative or extended offsets.

---

## 7. Automated Unit Test Suite (`Autoclicker.Tests`)

- Add an xUnit test project targeting the pure platform-agnostic classes in `Autoclicker/Core/`:
  - `AxisTransformTests`: verify `FromScreenAndStreamSize`, `FromSamples`, `ToStream`, `ToGlobal`, and invalid/anisotropic scale rejection.
  - `IntervalProviderTests`: verify bounds `[Min, Max]`, `Min == Max` constant behavior, and Gaussian clamping.
  - `WindMouseTests`: verify deterministic output with seeded `Random`, exact convergence on `(destX, destY)`, step speed bounds, and zero-distance handling.
  - `ClickEngineTests`: verify action sequences (`Click`, `DoubleClick`, `Hold`, `Drag`) and guaranteed button release (`isDown: false`) when `CancellationToken` triggers mid-action using a fake `IInputSimulator`.
  - `SettingsStoreTests`: verify JSON round-tripping and graceful fallback when `settings.json` is missing or corrupted.
