# Known Issues & Investigation Notes

Running log of open/intermittent issues and design discussions, kept for future tracing.

---

## 1. Horizontal "first flick scrolls too far" (intermittent, OPEN)

**Symptom:** Occasionally the *first* horizontal scroll of a gesture is very sensitive
(jumps far); subsequent scrolls in the same session feel normal. Not reliably
reproducible.

**Environment:** Logitech MX Master 3S, thumb wheel = horizontal (`WM_MOUSEHWHEEL`).
Logi Options+ is **not installed** (so it is not Logitech driver smooth-/high-res
scrolling). Reproduced with smooth horizontal **on**.

**Observations**
- Horizontal **only** — the vertical (ratcheted) main wheel never shows it.
- "First then normal" within a session.
- Turning **momentum off does not change it** → not the velocity model amplifying.

**Ruled out (code review)**
- Per-gesture state leak: `RemainingPx` / `Velocity` / `UnitAccum` all reset to 0
  between gestures (easing cleanup, momentum stop, and axis-lock `Reset()`).
- Horizontal acceleration: `HorizontalAccelerationMax` defaults to 1, so `AccelFactor`
  is pinned at 1 on the horizontal axis — no per-notch amplification.
- Momentum amplification: user confirms momentum off behaves the same.

**Lead hypothesis:** the light/free-spinning thumb wheel emits a *burst* of
`WM_MOUSEHWHEEL` events within a few ms on the initial flick. We scale each notch to
`HorizontalStepSizePx`, so a burst of N notches = N× the distance; later precise nudges
are single notches = normal. Horizontal-only because the main wheel is ratcheted.

**Diagnostic in place** (commit `058333c`, `Core/SmoothScrollEngine.cs`): the hook
thread aggregates horizontal event count + total delta between worker frames (two int
adds only); the **worker** thread logs a gated line when a burst is seen. No file I/O on
the hook thread.
- Log: `%AppData%/SoftScroll/logs/softscroll-<date>.log`, search `HWheel`.
- Example: `[HWheel] burst: 4 event(s)/frame, totalDelta=480 (~4.0 notches), dt=9.1ms`
- If a "far" scroll coincides with a burst line (count > 1 or notches > 1) → confirms the
  thumb-wheel burst. If "far" happens with **no** burst line → look elsewhere.

**Candidate fixes (NOT applied — waiting on a captured log):**
- Coalesce horizontal notches arriving within a short window (~10 ms) into one, and/or
  cap horizontal pixels emitted per frame (a burst limiter). Risk: could clip a genuine
  fast horizontal flick.
- Lower `HorizontalStepSizePx` further (reduces the magnitude of any burst).

---

## 2. Momentum feel: "soft" and a little dizzying for slow reading (DESIGN, discussing)

**Symptom:** With momentum on, slow browsing (reading an article) feels mushy/"soft" and
less snappy than with momentum off; scrolling for a long stretch can feel slightly
dizzying.

**Analysis**
- **Soft start.** A single notch seeds `Velocity = pixels / tau`. For the Figma profile
  (step 160, friction 40 → tau ≈ 480 ms) that's ≈ 0.33 px/ms → ~2.7 px in the first frame.
  The momentum-off easing path front-loads the same notch (ExponentialOut over
  `AnimationTimeMs` ≈ 320 ms) → ~15 px in the first frame. So momentum starts each notch
  ~5–6× gentler → reads as "soft/laggy".
- **Continuous drift → dizziness.** Every notch becomes a ~tau-long glide that keeps
  moving *after* the wheel stops. Rapid reading notches overlap into constant smooth
  drift that doesn't stop crisply when you stop → the page "floats", mismatching input,
  which is the dizzying part.
- **Nearly linear (weak rate sensitivity).** "Real" inertia should be: scroll a little →
  weak glide, flick hard → strong glide. The current model only weakly has this. Because
  tau (~400–480 ms) ≫ the gap between notches in normal scrolling, velocity stacks almost
  fully regardless of how fast you scroll, so glide distance ≈ proportional to total
  notches (≈ linear). A fast flick is only marginally stronger than a slow scroll of the
  same number of notches.

**Root cause:** momentum applies inertia uniformly to *every* notch, including slow
single ones. A mouse wheel has no inherent "slow drag vs flick" distinction (unlike a
trackpad), so uniform inertia makes precise reading floaty.

**Directions (NOT decided):**
- **Flick threshold:** engage glide only above a scroll-rate threshold; below it use the
  crisp easing path. (Closest to macOS trackpad behaviour.)
- **Hybrid layering (recommended):** always do the crisp per-notch easing; add momentum as
  *extra* glide only once velocity exceeds a threshold. Slow reading = crisp; fast flick =
  inertia. Solves both the "soft" and the "not strong enough when flicking" complaints.
- **Snappier impulse + shorter tau:** raise initial velocity and shorten the low-speed
  glide so single notches start crisp and stop quickly, reserving long glide for stacked
  fast scrolls. Simpler but less clean than the hybrid.

Status: discussion only, no code changed.
