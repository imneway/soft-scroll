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

### Update 2026-06-24 — it's really on the ZOOM path

The user reproduces it mainly in **Figma**, and clarified the gesture: the thumb wheel is
remapped (externally — still no Logi Options+) to a **vertical** wheel, and scrolled with
**Ctrl held**, i.e. it becomes **zoom**. Symptom restated: input distance/speed is normal,
but *occasionally* a single gesture produces a **large zoom jump**. The `[HWheel]` burst
lines didn't line up because that gesture never goes through `OnHWheelCore` — Ctrl+vertical
routes to `ZoomSmoothEngine`, which (until now) logged nothing. (Chromium only zooms on
`deltaY`+Ctrl, so the events must indeed arrive as vertical, confirming the external remap.)

**Mechanism (confirmed by code):** a single frame emits at most `MAX_ZOOM_PER_FRAME` (60 =
½ notch) and total zoom is conserved, so a *sudden large* zoom can only come from a large
accumulated backlog → a **burst** of zoom events (`OnZoom` accumulates up to `MAX_BACKLOG`
= 6 notches = 720). Same thumb-wheel-burst family as above, just on the zoom path.

**Diagnostic in place** (`Core/ZoomSmoothEngine.cs`): hook thread aggregates zoom event
count + total delta per worker frame (int adds only); the **worker** logs a gated line:
- `[Zoom] burst: {Count} event(s)/frame, totalDelta={Total} (~{Notches} notches), backlog={N}`
- Search the log for `Zoom`. Pair a visible large zoom with a line here: `backlog` ≈ how
  many notches got zoomed. `count > 1` = a rapid multi-event burst; `count = 1` with
  `|total| > 120` = one oversized event (remap emitting multi-notch deltas). No line during a
  big zoom → it isn't this path (look at native zoom / routing instead).

Also noted: Ctrl is sampled at 16 ms (`KeyboardStateSampler`), so WM_MOUSEWHEEL zoom-vs-scroll
routing can be up to a frame stale — a secondary suspect if bursts alone don't explain it.

**Candidate fixes (after a capture):** lower `MAX_BACKLOG` (caps a burst's zoom; may clip a
genuine fast zoom), and/or coalesce zoom events arriving within a short window into one.

### Root cause + fix 2026-06-24 — stale-Ctrl misroute (RESOLVED, pending user retest)

The user reproduced the large zoom several times but captured **no `[Zoom]` line** — which is
itself the answer: the events never reach `OnZoom`. They're **misrouted to the regular vertical
scroll path**, and that path (unlike zoom) applies **step size + acceleration + momentum**.

Chain: `GlobalMouseHook` decides zoom-vs-scroll for `WM_MOUSEWHEEL` from `KeyboardStateSampler`,
which polls modifiers at ~60fps (≤16 ms stale). When the **first** wheel event of a Ctrl gesture
lands in that gap, Ctrl reads **up** → routes to `SmoothScrollEngine` (vertical), which scales
the notch by step/accel/momentum and injects a wheel. Because Ctrl is **physically** held, the
app reads that injected wheel as **zoom** → acceleration makes it a sudden LARGE zoom. No
`[Zoom]` log (never hit `OnZoom`), no `[HWheel]` log (it's vertical) → invisible. Matches the
original "sometimes the *first* scroll is too sensitive" framing.

**Fix:** in the hook's `WM_MOUSEWHEEL` branch, refresh the modifier snapshot from the live key
state (`_keyboard.ForceUpdate()`) before the zoom-vs-scroll decision — wheel events are rare, so
the per-event `GetAsyncKeyState` cost is negligible (the cache exists for `WM_MOUSEMOVE`). A
recovered misroute is flagged (`MouseWheelEventArgs.CtrlRecovered`) and logged by the zoom worker:
`[Zoom] recovered N stale-Ctrl event(s)/frame …`. After the fix those events route to zoom and
are bounded by `MAX_BACKLOG`, so they can no longer be accel-amplified into a big zoom.

### CONFIRMED via WheelTrace 2026-06-24 — Ctrl+HWHEEL through the scroll smoother (FIXED)

A full per-event routing trace (`WheelTrace`, logged from a worker) captured two reproductions.
Both large zooms were a single line:

```
H delta=-120 src=NativeHorizontal ctrlNow=True smoothing=True proc=Figma
```

i.e. the zoom gesture arrives as **WM_MOUSEHWHEEL + Ctrl**, and the hook routed *all*
`WM_MOUSEHWHEEL` to the horizontal **scroll** engine regardless of Ctrl. With `HorizontalSmoothness`
on (and the Figma profile's `HorizontalStepSizePx=80`), one notch becomes an `AnimationTimeMs`-long
train of small **Ctrl+HWHEEL** pulses, which Figma/Chromium over-zoom on (one physical notch → a
runaway zoom). It produced no `[Zoom]`/`[HWheel]` burst line because nothing was a multi-event
burst — it was the *smoother itself* fanning one notch into many pulses. The earlier stale-Ctrl
theory was a different (real but secondary) issue on the vertical path; this user's gesture is
horizontal, which is why the vertical fix didn't help.

**Fix:** Ctrl + horizontal wheel routes to the **zoom engine** instead of the horizontal scroll
smoother, so one notch is a bounded 1:1 Ctrl+wheel (like the main-wheel zoom) rather than a
fanned-out pulse train. The delta is negated so the thumb-wheel zoom direction matches expectation.

Made it an **opt-in setting** `CtrlHorizontalZoom` (default off — not every mouse/app uses the
thumb wheel to zoom), **per-profile overridable** (`AppProfile.CtrlHorizontalZoom`), since the
decision depends on the app under the cursor. The routing decision therefore lives in the App
`MouseHWheel` handler (which has the settings + profile and reads the live Ctrl state), not the
hook. A profile overrides global exactly like every other per-app setting — so for a profiled app
(e.g. Figma) the toggle must be enabled *in that profile*.

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

**Resolution (implemented):** threshold-gated mode switch. A configurable **flick threshold**
(`MomentumFlickThreshold`, px/s, global + per-profile, default 1200) picks each notch's mode
by input speed: a **slow** notch (below threshold) feeds the crisp easing target — no inertia,
so reading is snappy and stops cleanly; a **fast** notch (at/above threshold) feeds the
original velocity-friction momentum impulse (`Velocity += pixels/tau`, decays `exp(-dt/tau)`).
Both can coexist within a gesture and `Step` emits them concurrently, so a slow→fast transition
has no jump.

An earlier attempt seeded the glide from `vIn = pixels/gap` (speed) minus the threshold; that
blows up for small gaps (and scales with `StepSizePx`), so fast scrolling flung ~2500–3000px.
The impulse `pixels/tau` is bounded and stacks with rapid notches, which is the intended "old"
inertia. Threshold 0 = glide on any motion; higher = needs a faster scroll to engage.
