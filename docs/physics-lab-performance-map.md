# Physics Lab Performance Change Map

## Goal

Make running through the many-object physics lab smooth while retaining the
Box3D-style character mover, contact response, stack stability, and deterministic
rollback. Prefer removing repeated work and adopting Box3D's data layout and
caches over reducing simulation quality.

This is the performance roadmap, with diagnostic progress recorded below. **A is
complete for the user-accepted defined lab route**, including a warmed Metal GPU
capture, matched Debug/ExportRelease frame breakdowns, and explicit manual warm
windows. ExportRelease stays near 60 Hz; Debug reproduces sustained slowdown with
physics and replay amplification dominating. B is implemented and differentially
verified below; C and D are implemented and verified below. The original editor session is not reconstructed;
the user accepted the defined route and settings as A's comparison workload.

## Measured baseline

The authored `Client/maps/level_pipeline_test.level.bytes` contains a stress
area with 64 crates and 32 spheres. The diagnostic places a character at
`(9, 1.5, -33)`, moves right for 160 ticks, then lets the scene settle through
tick 479. It recreates and replays the world three times to distinguish cold
startup from warmed execution, and compares the full level with a version that
excludes all `PhysicsStress/` placements.

Representative measurements from the third replay:

| Window | Release session tick | Debug session tick |
| --- | ---: | ---: |
| Initial movement and settling, ticks 0–119 | 0.45 ms | 5.95 ms |
| Push/settling, ticks 120–239 | 0.32 ms | 3.77 ms |
| Further settling, ticks 240–359 | 0.22 ms | 2.92 ms |
| Mostly settled, ticks 360–479 | 0.08 ms | 0.97 ms |
| Stress area removed, ticks 120–239 | 0.08 ms | 0.84 ms |

These are developer-machine, headless .NET 10 test-runner measurements of the
GameCore simulation. They are not Godot frame times or a direct Box3D benchmark.
The desktop Godot client targets .NET 8. Removing `PhysicsStress/` also removes
that area's static placements, so this comparison measures the area as a whole.

### What the measurements establish

- Debug is about 12–13 times slower than warmed Release in these lab windows.
- Contact solving and narrowphase dominate the active-prop workload. In the
  Debug push/settling window they cost about 1.35 ms and 1.34 ms respectively;
  the character mover costs about 0.20 ms.
- Sleeping already works. Tick cost falls sharply as bodies settle; adding
  sleeping from scratch is not the next task.
- Cold Release runs are substantially slower than warmed replays. Treat startup
  and steady-state performance separately.
- Full-world interpolation copying costs about 0.10 ms/call in Release for the
  lab, copies a roughly 263 KB snapshot, and allocates about 2.36 KB/call.
  This was measured separately using the same serialize/load operation as the
  client, not as part of the session-tick figures above.

### Remaining measurement questions

- The user accepted the defined rendered traversal/settings on 2026-10-03;
  measurements show sustained Debug slowdown but no sustained optimized-export
  slowdown on that workload. Other routes/devices need their own captures.
- Godot's GPU counter remains zero; warmed GPU occupancy is now independently
  measured with Metal System Trace below.
- Attribute occasional release wall-time spikes outside the measured simulation
  and view boundaries; do not label leftover wall time as rendering.
- Broader/longer routes and target devices beyond the local M5 baseline.

Offline play calls the local server and client sequentially in
`Client/setup/ClientGame.cs`. Both simulations contribute to frame time, and
rollback can multiply the work. The headless Release timings alone do not prove
the cause of a sustained in-game FPS problem.

### Baseline follow-up: 2026-10-01

**Status:** A attempted, blocked before an actual ExportRelease traversal.
B/C/D remain unimplemented. No simulation, collision, or solver code was changed
for this follow-up. The existing uncommitted lab and large-ground fix were retained.

The requested Release diagnostic was run against the current working tree using
the command in the reproduction section; both explicit cases passed. Environment:
macOS, Apple M5 (reported by Godot), .NET SDK 10.0.301, test runtime .NET 10.0.9.
These are still **headless session tick measurements**, not client frame times:

| Third replay window | Full lab mean | Tick p95 | Tick max | No-stress-area mean |
| --- | ---: | ---: | ---: | ---: |
| Ticks 0–119 | 0.508 ms | 0.807 ms | 0.908 ms | 0.091 ms |
| Ticks 120–239 | 0.348 ms | 0.422 ms | 0.489 ms | 0.086 ms |
| Ticks 240–359 | 0.254 ms | 0.349 ms | 0.377 ms | 0.095 ms |
| Ticks 360–479 | 0.089 ms | 0.101 ms | 0.103 ms | 0.069 ms |

In the full-lab third replay's push/settling window, physics averaged 0.313 ms,
including solve 0.150 ms and narrowphase 0.079 ms; the mover averaged 0.021 ms.
The combined physics-plus-mover average was 0.334 ms, below the 1 ms budget.
The first replay's initial window averaged 5.118 ms/session tick, p95 7.085 ms,
maximum 94.512 ms. That window includes cold simulation/JIT work; it does not
measure Godot resource loading, shader startup, or process launch. World copying
in the separate warmed interpolation probe measured 0.110 ms/call, 2,360 B/call,
with a 263,134-byte full-lab snapshot. Session-window allocations include
structural/gameplay churn and are not the steady-state physics allocation gate.

#### Actual-client checks and blocker

- `dotnet build Client/Space.csproj -c ExportRelease` succeeded with zero warnings
  and errors, producing `Client/.godot/mono/temp/bin/ExportRelease/Space.dll`.
  Compilation alone does not establish what configuration a project launch uses.
- The local engine is
  `~/Applications/Godot_mono-4.7.2.app/Contents/MacOS/Godot`, version
  `4.7.2.stable.mono.official.ed1daf0bf`, matching the project's Godot SDK.
- A three-frame rendered smoke launch (`--path Client --quit-after 3 -- --level
  level_pipeline_test`) succeeded with Metal 4.0 / Forward Mobile on Apple M5.
  It loaded 156 level placements with content hash
  `6AB8DFF2116D59D270A13CF10BB5E3319D91B4F8D36BF247673DD57F7A484BE5`, and started
  both the offline server and predicted client. Its C# backtrace identified
  `obj/Debug`, so this was a Debug project launch, **not an ExportRelease sample**.
  It neither traversed the lab nor supplied a frame-time profile.
- A desktop release export attempt with `--headless --path Client
  --export-release macOS <temporary-output.zip>` failed with
  `Invalid export preset name: macOS`. `Client/export_presets.cfg` contains only
  `iOS` and `Android`. The installed `4.7.2.stable.mono` templates under
  `~/Library/Application Support/Godot/export_templates/` contain Android and iOS
  artifacts, but no macOS export template. No desktop release artifact was made.
- The active `dotnet --list-runtimes` lists only .NET 10. That inventory is not
  a demonstrated client launch failure: the rendered smoke launch succeeded.
  Do not treat a presumed missing .NET 8 runtime as the confirmed blocker.

To resume A locally, supply/install the matching **Godot 4.7.2 Mono macOS export
template** and configure a macOS preset, or supply a runnable desktop release
export of this working tree. If the slowdown is on another device, access to
that target's rendered client and profiler capture is needed instead. Confirm
the reported slow route (or provide an input replay/recording with starting pose),
resolution, VSync/frame cap, and debug-overlay state. The diagnostic's
`(9, 1.5, -33)`, +X for 160 ticks, then settling through tick 479 is the defined
comparison route; it has not yet been reproduced in the rendered client.

**Still unmeasured:** client frame-time p50/p95/p99/max; server forward ticks,
client forward ticks, and rollback-resimulation ticks per rendered frame; server,
prediction, interpolation-copy, view-update, debug-draw, and rendering costs.
No client frame capture or new client instrumentation was produced by this
follow-up. Use actual tick callbacks to count work: a net change in session tick
does not count replayed ticks. Treat interpolation as a nested part of client
update when reporting inclusive boundaries, rather than adding it twice. Capture
render CPU/GPU work with Godot's profiler; leftover wall time is not proof of
rendering cost. Keep process startup/connection/first traversal separate from
repeated warmed pushes, with per-frame samples rather than just `LastStep`.

#### Verification of the retained baseline

- Release diagnostic: 2/2 passed.
- Release suite: 326/326 non-explicit tests passed, including tilted-crate/ground
  manifolds in both shape orders, pushed stress clusters, the complete dense lab,
  contact persistence, mover/platform behavior, sleep/wake, full sync, and
  per-tick rollback state-hash replay. The two explicit diagnostic cases were
  run separately above. The initial full-suite invocation hit the 120-second
  tool timeout; rerunning with `--no-build --no-restore -m:1` and a longer timeout
  completed successfully in about 106 seconds.
- `dotnet run --project benchmarks/GameCore.Benchmarks/GameCore.Benchmarks.csproj
  -c Release -- --physics --enforce`: passed. Regular-tick average 0.330 ms
  (worst 2.197 ms; the gate is the average), 30-tick rollback burst 5.512 ms,
  125-tick rollback burst 25.450 ms. The suite's load-scene steady-state allocation
  test measured zero bytes/tick.

These results established a passing simulation baseline. The desktop export
blocker described above was subsequently removed; see the rendered capture below.

### Rendered-client baseline after installing the macOS template

**A status:** local baseline captured; original user-route confirmation and GPU
attribution remain open. **B/C/D status:** deferred. The optimized traversal has
frame-time headroom, so there is no demonstrated steady-state ExportRelease
physics bottleneck to optimize yet. Debug does reproduce severe sustained stalls.

#### Implementation and workload

- Completed the user-added macOS preset with a bundle identifier and
  `include_filter="*.level.bytes"`; the matching Mono macOS template exports a
  runnable application. The template embeds .NET **8.0.28**. The rendered Debug
  project launch uses the locally installed .NET **10.0.9**; the comparison is of
  these actual workflows, not an isolated same-runtime compiler experiment.
- Added `Directory.Build.props` to explicitly enable `Optimize` for
  `ExportRelease` in ordinary .NET dependencies as well as the Godot client.
  Before this, the client reported `Optimize=true`, while GameCore's property
  was unset for this custom configuration. The captured release assemblies
  (Space, GameCore, FixedPoint, StaticEcs, Rollback) all report `ExportRelease`
  with JIT optimization **not disabled**. Debug reports optimization disabled.
  No before/after speedup is claimed for this build-setting fix alone.
- Added opt-in `ClientPerformanceCapture` and
  `benchmarks/summarize_client_capture.py`. Timings/counters are outside world
  snapshots. The capture wraps the existing update roots and retains all
  production systems, correction observers, transport, prediction, rollback,
  interpolation, views, and rendering. No physics algorithm or quality settings
  were changed. Frame storage allocations are diagnostic-only; file output is
  deferred until capture ends, and no tick logs were added.
- `--physics-lab-replay` recreates the scene/worlds three times **in one process**:
  run 0 is process-cold; runs 1/2 retain warmed JIT and graphics/resource caches.
  At simulation tick 60 both worlds place the character at `(9, 1.5, -33)` and
  reset mover motion state. This fixed-tick fixture operation also runs during
  rollback replay. Normal networked input requests +X for the nominal 160-tick
  movement window, then zero through route tick 479. Transport/prediction can
  delay those transitions, especially during Debug stalls. This is the same
  authored route, **not a bit-identical workload to the headless fixture**:
  props have simulated 60 ticks before movement, and input is frame-sampled.
- All runs used the unchanged level hash recorded above, 1920×1080, Metal 4.0 /
  Forward Mobile, Apple M5 (Apple9), VSync enabled, and `Engine.MaxFps=0`.
  The first release capture contains 1,538 frames, Debug 366 frames, and the
  overlay-enabled release comparison 1,498 frames. All reported valid route
  starts in both worlds. The final incomplete frame of each run is excluded.

#### Warm frame-time percentiles

Third replay; milliseconds per **rendered frame**. Percentiles use nearest rank;
windows are grouped by server head at frame start, so boundary frames can contain
ticks from two windows. A 25 ms warmed-p95 diagnostic threshold passes release
and fails Debug; it is not a new portable physics/CI budget.

| Route window | Release frames | Release p50 / p95 / p99 / max | Debug frames | Debug p50 / p95 / p99 / max |
| --- | ---: | --- | ---: | --- |
| 0–119 | 120 | 16.685 / 19.620 / 20.219 / 21.202 | 63 | 22.349 / 64.462 / 246.961 / 246.961 |
| 120–239 | 120 | 16.647 / 16.966 / 18.201 / 19.031 | 16 | 198.362 / 240.271 / 240.271 / 240.271 |
| 240–359 | 120 | 16.674 / 16.984 / 17.217 / 19.045 | 15 | 200.619 / 231.402 / 231.402 / 231.402 |
| 360–479 | 119 | 16.688 / 17.047 / 18.053 / 20.259 | 14 | 165.889 / 187.646 / 187.646 / 187.646 |

The small Debug frame counts make tail percentiles coarse. Run 2 release had no
route frames above 25 ms. Run 1 release had three isolated frames above 25 ms
(maximum 34.577 ms), whose measured simulation/view work was small; their cause
is not established by these counters. No sustained release slowdown was reproduced.

#### Attribution: warmed push/settling, route ticks 120–239

Mean milliseconds/frame, except tick counts. Boundaries are **inclusive**:
physics is inside simulation, interpolation is inside client update, and
server/client updates are inside `ClientGame._Process`. Do not add nested rows.

| Work | ExportRelease | Debug |
| --- | ---: | ---: |
| Offline server update | 0.374 | 52.952 |
| Client update (prediction, replay, copies, snapshot/transport work) | 2.230 | 140.332 |
| Physics within server / client simulation | 0.294 / 1.764 | 47.369 / 125.933 |
| Client replay simulation alone | 1.665 | 85.632 |
| Interpolation copying (inside client update) | 0.130 | 0.550 |
| Entity view updates | 0.147 | 0.940 |
| Disabled physics + navigation debug view callbacks | 0.004 | 0.011 |
| Render CPU + frame setup, latest available engine samples | 0.191 | 0.348 |
| Server ticks/frame, mean / maximum | 1.00 / 1 | 7.75 / 8 |
| New client ticks/frame, mean / maximum | 1.00 / 1 | 7.88 / 9 |
| Client replay ticks/frame, mean / maximum | 6.00 / 8 | 12.88 / 17 |

**Confirmed:** Debug's measured simulation CPU cost accounts for most of the
stalled frames. Late-input drops/mispredictions also occur under those stalls.
Actual callback counts reveal substantial replay even in warmed release: one
server tick and one new client tick are accompanied by 4–8 old client ticks in
the push window. `Session.Data.FastForwardToTick` unconditionally restores the
aligned snapshot in automatic-rollback mode before advancing; these replay
counts are not a count of misprediction events. Slow-frame catch-up further
multiplies the expensive Debug ticks. No rollback policy was changed in this slice.

**Not established:** that the user's reported slowdown is exclusively a Debug
problem, that release has no GPU issue, or that all sporadic long wall intervals
are explained. GPU counters were zero in every capture. Render CPU measurements
can lag the current frame and are not a complete GPU/presentation/wait-time
profile. Frame intervals include VSync and OS/engine waiting.

#### Startup and enabled debug drawing

Release run 0 reached the first `ClientGame._EnterTree` at engine uptime
5,537.772 ms; the measured setup body took another 3,831.973 ms. Fresh-world setup
on runs 1/2 took 15.196 / 44.327 ms. The first nine sampled startup frames ranged
up to **823.354 ms**, with mean server/client update 82.227 / 72.031 ms per frame;
the first route window also contained cold work (maximum 134.241 ms). These are
process-cold observations, not guaranteed cold filesystem/driver caches, and
the pre-`_EnterTree` interval is not internally attributed by this C# probe.

With both physics and navigation overlays enabled in a separate release capture,
run 2 debug drawing averaged **0.887–1.004 ms/frame** across the route windows.
Window p95 values were 19.310 / 18.799 / 18.015 / 18.751 ms; occasional longer
intervals occurred (maximum 45.330 ms). Drawing is measurable, but the warmed
overlay capture does not reproduce Debug's sustained ~200 ms stalls. The first
overlay run's startup reached 1,812.959 ms/frame, reinforcing the need to exclude
first-use resource/JIT/graphics work from a warmed diagnosis.

#### Reproduction and verification

```sh
GODOT="$HOME/Applications/Godot_mono-4.7.2.app/Contents/MacOS/Godot"
"$GODOT" --headless --path Client --export-release macOS /tmp/physics-lab.app
/tmp/physics-lab.app/Contents/MacOS/Space -- \
  --level level_pipeline_test --physics-lab-profile /tmp/lab-release.csv \
  --physics-lab-replay
python3 benchmarks/summarize_client_capture.py /tmp/lab-release.csv --max-warm-p95-ms 25

# Same route, Debug project-launch workflow (run configurations sequentially).
dotnet build Client/Space.csproj -c Debug
"$GODOT" --path Client -- --level level_pipeline_test \
  --physics-lab-profile /tmp/lab-debug.csv --physics-lab-replay
python3 benchmarks/summarize_client_capture.py /tmp/lab-debug.csv --max-warm-p95-ms 25
```

Add `--physics-lab-debug` for the enabled-overlay comparison. Omit
`--physics-lab-replay` to capture a manually driven user route without fixture
teleports or scripted input; close the application normally to flush it. CSV
rows retain all frame/tick boundaries, phase timings, and player positions;
`<capture>.json` records build/runtime/hardware/workload metadata. Use the
summarizer's `--json` option for full distributions and slow-frame details.
The local raw files are `lab-release.csv`, `lab-debug.csv`, and
`lab-release-overlays.csv` (with JSON sidecars) under
`/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode/`.

Verification: rendered captures completed; client Debug and ExportRelease builds
passed; Release suite 326/326 passed; ExportRelease physics/manifold/toy suite
126/126 passed, including the large-ground fix and zero steady-state allocation.
The same two filtered rollback tests passed in Debug, Release, and ExportRelease
with matching final hashes (`rollback-replay b1b19ff21686b874`,
`sleep-replay 8100b262cc63ef10`). Hashes depend on test workload/order; this
comparison uses identical filters. Different configuration restores/builds must
run sequentially because shared `obj/project.assets.json` files select different
StaticPack dependencies; one concurrent Debug/export build encountered this
race and a sequential rebuild succeeded.

### User clarification and change B: 2026-10-01 follow-up

**Scope decision:** the user clarified that the ~5 FPS report came from running
in the editor, suspected Debug, and explicitly requested proceeding with B.
This superseded the immediate manual-route capture request. It is consistent
with the earlier Debug reproduction, but does not establish the exact path,
resolution, cap, or overlays of that original run. **A remains partial** for that
exact route; no optimized-user-route physics bottleneck is claimed. **B is now
implemented and verified. C/D are untouched.**

#### Actual-export verification and reproduction limits

- Before changing physics, rebuilt `Client/Space.csproj -c ExportRelease` and
  exported `physics-lab-route.app`. A rendered, unscripted 180-frame smoke launch
  (`--quit-after 180`, profile enabled, **no replay flag**) closed normally and
  flushed 179 frames. This captured startup/idle at the normal spawn, not user
  traversal. All six loaded assemblies report ExportRelease, with JIT optimization
  enabled; engine 4.7.2, .NET 8.0.28, M5, Metal/mobile, 1920×1080, VSync enabled,
  `MaxFps=0`, overlays disabled, unchanged level hash. Whole-smoke wall p50/p95/
  p99/max: 20.608/179.240/1264.528/1285.388 ms. These startup-inclusive numbers
  are **not** warmed performance. Setup: 2737.633 ms; first enter: 7835.630 ms.
- The manual summarizer currently groups all frames together. For a future exact
  route, select startup and repeated-push frame ranges explicitly from the CSV;
  do not use the combined `manual` percentile as a warmed percentile. Its overlay
  metadata is the launch flag, not a record of subsequent F3/F4/F5 toggles.
- Post-B uncapped scripted capture produced intervals around 3–4 ms despite
  reporting VSync enabled, unlike the previous near-60 Hz run. The presentation/
  window/display condition causing this difference is unverified. Its changed
  rendered-frame sampling also changes replay work per frame. Do not interpret
  this as a several-fold FPS gain from B. Repeated with an explicit `--max-fps 60`
  to obtain the separately labelled validation workload below.
- Xcode 27.0's `Metal System Trace` **successfully records on this Mac** with
  `xcrun xctrace record --template "Metal System Trace" --time-limit 5s
  --no-prompt --output <absolute.trace> --launch -- <export>/Contents/MacOS/Space
  --quit-after 120 -- --level level_pipeline_test`. Exporting the
  `metal-gpu-intervals` table confirms nonzero GPU execution intervals for Space
  (render, compute, blit). This is a capability/startup smoke trace, not warmed
  route GPU percentiles. Nested/overlapping encoder intervals must not simply be
  added as GPU frame time. The Godot counters remain zero. **Missing input is the
  exact route/recording and original settings, not demonstrated profiler denial.**

#### B implementation and exact-result verification

`GameCore/Physics/Manifold.cs` hoists the face-query inverse once per query and
uses stack-local edge-query caches: 12 normalized A edge directions, 8 fully
transformed B corners, 6 rotated B face normals, and one inverse B rotation.
Each B edge is normalized once outside the inner loop. Classification and axis
separation reuse the exact same normalized values. In particular, B edges are
still obtained by subtracting **translated, rotated endpoints**, not by rotating
a local edge vector (which would change fixed-point rounding). The full-box
support separation fix, j-then-i traversal, strict `>` tie-breaking, features,
tolerances, clipping, and solver settings are unchanged. No persistent cache,
snapshot change, or managed hot-path allocation is introduced.

- `BoxSatReference.cs` freezes the pre-B SAT and collision-selection arithmetic,
  including the existing ground fix. It shares only unchanged contact builders/
  transforms through test-only delegates; production SAT is not its oracle.
- `BoxSatEquivalenceTests.cs` compares complete raw manifolds (normal, count,
  all four point slots, order, feature/impulse/persistence fields) and edge-query
  separation/winning indices even when a face wins or collision returns early.
  Coverage: 4096 deterministic poses in both orders; 48 large-ground poses in
  both orders; 27 touching/speculative boundary poses in both orders; 650
  near-parallel, small-angle and symmetric edge-query cases. The random corpus
  includes local hull offsets/rotations and translated body poses, with empty,
  single-, three-, and four-point results. All equality checks pass.
- The explicit active-lab probe additionally checks 2957 awake box-contact poses
  captured before solving at authored ticks 120–239. It stores shape/transform
  values, never persistent entity handles. Every complete manifold and edge-query
  result agrees with the reference.

#### Paired active-lab narrowphase measurements

Release / .NET 10 test process, nine alternating-order warmed batches on the
same 2957 poses. Query delegates have matching signatures. The full-manifold
comparison uses **identical contact-builder/transform glue** with only SAT query
implementations changed; that timed optimized glue is checked against direct
production `Manifold.Collide`. This avoids attributing unequal contact-builder
dispatch to B.

| Work, entire 2957-pose batch | Original median | B median | Reduction |
| --- | ---: | ---: | ---: |
| Edge SAT | 5.263 ms | 2.032 ms | 61.4% |
| Full box-manifold evaluation, common glue | 5.053 ms | 3.007 ms | 40.5% |

Batch ranges were 4.650–6.456 / 1.835–2.515 ms for edge SAT and 4.788–7.545 /
2.813–3.791 ms for manifolds. Edge SAT is evaluated on every captured pair in
isolation; normal collision can reject a pair at face SAT first. These rows are
different experiments, not additive phases. Full-manifold timing excludes ECS
traversal, point matching, round-shape pairs, solver, rollback and rendering;
the 40.5% result is **not** a full-frame or complete ContactSystem speedup.

Unpaired wall-clock runs varied substantially. The pre-B third replay at
ticks 120–239 measured session/physics/narrowphase/solve 0.330/0.292/0.060/0.143 ms.
The first post-B run failed the explicit 1 ms lab gate (physics+mover 1.029 ms),
with multiple unchanged phases elevated as well. A subsequent run passed all
three explicit cases; its third replay measured 0.574/0.504/0.107/0.178 ms, with
mover 0.043 ms. The corresponding awake/contact/constraint counts match.
**Those separate runs do not demonstrate a total-phase speedup**; the paired
comparison above establishes the isolated narrowphase improvement. Timing noise
and JIT/host scheduling remain possible contributors, not confirmed diagnoses.

#### Post-B rendered checks (explicit 60 FPS cap)

Three scripted fresh-world runs; same authored comparison route, renderer,
resolution and overlays, VSync enabled, **MaxFps=60**. ExportRelease uses .NET
8.0.28; Debug project launch uses .NET 10.0.9. All loaded assemblies report the
expected configuration/optimization state, and both captures report no fixture
errors. Third-run rendered-frame wall p50/p95/p99/max, in ms:

| Route ticks | ExportRelease (frames) | Debug (frames) |
| --- | --- | --- |
| 0–119 | 16.664 / 18.211 / 18.650 / 18.665 (121) | 16.909 / 27.404 / 155.459 / 155.459 (93) |
| 120–239 | 16.667 / 18.455 / 18.656 / 18.666 (120) | 39.490 / 57.543 / 61.234 / 61.234 (49) |
| 240–359 | 16.670 / 17.866 / 18.428 / 18.448 (120) | 44.129 / 60.486 / 62.173 / 62.173 (45) |
| 360–479 | 16.672 / 18.485 / 18.656 / 18.660 (119) | 16.512 / 39.825 / 50.742 / 51.035 (106) |

Push-window mean inclusive milliseconds/frame:

| Boundary | ExportRelease | Debug |
| --- | ---: | ---: |
| Server / client update | 0.393 / 2.268 | 8.052 / 31.712 |
| Physics inside server / client simulation | 0.268 / 1.605 | 7.310 / 28.666 |
| Replay simulation inside client update | 1.530 | 23.236 |
| Interpolation inside client update | 0.124 | 0.198 |
| Views / debug callbacks | 0.127 / 0.003 | 0.437 / 0.004 |
| Latest render CPU + setup sample | 0.176 | 0.140 |
| Server / new-client / replay ticks per frame | 1.00 / 1.00 / 6.00 | 2.45 / 2.47 / 7.22 |
| Maximum new-client / replay ticks | 1 / 8 | 4 / 11 |

Do not add nested rows. Replay includes normal snapshot alignment, not just
misprediction recovery. Optimized warmed windows pass the 25 ms diagnostic-p95
threshold; run 2 has no frames over 25 ms. Debug still has sustained CPU-limited
slow frames. Its current push p50 is lower than the older ~198 ms capture, but
cap/pacing, input delivery and resulting replay workloads differ; the exact
before/after frame-speedup magnitude cannot be assigned to B from these runs.
Unaccounted intervals are not assigned to rendering.

Startup remains separate: capped export first enter 685.721 ms, setup 306.903 ms,
initial startup-frame max 165.394 ms; warm setup 22.964/39.145 ms. Debug first
enter 1121.497 ms, setup 952.276 ms, startup-frame max 331.222 ms. Prior uncapped
process startup was much longer; neither implies guaranteed cold driver caches.

#### Verification, artifacts and commands

- Final Release suite: **330/330** non-explicit cases passed, including physics,
  allocation, rollback/full-sync and authored toys. The three explicit diagnostics
  passed separately. Debug and ExportRelease filtered physics/manifold/toy/
  equivalence suites: **130/130 each**.
- Identical two-test rollback filters in Release, Debug and ExportRelease retain
  hashes `b1b19ff21686b874` and `8100b262cc63ef10` and reproduce every per-tick hash.
- Release documented load budgets pass: average regular tick **0.337 ms**, 30-tick
  burst **4.868 ms**, 125-tick burst **25.040 ms**. Worst regular tick 3.993 ms;
  the gate is the average. Steady-state allocation regression passes at zero.
- Client Debug/ExportRelease builds succeed; matching desktop export succeeds.
  The exporter still emits the unrelated shutdown `EditorSettings` message after
  finishing the bundle; the actual application runs and flushes its captures.
- Review found no production numerical/standards issues. Its two validation
  findings (unequal benchmark glue and hidden edge-query differences) were fixed
  and re-reviewed with no remaining actionable findings. No commit was made.

Artifacts are under `/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode/`:
pre-B `physics-lab-route.app`, `lab-route-export-smoke.csv` + JSON;
post-B `physics-lab-b.app`, `lab-b-release.csv`, `lab-b-release-cap60.csv`,
`lab-b-debug-cap60.csv` + JSON sidecars; `lab-b-metal-smoke.trace`.

```sh
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Release \
  --filter FullyQualifiedName~BoxSatEquivalenceTests
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Release \
  --filter FullyQualifiedName~ActiveLabBoxNarrowphaseMatchesAndImprovesOriginal \
  --logger "console;verbosity=normal"
# Run other configurations sequentially; use the usual full-suite/budget commands below.
"<absolute>/physics-lab-b.app/Contents/MacOS/Space" --max-fps 60 -- \
  --level level_pipeline_test --physics-lab-profile <absolute-output.csv> \
  --physics-lab-replay
# For an exact user route: omit replay, repeat warmed pushes manually, close normally.
```

### A completion: 2026-10-03

**Status:** complete for the agreed comparison workload. The user selected **Use
defined lab route**: `(9, 1.5, -33)`, +X for 160 nominal ticks, settling through
route tick 479, 1920×1080, VSync enabled, explicit 60 FPS cap, overlays off. This
supersedes the exact-original-route input blocker above. The historical ~5 FPS
editor session has not been reconstructed, and completion does not imply a
sustained release slowdown was found or fixed.

#### Finished measurement tooling

- `summarize_client_capture.py --window NAME:FIRST_FRAME:LAST_FRAME` selects
  inclusive **engine-frame numbers** from the CSV. Repeat it for startup and
  warmed pushes. `--warm-window NAME` explicitly identifies windows eligible for
  the p95 gate. Manual captures cannot run a warmed gate on their combined
  startup-inclusive distribution. Explicit frame selections also require an
  explicit warmed selection when gating. Automatic replay still gates only the
  final run's route windows. Named selections with no frames are rejected.
  Automatic gates additionally require all three completion records with both
  terminal heads reaching the route end. Completion is recorded before discarding
  the final incomplete frame, which can advance multiple ticks during catch-up.
  Legacy CSVs still summarize; automatic gates require a fresh capture with
  completion metadata, or explicitly selected/labelled warmed frame windows.
- CSV records actual physics/navigation overlay enablement, layer flags and
  selected navigation agent each frame, after input toggles. Summary histograms
  expose mixed overlay states. `debugDrawing` in older metadata remains only the
  initial launch flag. Existing CSVs still summarize without the new columns.
- On macOS, each complete wall interval records `mach_absolute_time` start/end
  timestamps; metadata records the process ID. `metal_capture.py` joins matching
  `xctrace` GPU/time-info/TOC exports using the trace's Mach epoch and timebase.
  It filters **Active execution for the exact capture PID**, merges overlapping
  and nested encoder intervals, then clips their union to each wall interval.
  PID mismatches and captures outside trace coverage fail instead of yielding a
  misleading zero. Native timestamps and all timing data remain outside snapshots.
- The reported metric is **GPU union busy milliseconds per captured wall interval**,
  not latency of an individually submitted/presented GPU frame. Asynchronous work
  can cross interval boundaries. It does not include WindowServer/other processes,
  presentation waiting, or identify every cause of a long frame. CPU and GPU work
  overlap; these durations must not be added together as a frame-time budget.

#### Fresh matched captures and warmed GPU results

Rebuilt/exported the current post-B working tree. All six export assemblies report
ExportRelease with JIT optimization enabled; Debug reports Debug/optimization
disabled. Export runtime .NET 8.0.28, Debug .NET 10.0.9; Godot 4.7.2, M5 (Apple9),
Metal 4.0 / Forward Mobile. Level hash is unchanged. Every capture completed all
three fresh-world runs with no fixture errors and recorded both overlays disabled
throughout. As before, normal transport/prediction makes input frame-sampled and
may change replay counts; these are workflow comparisons, not an isolated
same-runtime compiler or B speedup experiment.

Third-run wall p50 / p95 / p99 / max, milliseconds:

| Route ticks | ExportRelease, untraced (frames) | Debug, untraced (frames) | ExportRelease, Metal-traced (frames) |
| --- | --- | --- | --- |
| 0–119 | 16.665 / 16.999 / 17.125 / 17.261 (120) | 16.686 / 23.410 / 162.690 / 162.690 (99) | 16.670 / 16.937 / 17.472 / 17.687 (120) |
| 120–239 | 16.675 / 16.892 / 17.742 / 18.149 (120) | 32.383 / 42.630 / 45.056 / 45.056 (62) | 16.646 / 17.025 / 17.136 / 49.755 (118) |
| 240–359 | 16.669 / 16.998 / 17.105 / 17.692 (120) | 32.019 / 38.370 / 43.108 / 43.108 (63) | 16.653 / 16.979 / 17.103 / 17.215 (120) |
| 360–479 | 16.674 / 17.177 / 17.825 / 18.113 (119) | 16.598 / 37.588 / 40.826 / 40.841 (100) | 16.676 / 17.055 / 17.655 / 49.825 (118) |

Metal-traced third-run **GPU union busy per wall interval**, milliseconds:

| Route ticks | Mean | p50 | p95 | p99 | Maximum |
| --- | ---: | ---: | ---: | ---: | ---: |
| 0–119 | 3.225 | 2.631 | 6.737 | 8.022 | 11.015 |
| 120–239 | 3.058 | 2.562 | 5.253 | 7.519 | 7.602 |
| 240–359 | 3.742 | 3.338 | 5.663 | 7.098 | 9.275 |
| 360–479 | 3.020 | 2.640 | 4.847 | 6.805 | 9.483 |

Godot GPU samples are still all zero. The independent Metal capture establishes
nonzero warmed GPU execution with headroom on this workload; sustained GPU
saturation is not observed. A separate untraced export checks the baseline without
Metal System Trace overhead; do not claim identical workload or zero profiler
overhead from the similar p95s.

Push-window inclusive means, milliseconds/frame unless noted:

| Boundary | Untraced ExportRelease | Untraced Debug | Metal-traced ExportRelease |
| --- | ---: | ---: | ---: |
| Server / client update | 0.358 / 1.836 | 5.845 / 25.610 | 0.380 / 2.063 |
| Physics inside server / client simulation | 0.233 / 1.162 | 5.277 / 22.849 | 0.286 / 1.460 |
| Replay simulation inside client update | 1.091 | 19.133 | 1.367 |
| Interpolation inside client update | 0.133 | 0.195 | 0.178 |
| Views / debug callbacks | 0.155 / 0.004 | 0.453 / 0.005 | 0.154 / 0.005 |
| Latest render CPU + setup sample | 0.218 | 0.180 | 0.208 |
| Server / new-client / replay ticks per frame | 1.00 / 1.00 / 5.00 | 1.94 / 1.95 / 6.48 | 1.02 / 1.02 / 5.12 |

**Conclusion:** the accepted route reproduces sustained **Debug simulation CPU**
slowdown, amplified by replay and catch-up. Measured server+client physics alone
averages 28.126 ms/Debug push frame. ExportRelease passes the 25 ms warmed-p95
diagnostic gate in all four windows, both traced and untraced; this is a local
diagnostic target, not a portable CI budget. The untraced third run has no
isolated route frames over 25 ms; the traced third run has two. Their entire
unmeasured wall intervals are not assigned to rendering or GPU cost. Rare-spike
root causes and other devices/routes remain follow-up questions, rather than a
blocker to this baseline or evidence supporting C/D/E.

Startup is kept separate: untraced export first enter/setup 1131.688 / 302.800 ms,
startup-frame max 267.157 ms; Debug 762.620 / 677.450 ms, startup max 232.190 ms;
Metal-traced export 3285.842 / 2141.367 ms, startup max 1165.317 ms. Fresh-world setup
on subsequent runs is 24.453 / 22.686 ms (untraced export), 25.153 / 37.415 ms
(Debug), 21.419 / 35.980 ms (traced export). These do not guarantee cold disk or
driver caches and do not attribute the pre-enter interval.

#### Reproduction and verification

Artifacts under `/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode/`:
`physics-lab-a-final.app`, `lab-a-final-release.csv`, `lab-a-final-debug.csv`,
`lab-a-final-metal.csv` (with metadata sidecars), `lab-a-final-metal.trace`,
`lab-a-final-gpu.xml`, `lab-a-final-time.xml`, `lab-a-final-toc.xml`, and launch/export
logs with the `lab-a-final-` prefix. Tables above use these final captures, rerun
after the completion-gate review fix. Earlier `lab-a-*` captures without the
`final` marker remain separate exploratory artifacts. Use a new path for each trace.

```sh
# Build/export as above, then capture the accepted route with explicit pacing.
APP="<absolute>/physics-lab-a-final.app/Contents/MacOS/Space"
OUT="<absolute-existing-output-directory>"
"$APP" --max-fps 60 -- --level level_pipeline_test \
  --physics-lab-profile "$OUT/lab-a-release.csv" --physics-lab-replay
xcrun xctrace record --template "Metal System Trace" --time-limit 45s \
  --no-prompt --output "$OUT/lab-a-metal.trace" --launch -- "$APP" \
  --max-fps 60 -- --level level_pipeline_test \
  --physics-lab-profile "$OUT/lab-a-metal.csv" --physics-lab-replay
xcrun xctrace export --input "$OUT/lab-a-metal.trace" --toc \
  --output "$OUT/lab-a-toc.xml"
xcrun xctrace export --input "$OUT/lab-a-metal.trace" \
  --xpath '/trace-toc/run[@number="1"]/data/table[@schema="time-info"]' \
  --output "$OUT/lab-a-time.xml"
xcrun xctrace export --input "$OUT/lab-a-metal.trace" \
  --xpath '/trace-toc/run[@number="1"]/data/table[@schema="metal-gpu-intervals"]' \
  --output "$OUT/lab-a-gpu.xml"
python3 benchmarks/summarize_client_capture.py "$OUT/lab-a-metal.csv" \
  --metal-gpu "$OUT/lab-a-gpu.xml" --metal-time "$OUT/lab-a-time.xml" \
  --metal-toc "$OUT/lab-a-toc.xml" --max-warm-p95-ms 25

# Manual example: substitute frame ranges from that capture, omit replay at launch.
python3 benchmarks/summarize_client_capture.py "$OUT/manual.csv" \
  --window startup:2:20 --window warmed_push:1210:1270 \
  --warm-window warmed_push --max-warm-p95-ms 25 --json
python3 -m unittest discover -s benchmarks -p 'test_client_capture.py'
```

Verification: Debug/ExportRelease client builds passed with zero warnings/errors;
desktop export and all rendered runs completed. Both release captures pass the
warmed-p95 gate; Debug fails it as expected. Release suite **330/330** non-explicit
tests passed. **6/6** capture regressions exercise encoder union, PID filtering, XML
references, native-clock conversion, interval clipping/coverage, and manual gate
selection/startup exclusion, and incomplete/multi-tick replay completion. Standards
review found no actionable issues. Spec review found an incomplete-replay gate
false-pass and then a stored-tail completion inference problem; explicit terminal
completion metadata fixes both. Re-review found no remaining actionable findings.

### C completion: 2026-10-03

**Status:** implemented as a narrowphase optimization and Box3D-fidelity slice at
the user's explicit request. A is complete for the accepted defined route/settings.
A did **not** demonstrate a sustained ExportRelease physics bottleneck. No solver,
substep, sleep, tolerance, collision-coverage, or rollback-policy setting changed.
D and subsequent roadmap changes remain separate work.

#### Design and reference semantics

`Contact.SatCache` stores an inline unmanaged `BoxSatCache`: invalid / face A /
face B / edge-pair state, reference-face or edge indices, original separation,
both local hull geometries, and ordered shape GIDs. This is geometric history,
independent of the existing normal/friction/rolling/twist impulse persistence.
`Manifold.Collide` has a persistent-cache overload; the original four-argument
overload remains full-search-only so B's frozen-oracle tests stay meaningful.

The port follows `b3SATCache` in `references/box3d/include/box3d/types.h` and
`b3CollideHulls` plus its contact builders in `src/convex_manifold.c`:

- A cached face recomputes full-box support separation in the reference frame.
  Separation **>= speculative distance** is a hit returning an empty manifold.
  Otherwise rebuild the incident-face clipping, including B-to-A transform and
  feature flipping for a B reference. Reuse requires nonempty points and
  **absolute clipped-separation drift < linear slop** from the stored baseline.
  Store the pre-reduction minimum clipped separation, as the reference does.
- A cached edge must have valid indices, intersecting Gauss-map arcs, and a
  meaningful nonparallel axis. Recompute its separation with full-box supports;
  **> speculative distance** is a separating hit. Otherwise rebuild the finite
  edge contact, reject closest points outside either segment, and require
  **absolute contact-separation drift < linear slop**.
- Successful hits retain the original indices and separation baseline. Updating
  that baseline every hit would allow accumulated drift to bypass the reference's
  validity test. Failed candidates are discarded completely before full SAT.
  Full SAT caches its separating feature or the feature that successfully built
  the selected manifold; failed contact construction leaves the feature invalid.
- Preserve this port's existing normalized-edge fixed-point Gauss-map epsilon
  and cross-axis parallel guard. Unlike Box3D's floating-point interpolated-normal
  edge line-separation check, cached edge rejection uses the same full-box support
  correction as the full query. Restoring the reference's line-only rejection
  would reintroduce the tilted-crate/large-ground failure. B's stack-local edge
  arrays, transformed-endpoint subtraction/normalization, traversal and strict
  tie-breaking are unchanged. Full-search face/edge selection also remains B's
  existing selection, rather than switching to newer reference selection rules.

`BoxSatResult` is returned per call rather than stored authoritatively. Hit/full
search/fallback counters accumulate only in `PhysicsRuntime.Pending` and publish
through `PhysicsDiagnostics.LastStep`; this runtime resource has no snapshot GUID.
A full search includes cold/invalid-feature misses. `BoxSatFallbacks` separately
counts full searches which started with an existing feature, including invalidation.

#### Invalidation, lifecycle and snapshots

- Supported live geometry mutation exists: `ShapeOperations.SetHull/SetBox`,
  `SetSphere` and `SetCapsule` destroy affected contacts and forget pairs before
  rebuilding geometry, mass and proxies. New contacts are default-initialized
  with an invalid cache. Removal, body disable/enable, teleport, and body/shape
  teardown cannot transfer an old feature to a new contact or reused entity slot.
- The persistent overload additionally compares local centers, local rotations
  and half-extents **exactly**, plus ordered shape GIDs. Geometry or identity/order
  changes clear history. Quaternion `==` is approximate in this math library;
  typed `Equals` is used for this guard. Non-box pairs clear the cache. Transforms
  from ordinary simulation motion are handled by feature validity, not blanket
  invalidation. Sleeping pairs retain the cache with their unchanged manifolds;
  supported mutations destroy contacts/wake bodies before reuse.
- Direct shape/body field writes are not a substitute for the mutation APIs:
  this defensive geometry guard does not repair stale mass/proxies or wake a
  directly modified sleeping body. Persistent identity uses `EntityGID` only.
- Inspected StaticEcs component registration and serialization:
  `GameTypes.Register` already registers `Contact`; default unmanaged strategies
  copy its whole inline layout in world snapshots, and entity serialization also
  copies unmanaged component data. Both `GameWorldRollback` and
  `GameWorldFullSyncHandler` use world snapshots with hard reset. No extra cache
  resource/type registration is needed. `Contact` now supplies
  `IComponentConfig<Contact>` **schema version 1** so old v0 raw layouts are not
  silently interpreted as the new layout; no v0 migration is supplied. Peers and
  snapshots must use the matching schema. Diagnostics remain outside these bytes.

#### Verification and review

- **13 focused cache cases** pass: separating faces and edges; face A/B and edge
  hits; baseline retention/accumulated drift; invalid indices; parallel/Gauss-map
  invalidation; empty face clipping; finite-segment endpoint rejection; geometry
  and ordered-identity changes; round-pair clearing; contact creation/removal,
  geometry mutation, disable/enable and entity-slot reuse.
- The 4096-pose deterministic edge corpus contains **168 contact-edge hits and
  10 separating-edge hits**, with full-oracle fallback after rotation invalidates
  the edge feature. An independent test transcription of reference reuse rules
  verifies 1024 moving sequences × 12 evaluations, with nonidentity body/local
  rotations, local offsets and alternating shape order. Decisions are
  full/separation/face/edge **1698/4207/6363/20**. Complete rebuilt cached manifolds,
  including flipped features and all point slots, agree with reference cached
  reconstruction; fallbacks agree with B's cold-search oracle. Cached winners are
  not required to equal a different cold-search winner.
- A real populated-contact test restores cache bytes/GIDs through production
  rollback and full sync into a distinct world type, then reproduces **every one
  of 180 tick hashes**, including a wake/push. The same three-test filter in
  Debug, Release and ExportRelease matches hashes: `box-cache-replay
  4d9f8d96c3b0d49c`, `rollback-replay f0aeaca8f9bc1605`, `sleep-replay
  9c1b175f84e92491`. Hashes differ from B because Contact's schema/layout and
  geometric history changed; equality is required across replay/configurations,
  not against B's state bytes. Test order also affects snapshot identity/history.
- Final full Release suite: **343/343** non-explicit cases passed. Debug and
  ExportRelease physics/manifold/toy/cache/equivalence/platform sets:
  **150/150 each**. These exercise pushed/toppled crates on large ground, dense
  authored stacks, stable warm-start features, mover pushing/grounding, moving
  platforms, sleep/wake and support destruction. B's four full-search equivalence
  tests pass unchanged. Client Debug/ExportRelease builds pass without warnings
  or errors. All configuration restores/builds/tests ran sequentially.
- Steady-state load-scene allocation remains **0 bytes/tick**. Documented Release
  budgets pass: regular-tick average **0.299 ms** (worst 1.172 ms; average is the
  gate), 30-tick rollback **5.929 ms**, 125-tick rollback **27.080 ms**.
- Standards review found no actionable issues. Spec review found missing complete
  cached-hit reconstruction checks and insufficiently asserted edge-specific
  rejection coverage; both were added and re-reviewed with no remaining findings.
  Existing uncommitted A/B, lab, physics and unrelated changes were retained.

#### Cache effectiveness and paired measurements

Release, .NET 10.0.9 / SDK 10.0.301, local M5/macOS; same authored headless route,
three fresh-world runs in one process. Actual ContactSystem counters repeat
identically across all three runs:

| Window | Box evaluations | Face / edge / separation hits | Full searches (fallbacks / cold misses) | Hit rate | Sleeping contacts skipped |
| --- | ---: | --- | --- | ---: | ---: |
| Push, ticks 120–239 | 3018 | 1933 / 0 / 838 | 247 (205 / 42) | 91.8% | 10137 |
| Late, ticks 360–479 | 2933 | 1869 / 0 / 1017 | 47 (20 / 27) | 98.4% | 11164 |

Fallback rates over all evaluated box pairs are **6.8% / 0.7%**, with cold/invalid
misses **1.4% / 0.9%**. Awake edge contact hits are uncommon on this route (two
in ticks 240–359); the deterministic corpus verifies that path separately.
The late scene still contains awake props/platforms; it is not an all-asleep scene.

Capture existing eligible awake box contacts **before ContactSystem** on the third
run. Store values and GIDs, never entity handles. New pairs first created inside
ContactSystem are absent from this capture, explaining 2993 captured push poses
versus 3018 actual evaluations. Time nine alternating-order warmed batches on the
same frozen poses and incoming caches. Each cached evaluation starts from a local
copy of its incoming history; it does not repeatedly warm one pose's cache.
Both variants call production collision directly, including identical contact
builders/transforms. Cache eligibility/geometry guards are included in C's timing.

| Full batch | Poses | B full-search median (range), ms | C cached median (range), ms | Isolated reduction |
| --- | ---: | --- | --- | ---: |
| Push 120–239 | 2993 | 7.478 (5.765–7.932) | 2.895 (2.345–3.298) | 61.3% |
| Late 360–479 | 2933 | 3.827 (3.607–10.539) | 1.178 (1.155–1.291) | 69.2% |

An earlier paired run also improved (52.1% push, 63.3% late), with noisier ranges.
These gains exclude ECS traversal, point matching, non-box pairs, solver, snapshot
copying, replay and rendering. Capturing histories itself allocates diagnostically
and adds traversal to the session measurement; it is outside the allocation gate.

The separate existing **uncaptured** lab tick diagnostic passes both explicit
cases. Third full-lab run reports session/physics/narrowphase/mover **0.598 / 0.492 /
0.051 / 0.065 ms** during push and **0.407 / 0.331 / 0.044 / 0.047 ms** late.
Those are post-C absolute costs, **not a paired complete-tick speedup versus B**.
Changed cached-feature selection also changes trajectories/awake-contact counts.
Separate wall-clock runs varied noticeably; no complete tick or rendered-frame
gain is inferred. No new rendered-client performance capture was made for C.

Maximum dense-lab snapshot is **281878 / 640000 bytes**; the final diagnostic's
world copy is **281814 bytes**, **0.197 ms/call**, **2456 B/call**. Snapshot growth
and copying cost are tradeoffs of authoritative geometric history; the copy
figures are unpaired, not an isolated cache-size regression benchmark. The lab
remains within the fixed rollback buffer, but larger levels need their own capacity
check. No general-hull/mesh cache states or later roadmap optimizations were added.

#### Reproduction

```sh
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Release \
  --filter FullyQualifiedName~BoxSatCacheTests --logger "console;verbosity=normal"
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Release \
  --filter FullyQualifiedName~PairedActiveAndSettledLabFeatureCache \
  --logger "console;verbosity=normal"
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Release \
  --filter FullyQualifiedName~AuthoredLabTickBudget --logger "console;verbosity=normal"

# Run Debug then ExportRelease sequentially, substituting CONFIG below.
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c CONFIG \
  --filter 'FullyQualifiedName~PhysicsScenarioTests|FullyQualifiedName~ManifoldBoxBoxTests|FullyQualifiedName~PhysicsToyTests|FullyQualifiedName~BoxSatEquivalenceTests|FullyQualifiedName~BoxSatCacheTests|FullyQualifiedName~PlatformTests'
dotnet build Client/Space.csproj -c CONFIG

# Use the identical filter in each configuration when comparing hashes.
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c CONFIG \
  --filter 'FullyQualifiedName~RollbackAndDistinctWorldFullSyncRestoreCacheAndEveryTickHash|FullyQualifiedName~RollbackStateHashReplayTest|FullyQualifiedName~SleepRollbackReplayTest' \
  --logger "console;verbosity=normal"
dotnet run --project benchmarks/GameCore.Benchmarks/GameCore.Benchmarks.csproj \
  -c Release -- --physics --enforce
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Release
```

### D completion: 2026-10-03

**Status:** implemented and verified after committing the accepted pre-D baseline
as **`665585a`** (`Add physics lab profiling and persistent box SAT caching`). That
commit includes A–C, authored lab content, physics fixes and the existing
navigation/level work, as explicitly requested. Its formatting hook passed after
applying the repository formatter. Submodule `bin/obj` artifacts were not committed.
D is committed separately from that pre-D baseline.

#### Packed gather / solve / scatter

`ContactSolverSystem` now gathers awake non-static bodies in the existing ECS
query order into `PhysicsRuntime.SolverStates`. `SolverBodyState` holds only the
hot velocities, accumulated deltas, inverse mass and world inverse inertia.
`SolverBodyInput` separately holds cold force/torque, gravity/damping, motion-lock
and fast-rotation inputs. Both are reused contiguous lists, exposed as spans only
after all gathers are complete. No list can grow while those spans are in use.

Prepared constraints retain tick-local entity handles for final impulse/event
storage and integer `IndexA/IndexB` for the hot solve. Warm start, biased solve,
relaxation, restitution and substep integration use indexed state rather than
repeated `Entity.Ref<Body>()` lookup. The port follows the reference's indexed
`b3BodyState`/`b3BodySim` pattern in `references/box3d/src/contact_solver.c` and
`solver.c`; it does not add graph coloring, SIMD, scheduling or gyroscopic torque.

- Awake entries form an integration prefix; referenced static bodies are appended
  only when a prepared constraint needs them. The existing body-index dictionary
  deduplicates participants, so multiple contacts see the same static or dynamic
  working state. Unlike Box3D's identity dummy for statics, this first exact-result
  version gathers each referenced static body's actual values. It preserves this
  port's shared-static-body behavior and never integrates or resets static deltas.
- Awake deltas are reset at the same boundary as before. All fixed-point
  expressions, constraint/point order, warm-start order, force/damping integration,
  speed clamps, motion-lock application, normal/friction/rolling/twist/restitution
  operations and substep count are unchanged.
- Scatter writes **only linear/angular velocity and accumulated translation/
  rotation** back to each participant, once after restitution and **before CCD**.
  CCD reads authoritative ECS velocities/sweeps and can edit them. Hit-event
  anchors subsequently read the CCD-adjusted ECS state; finalization updates
  transforms/inertia and sleeping as before. No later packed-state scatter can
  overwrite CCD clipping, waking, disabling or sleep decisions.
- ECS `Body` and `Contact` remain authoritative at system/snapshot boundaries.
  The packed arrays/index map are derived scratch in the existing unserialized
  `PhysicsRuntime`. No component layout/schema or registration changes are needed.
  Rollback/full sync can retain that runtime resource safely: every update clears
  and rebuilds scratch. A `finally` clears all solver body/constraint handles,
  packed arrays, indices and island scratch even if an update fails. Capacities
  remain reusable; no `Entity` handle is retained logically between ticks.
- Gather/index construction is timed in SolverPrepare; solving **and scatter**
  are timed in Solve. Entry/finally scratch clearing is outside the existing
  per-phase timers, but inside the complete session-tick measurement. Report both
  rather than treating the sum of phase timings as the complete session cost.
  Array/dictionary growth remains structural warm-up work, not steady-state GC.

#### Exact-result and fidelity verification

`PackedSolverTests.AuthoredLabMatchesEveryPreDTickHash` records the full authored
480-tick push/settling route from the committed ECS solver **before any D production
edits**, with a fixed player GUID. The fixture stores all 480 FNV-1a world-snapshot
hashes, not a final-pose or final-hash proxy. D matches **every tick exactly** in
Release, Debug and ExportRelease, including the same C cache histories, contact
features/impulses, broad phase, gameplay and sleep state. The encoded fixture is
the pre-D oracle; it must not be regenerated from the packed solver to make a
failure pass. Snapshot schema changes or intentional gameplay changes will need
explicit rebaselining against an identified reference implementation.

The second focused test full-snapshot-restores a populated world with an awake
box contacting ground, deliberately poisons the surviving runtime state buffer,
body-index map and state/entity lists, and compares **every one of 30 replay tick
hashes**. It requires nonzero awake-body and prepared-constraint counts and verifies
all tick-local scratch is empty after every update. Existing production rollback,
distinct-world full-sync, sleep/wake and CCD regressions also pass.

Final verification (configurations run sequentially):

- Full Release suite **345/345** passed, including zero steady-state physics
  allocation, pre-D equivalence, C reference/fallback checks, pushed/toppled crates
  and dense stacks, normal/friction persistence, motion/force mutation, mover and
  platform behavior, sleeping/support removal, bullet/rotational CCD, rollback
  and distinct-world full synchronization.
- Debug and ExportRelease physics/manifold/toy/cache/equivalence/platform/packed
  sets **152/152 each** passed. Client builds in both configurations have zero
  warnings/errors. C's original full-search equivalence tests are unchanged.
- Default-runtime Release budgets pass: average regular tick **0.226 ms**, worst
  **1.531 ms** (the gate is the average); 30-tick rollback **3.775 ms**, 125-tick
  rollback **16.321 ms**. These are a fresh unpaired budget run, not isolated D
  before/after gains. Steady-state allocation remains **0 bytes/tick**.
- Standards review found no actionable issue. Spec review found that the first
  poisoned-scratch fixture did not guarantee a prepared contact. It now explicitly
  creates an awake ground-contacting box, asserts actual solver work, and poisons
  both mappings and state arrays. Re-review found no remaining actionable issues.

#### Controlled active / late measurements and limits

Both comparison executables use **ExportRelease, .NET 10.0.9**, the same authored
level, headless input route and unchanged simulation settings. The pre-D executable
was retained from C verification; the candidate was built into a separate ignored
`tests/GameCore.Tests/bin/PackedComparison/` tree so building D did not overwrite
the reference before comparison. Each process runs three fresh worlds; compare
only run 2. Body/constraint/CCD counts match, and the independent golden regression
confirms the route's complete authoritative state is unchanged.

Default-tiered runs varied with promotion/host scheduling. One final-candidate
comparison had push Solve **0.133 → 0.126 ms**, but complete session **0.278 →
0.295 ms**; late Solve **0.111 → 0.116 ms**, session **0.191 → 0.217 ms**. This
does not establish a default-runtime total-phase speedup.

To control ongoing tiered-JIT promotion, ran a separately labelled **ABBA**
comparison with `DOTNET_TieredCompilation=0` for **both** pre-D and D executables.
This affects compilation policy only, not authoritative simulation or project
defaults. Two chronological pairs, third replay, mean milliseconds/tick:

| Window / pair | Pre-D Solve | D Solve (including scatter) | Pre-D prepare | D prepare | Pre-D session | D session |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Push 120–239, A→B | 0.053 | 0.049 | 0.016 | 0.016 | 0.132 | 0.125 |
| Push 120–239, B→A | 0.054 | 0.052 | 0.016 | 0.017 | 0.133 | 0.131 |
| Late 360–479, A→B | 0.048 | 0.042 | 0.015 | 0.015 | 0.101 | 0.092 |
| Late 360–479, B→A | 0.048 | 0.048 | 0.015 | 0.016 | 0.102 | 0.101 |

The controlled active solve reduction is modest, about **4–8%** using rounded
reported means; late results overlap (**0–12.5%**). This is phase-level evidence
on a deterministic complete workload, not the stronger same-process frozen-row
microbenchmark used for B/C. Only two pairs were collected, and other phases vary
slightly too; complete-session rows are observations, not an isolated causal tick
gain. No new rendered-client capture or frame-rate improvement is claimed. A did
not establish an ExportRelease physics bottleneck. Wider hardware/workloads and
default tiered-JIT behavior still require their own measurement.

#### Reproduction

```sh
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Release \
  --filter FullyQualifiedName~PackedSolverTests

# Run Debug and ExportRelease sequentially; extend C's broad filter with PackedSolverTests.
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c CONFIG \
  --filter 'FullyQualifiedName~PhysicsScenarioTests|FullyQualifiedName~ManifoldBoxBoxTests|FullyQualifiedName~PhysicsToyTests|FullyQualifiedName~BoxSatEquivalenceTests|FullyQualifiedName~BoxSatCacheTests|FullyQualifiedName~PlatformTests|FullyQualifiedName~PackedSolverTests'
dotnet build Client/Space.csproj -c CONFIG
dotnet run --project benchmarks/GameCore.Benchmarks/GameCore.Benchmarks.csproj \
  -c Release -- --physics --enforce
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Release

# Compile a pre-D checkout of 665585a and D into different output roots first.
# Use outputs inside their checkout trees so the lab diagnostic can locate Client/maps.
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c ExportRelease \
  -p:BaseOutputPath="<absolute-checkout>/tests/GameCore.Tests/bin/Comparison/" \
  --filter 'FullyQualifiedName~AuthoredLabTickBudget&Name~True'
# Run the two compiled test DLLs sequentially in A,B,B,A order. No rebuild in this loop.
DOTNET_TieredCompilation=0 dotnet vstest "<absolute-output>/ExportRelease/net10.0/GameCore.Tests.dll" \
  '--TestCaseFilter:FullyQualifiedName~AuthoredLabTickBudget&Name~True' \
  '--logger:console;verbosity=normal'
# Omit the environment override for default-tiered measurements; label them separately.
```

### Combined B+C+D rendered measurement and E assessment: 2026-10-03

**Decision: defer E as a performance priority on the accepted M5 workload.**
Copying is redundant and allocates, but accounts for only about 0.12 ms/export
push frame and 0.24 ms/Debug push frame. It does not explain Debug's remaining
simulation/replay stalls or export wall-time tails. E–H were not implemented.

#### Source, instrumentation and verified workload

Started with a clean main working tree at **`531c9c1`**, following `665585a`.
Both rollback submodules contained only untracked `Runtime/bin/` and `Runtime/obj/`
directories; these were preserved. Built Debug, captured it, then built and
exported ExportRelease sequentially into a **new** `physics-lab-bcd.app` bundle.
The application was run twice, each process recreating three fresh worlds. No
old bundle was used as the current candidate. Simulation and snapshot source is
the committed D baseline; the only client changes are diagnostic attribution.

Existing `interpolation_ms` already brackets serialize + previous-world hard-reset
load. Added actual call count, serialized snapshot byte count, and
`GC.GetAllocatedBytesForCurrentThread()` deltas around that synchronous boundary.
These counters live in `ClientPerformanceCapture.Frame`, outside authoritative
snapshots. They exclude diagnostic frame/list storage and output allocations,
but include any serializer/load allocations and first-use growth within the copy.
They measure current-thread managed allocation, not native allocation or retained
memory. Timing includes the small diagnostic bookkeeping inside the boundary;
its observer overhead was not separately measured. No copy or simulation behavior
was replaced. The summarizer supports legacy CSVs, reports copy totals/per-call
values and actual tick totals, and exposes simulation/narrowphase/solve distributions
when present. `metal_capture.py` was inspected and remains unchanged.

All six loaded assemblies report **Debug / JIT optimization disabled** or
**ExportRelease / JIT optimization not disabled**, respectively. Debug runtime
is **.NET 10.0.9**; export is **.NET 8.0.28**. All captures report Godot
**4.7.2-stable official**, **Apple M5 (Apple9)**, **Metal 4.0 / Forward Mobile**,
**1920×1080**, **VSync Enabled**, **MaxFps=60**, with both overlays disabled in
every recorded frame. All three worlds use the unchanged 156-placement level hash
`6AB8DFF2116D59D270A13CF10BB5E3319D91B4F8D36BF247673DD57F7A484BE5`.
Route remains `(9, 1.5, -33)`, nominal +X for 160 ticks, settling through 479.
Fixture errors are empty. Debug terminal server/client heads are
`540/545, 540/544, 540/544`; primary export `540/544, 540/543, 540/543`.
The repeat export also passes the explicit three-run completion gate.

#### Warmed rendered-frame distributions

Final run only, nearest-rank **wall p50 / p95 / p99 / max**, milliseconds.
Windows use server head at frame start; a catch-up frame can cross a boundary.
Startup and final incomplete wall intervals are excluded from these windows.

| Route ticks | Debug (frames) | ExportRelease (frames) | ExportRelease repeat (frames) |
| --- | --- | --- | --- |
| 0–119 | 16.728 / 22.004 / 56.239 / 82.223 (106) | 16.658 / 19.672 / 22.581 / 22.657 (120) | 16.702 / 21.831 / 22.646 / 22.712 (120) |
| 120–239 | 25.311 / 33.706 / 37.717 / 37.717 (79) | 16.704 / 19.639 / 21.090 / 22.587 (120) | 16.630 / 20.421 / 21.941 / 22.282 (120) |
| 240–359 | 18.793 / 26.076 / 28.189 / 29.015 (105) | 16.622 / 22.114 / 23.771 / 24.069 (120) | 16.585 / 22.694 / 23.301 / 23.404 (120) |
| 360–479 | 16.554 / 20.005 / 20.903 / 22.119 (119) | 16.658 / 21.815 / 22.759 / 24.949 (119) | 16.689 / 21.904 / 22.360 / 22.571 (119) |

Both exports pass all four 25 ms warmed-p95 diagnostic windows; neither final
run has a route frame above 25 ms. Debug fails push and 240–359 p95; its route
frames above 25 ms number **3 / 41 / 8 / 0**. This is still a local diagnostic
threshold, not a portable CI budget. Repeat export checks the unresolved elevated
tails; it is retained as a separate observation, not pooled into a better result.

Actual callback **totals**, server / new client / client replay, in those frames:

| Route ticks | Debug | Primary export | Repeat export |
| --- | --- | --- | --- |
| 0–119 | 119 / 120 / 601 | 120 / 120 / 600 | 120 / 121 / 576 |
| 120–239 | 121 / 121 / 511 | 120 / 120 / 600 | 120 / 120 / 600 |
| 240–359 | 119 / 120 / 657 | 120 / 121 / 704 | 120 / 120 / 600 |
| 360–479 | 119 / 119 / 802 | 119 / 119 / 713 | 119 / 119 / 594 |

These are complete stored-frame counts, not inferred head advances. The discarded
terminal frame can contain additional work. Replay includes ordinary aligned
snapshot resimulation, not only misprediction recovery.

Push-window means in **ms/rendered frame**, with inclusive boundaries:

| Boundary | Debug | Primary export | Repeat export |
| --- | ---: | ---: | ---: |
| ClientGame process | 24.174 | 1.960 | 1.882 |
| Offline server / client update | 3.894 / 20.243 | 0.312 / 1.621 | 0.318 / 1.544 |
| Server simulation / new-client simulation / replay simulation | 3.840 / 3.749 / 15.750 | 0.282 / 0.196 / 1.004 | 0.293 / 0.192 / 1.004 |
| Physics inside server / client simulation | 3.370 / 17.411 | 0.213 / 1.059 | 0.217 / 1.048 |
| Narrowphase inside server / client physics | 0.503 / 2.585 | 0.032 / 0.147 | 0.034 / 0.137 |
| Solve (including D scatter) inside server / client physics | 1.747 / 9.155 | 0.096 / 0.544 | 0.095 / 0.537 |
| Interpolation copy inside client update | 0.242 | 0.119 | 0.116 |
| Views / disabled debug callbacks | 0.535 / 0.005 | 0.141 / 0.004 | 0.129 / 0.004 |
| Latest engine render CPU + setup samples | 0.223 | 0.239 | 0.206 |
| Server / new-client / replay ticks per frame, mean | 1.532 / 1.532 / 6.468 | 1.000 / 1.000 / 5.000 | 1.000 / 1.000 / 5.000 |
| Server / new-client / replay ticks per frame, maximum | 2 / 2 / 9 | 1 / 1 / 7 | 1 / 1 / 7 |

Do **not** add nested rows. Physics is the sum of instrumented physics phases,
not every instruction in the complete simulation boundary. Simulation also includes
gameplay, mover and phase gaps. Server/client updates include transport and snapshot
work; copying is nested inside the client update. Views run outside ClientGame.
Engine render samples may lag; wall intervals include pacing/OS waiting. Godot GPU
counters remain zero. No fresh GPU attribution was needed to assess this small
CPU-copy boundary; A's independent Metal trace remains a separately labelled
GPU baseline, not a measurement of this candidate. Unmeasured wall time is **not
assigned to rendering**, and GPU and CPU durations must not be added.

#### Startup and fresh-world costs

| Observation | Debug | Primary export | Repeat export |
| --- | ---: | ---: | ---: |
| Engine uptime at first enter, ms | 739.802 | 4027.322 | 626.632 |
| Run 0 setup body, ms | 701.834 | 1369.004 | 292.695 |
| Run 1 / 2 setup body, ms | 22.785 / 30.074 | 20.831 / 33.922 | 19.695 / 35.642 |
| Run 0 pre-route wall p50 / p95 / p99 / max, ms | 86.114 / 193.264 / 193.264 / 193.264 | 17.451 / 565.217 / 783.819 / 783.819 | 33.485 / 105.040 / 152.595 / 152.595 |

Process-cold is not guaranteed cold filesystem/driver caches. Pre-enter time has
no internal attribution. Primary export connection/startup stalls continued into
its first route; client simulation was initially absent there. That run is not a
warmed performance sample. Fresh-world pre-route copy allocation on run 2 totals
**1,260,360 B/14 calls** (Debug), **1,322,104 B/50 calls** (primary export), and
**1,322,928 B/51 calls** (repeat). This includes previous-world pool growth and
is kept separate from warmed route-copy allocation.

#### Copy attribution and E payoff

Primary captures, final run. Calls / mean ms per call / mean allocated bytes per
call / mean serialized bytes per call:

| Route ticks | Debug | ExportRelease |
| --- | --- | --- |
| 0–119 | 105 / 0.201 / 2019 / 271683 | 120 / 0.118 / 2058 / 272408 |
| 120–239 | 79 / 0.242 / 2340 / 279158 | 120 / 0.119 / 2349 / 280077 |
| 240–359 | 105 / 0.210 / 2323 / 280462 | 120 / 0.197 / 2389 / 280348 |
| 360–479 | 119 / 0.167 / 2333 / 280457 | 119 / 0.158 / 2338 / 280347 |

Push per-frame copy p50/p95/p99/max is **0.217/0.343/0.955/0.955 ms**
Debug and **0.106/0.181/0.254/0.629 ms** export. Copy calls are normally once
per update, not once for every replay tick; run-2 Debug's first window has one
frame with no copy. Export repeat push measures **0.116 ms/call, 2401 B/call**.
At 60 calls/s this is roughly **140–144 KB/s managed garbage** and **16.8 MB/s
serialized payload**; deserialize/load also traverses that state. Payload size
is not total memory traffic. All fields except previous Transform and identity
are unnecessary for current interpolation: contacts (including C SAT history),
broad-phase trees/pairs, bodies/velocities, configuration, gameplay and resources.
No byte-by-component or selective-copy benchmark was collected, so a precise
redundant-byte percentage or achievable E speedup is not claimed.

Even eliminating the entire measured push copy would recover only **0.7% of a
16.67 ms export frame budget** and **1.0% of Debug's measured ClientGame process**
before paying for replacement capture/lookups. Copy is about 6–8% of export client
update CPU, so it is a useful cleanup opportunity, but not a demonstrated
responsiveness bottleneck here. There is no measured GC-pause-to-tail correlation.

#### Comparison to A and what is established

Re-read A's retained `lab-a-final-{debug,release}.csv` and metadata. Settings,
level hash, renderer/hardware and each workflow's runtime match. **A final is
already post-B**; comparison measures the current **B+C+D candidate versus B**,
not an isolated total A→B+C+D gain. It predates the two named commits as recorded
working-tree artifacts and is not a new paired rebuild of a pre-B commit.

- **Debug responsiveness improves observationally:** push p50/p95 changes
  **32.383/42.630 → 25.311/33.706 ms**, and 240–359 **32.019/38.370 →
  18.793/26.076 ms**. Push replay mean is almost equal (6.484→6.468), but
  server/new-tick catch-up falls from 1.935/1.952 to 1.532/1.532. Callback-normalized
  push complete simulation is **2.998→2.507 ms/server tick**, **2.954→2.437
  ms/client tick**. Simulation CPU and replay still dominate. The lower costs and
  reduced catch-up are consistent with improvement, not a controlled attribution
  of every percentage to C/D. Debug late p50 stays near 16.6 ms, with lower tails.
- **Export simulation cost improves modestly in push, not consistently across
  all windows:** callback counts there match exactly (120/120/600). Complete
  server/client simulation changes **0.304→0.282 / 0.218→0.200 ms per tick**;
  server+client physics/frame **1.395→1.272 ms**. Initial server simulation and
  both late-window costs do not all improve. Separate runs, tiered-JIT/host
  variation and C's changed trajectories prohibit a causal complete-tick gain
  claim. B/C isolated gains and D's controlled phase gains remain separate evidence.
- **Export frame tails do not improve:** primary p95 is **19.639 ms** push versus
  A's **16.892 ms**, repeat **20.421 ms**. All primary/repeat export windows remain
  paced near 60 Hz and pass the diagnostic gate. The elevated tails are unresolved;
  the small measured CPU boundaries do not explain all wall variation.

Input is still delivered through frame-sampled transport/prediction; dropped late
inputs/mispredictions occur during stalls, changing the effective movement and
prop trajectories. C changes cached-feature history/selection and snapshot size.
Diagnostic allocation/byte attribution is new; A measured copy time only. Neither
workflow is a bit-identical headless route or same-runtime Debug/export experiment.
One Debug process and two export processes establish local observations, not
statistical proof of gains across machines. Stronger causal attribution would
require alternating fresh paired builds from identified commits with equal
instrumentation/content and matched actual workload counters. It is not necessary
to reject E as the immediate fix for the measured remaining Debug stall.

#### Audited E boundary and required verification before implementation

The complete production previous-world reader audit (`WP` / `GameWorldPrev`)
finds only **`TransformViewBehavior.TrySamplePose`** reading previous components.
`EntityViewUpdater` also calls it to place new views before entering the tree.
Required previous data: **EntityGID + Transform.Position + Transform.Rotation +
Transform.TeleportTick**. PlayerInfo/correction channel are read from the **current**
world. Player presentation reads current Mover, PlayerInfo, input and PendingShot;
its prior grounded/vertical state is presentation-local. BoxShapeSize reads current
Shapes/Shape on assignment; ParticleReset reads current Body velocity; Camera reads
current PlayerInfo. None needs a previous Body, Contact, shape, tree or session.
Remaining WP occurrences are setup/teardown, snapshot copying and XML documentation;
the headless copy probe uses its own separate previous-world type.

If approved later, the smallest E slice is a **reused, presentation-only previous
pose table keyed by full EntityGID**, populated at the existing interpolation
receiver callback. Initially capture all Transform entities to preserve the public
sampler's behavior; narrowing to ViewId requires proving all sampler callers fit
that restriction. Preserve the existing pre-final-tick boundary after rollback
replay, including the time-budget-stop callback, and the explicit post-load full-sync
callback. Do not substitute the previous rendered frame or capture every replay tick.
Current pose remains read from W; conversion/interpolation order and Alpha remain
unchanged. No simulation component/schema, snapshot or physics settings change.

- **Spawn/despawn:** clear/replace table membership at each capture. Missing
  previous pose falls back to current; missing current entity returns false and
  existing view reconciliation removes it. No ghost/deferred despawn semantics.
- **Rollback and slot reuse:** full GID keys include generation; never retain
  Entity handles or ECS refs. Rebuild from the corrected pre-final-tick world, so
  resimulation-created generations replace discarded history. Preserve
  `ProjectileOrigin(channel, spawnTick)` **view/trail identity** and GID rebind in
  EntityViewUpdater, including its duplicate-origin fallback. Do not use origin
  alone to blend poses across a GID change; current semantics fall back when no
  matching previous GID exists. Cross-GID blending would be a separate change.
- **Teleports:** retain and compare TeleportTick; mismatch snaps to current.
  Capture fixed values so later authoritative writes cannot mutate prior samples.
- **Correction smoothing:** retain correction probe/smoother lifecycle and apply
  the current channel offset **after** interpolation exactly as today. Do not
  bake correction offsets into the table or apply them twice.
- **Full sync/reload:** replace pose membership from the loaded timeline at the
  existing callback; never blend stale pre-sync values into a replacement world.
  Reset the table on client create/destroy; validate callbacks/Alpha on zero-tick
  frames and full-sync-plus-forward simulation within the same update.

Verification gates for an approved implementation: differential pose sampling
against the WP oracle across Alpha 0/intermediate/1, rotations, no-tick/multi-tick
frames, rollback GID changes/slot reuse, spawn/despawn, teleports and full sync;
projectile rebind/trail/pool placement and corrected player tests; rendered moving
props/platforms/characters/projectiles remain smooth. Reproduce all 480 D golden
hashes and focused rollback/full-sync checks across configurations if integrating
the presentation boundary, without regenerating the oracle. Require zero
steady-state pose-capture allocation after capacity warm-up, bounded lifecycle
storage, unchanged authoritative snapshots and preserved B/C/D fidelity gates.
Re-capture the same rendered workloads, comparing inclusive update, copy/capture
time and allocations rather than claiming that smaller payload guarantees FPS.

#### Artifacts, reproduction and verification

Retained A artifacts were found intact. New artifacts are under
`/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode/`:
`physics-lab-bcd.app`, `lab-bcd-debug.csv`, `lab-bcd-release.csv`,
`lab-bcd-release-repeat.csv`, all with `.csv.json` metadata. The local
`analyze-bcd.py` records summaries of A and all BCD captures as
`<capture-name>-bcd-summary.json`; these are derived artifacts, not raw captures.
Build/export/launch console output is retained in the session tool output.

```sh
OUT="/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode"
GODOT="$HOME/Applications/Godot_mono-4.7.2.app/Contents/MacOS/Godot"
# Use fresh output names; do not overwrite the recorded artifacts.
dotnet build Client/Space.csproj -c Debug
"$GODOT" --path Client --max-fps 60 -- --level level_pipeline_test \
  --physics-lab-profile "$OUT/lab-bcd-debug-new.csv" --physics-lab-replay
dotnet build Client/Space.csproj -c ExportRelease
"$GODOT" --headless --path Client --export-release macOS "$OUT/physics-lab-bcd-new.app"
"$OUT/physics-lab-bcd-new.app/Contents/MacOS/Space" --max-fps 60 -- \
  --level level_pipeline_test --physics-lab-profile "$OUT/lab-bcd-release-new.csv" \
  --physics-lab-replay
python3 benchmarks/summarize_client_capture.py "$OUT/lab-bcd-release-new.csv" \
  --max-warm-p95-ms 25 --json
python3 benchmarks/summarize_client_capture.py "$OUT/lab-bcd-debug-new.csv" \
  --max-warm-p95-ms 25 --json # expected exit 1 for measured Debug stalls
python3 -m unittest discover -s benchmarks -p 'test_client_capture.py'
```

Capture regressions **7/7** pass, including legacy CSV compatibility, zero-call
windows and per-call rather than per-frame allocation normalization. Debug and
ExportRelease client builds pass with zero warnings/errors after correcting the
diagnostic byte-count parameter to the writer's uint Position type. Export and
all rendered runs complete; the exporter retains the prior unrelated shutdown
EditorSettings message. No simulation/snapshot edits required, so the committed
D full-suite/budget results were not rerun merely for capture. Standards and spec
reviews found **zero actionable findings**; spec review independently recomputed
the reported primary figures from raw captures. `git diff --check` passes.
No commit or push.

### Paired rendered Debug D comparison: 2026-10-03

**Finding:** D has a repeatable *directional push-window observation* in this
two-pair ABBA experiment: lower wall tails and callback-normalized complete
simulation costs in both chronological pairs. Late-window results overlap and
reverse direction. Effective workloads are not identical, so these observations
do **not** establish an exact-workload causal percentage, a general Debug speedup,
or acceptable responsiveness. Both variants still stall severely. Continue to
**defer E as the immediate performance fix**.

#### Identified sources, isolation and equal diagnostics

- **A = pre-D `665585a7d26a1b5e0cc10d1f4d23017993e89dd7`**;
  **B = D `531c9c1105eb54dec3b6024b2715b00114a550a5`**. Both contain B+C.
  This experiment compares **D versus pre-D**, not C or an isolated B+C+D gain.
- Created separate detached local shared clones, including separate clones of all
  four submodules at their committed IDs. Shared Git objects do not share working
  files, restore state or build outputs. The main tree's five existing modified
  files and rollback submodules' untracked `Runtime/bin/` and `Runtime/obj/` were
  inspected and preserved. No commit/push; no simulation/snapshot edits.
- Copied exactly the same four diagnostic files into A and B:
  `ClientPerformanceCapture.cs`, `GameInterpolationReceiver.cs`,
  `summarize_client_capture.py`, `test_client_capture.py`. Relative to both commits,
  this adds the previous session's actual copy calls/current-thread managed bytes/
  serialized bytes, copy-boundary description, actual callback totals, optional
  phase distributions and legacy/zero-call regressions. This session additionally
  records each loaded assembly's **module version ID (MVID)** at capture flush.
  Identity collection is outside measured frames and snapshots. No authored content,
  presentation setting, transport, prediction, rollback or input policy changed.
- Imported resources separately and built both clients sequentially with
  `dotnet build Client/Space.csproj -c Debug -m:1`. Initial fresh builds and final
  identity-instrumented builds succeeded; final builds have zero warnings/errors.
  Captured all output DLL SHA-256 hashes and read their MVIDs directly with
  `System.Reflection.Metadata.PEReader`. Each of the six loaded assembly MVIDs in
  every process matches its variant's fresh DLL. All six report **Debug and JIT
  optimization disabled**. The manifest records source IDs and identical diagnostic
  file hashes; final patches record every applied diagnostic difference.
- **Default runtime/JIT policy throughout**. No `DOTNET_TieredCompilation` or
  `COMPlus_` override; only inherited `DOTNET_CLI_UI_LANGUAGE=en` was present.
  The Release-built identity reader is an offline utility, not the measured client.
- First exploratory A launch reached the route end but failed during metadata
  flush because Godot's in-memory assembly has empty `Assembly.Location`. Its
  hash-reader exception and timed-out launch are retained as `01-A-launch.log`;
  there is no valid capture from that trial. Replaced that diagnostic with MVID
  verification and restarted the complete ABBA sequence under new names. The
  initial patch/manifest remain historical; use the `*-final` versions below.

#### Verified environment and chronological processes

All four valid processes: .NET **10.0.9**, Godot **4.7.2-stable official**, Metal
**4.0 / Forward Mobile**, **Apple M5 (Apple9)**, **1920×1080**, VSync **Enabled**,
explicit **MaxFps=60**. Both overlays are disabled in every stored frame. Every
process completes three fresh-world routes with empty fixture errors and no
runtime `ERROR:` lines. All worlds load 156 placements with the same level hash
`6AB8DFF2116D59D270A13CF10BB5E3319D91B4F8D36BF247673DD57F7A484BE5`.
Route: `(9, 1.5, -33)`, nominal +X for 160 ticks, then settle through 479.

| Order / artifact prefix | Local time (+02:00) | PID | Run 0 / 1 / 2 terminal server:client heads |
| --- | --- | ---: | --- |
| `paired-01-A` | 18:06:38–18:07:35 | 72519 | 541:550 / 542:551 / 546:556 |
| `paired-02-B` | 18:07:35–18:08:26 | 72809 | 545:555 / 543:553 / 546:556 |
| `paired-03-B` | 18:08:27–18:09:17 | 73053 | 540:549 / 541:551 / 545:554 |
| `paired-04-A` | 18:09:18–18:10:11 | 73294 | 542:552 / 545:555 / 546:556 |

No builds or simultaneous client processes inside this sequence. Each observation
below is **run 2 only**, retained separately. Run 0 is process-cold, not guaranteed
cold disk/driver caches. Windows use server head at frame start; catch-up crosses
some boundaries. Terminal incomplete wall intervals are excluded; completion
metadata, not stored-tail inference, verifies all three routes.

#### Per-frame wall p50 / p95 / p99 / maximum

Milliseconds, nearest rank; parenthesized complete frame counts:

| Route ticks | 01-A | 02-B | 03-B | 04-A |
| --- | --- | --- | --- | --- |
| 0–119 | 43.050 / 184.411 / 297.489 / 297.489 (32) | 46.475 / 187.033 / 359.706 / 359.706 (33) | 46.549 / 169.879 / 289.815 / 289.815 (31) | 62.375 / 215.944 / 271.430 / 271.430 (29) |
| 120–239 | 267.666 / 315.084 / 315.084 / 315.084 (15) | 256.995 / 297.829 / 297.829 / 297.829 (15) | 245.971 / 280.429 / 280.429 / 280.429 (15) | 285.807 / 327.303 / 327.303 / 327.303 (15) |
| 240–359 | 273.019 / 310.038 / 310.038 / 310.038 (15) | 251.110 / 285.479 / 285.479 / 285.479 (15) | 264.463 / 298.550 / 298.550 / 298.550 (15) | 231.511 / 312.315 / 312.315 / 312.315 (15) |
| 360–479 | 263.849 / 291.978 / 291.978 / 291.978 (14) | 197.938 / 250.788 / 250.788 / 250.788 (14) | 226.519 / 276.427 / 276.427 / 276.427 (15) | 198.147 / 216.174 / 216.174 / 216.174 (14) |

Push p50/p95 are lower by **4.0%/5.5%** in A→B and **13.9%/14.3%** in B→A
(each reduction uses its paired A denominator). Within-variant push p95 spans
**315.084–327.303 ms** for A and **280.429–297.829 ms** for B. These observed
process ranges do not overlap, but only two processes per variant were measured.
With 15 frames, p95/p99 equal the maximum; tails are very coarse. Late p95 ranges
**216.174–291.978 ms** for A and **250.788–276.427 ms** for B overlap. Late B is
better in pair 1 and worse in pair 2. Initial-window results also overlap.

These fresh captures are substantially slower than the previous session's
25.311/33.706 ms Debug push capture. Settings/runtime metadata match, but effective
input/replay/trajectory work does not. The cause of the absolute-cost discrepancy
is **not established**; do not merge the series or infer a regression from it.

#### Callback work and inclusive CPU boundaries

Actual callback totals: **server / new client / replay**. These count callbacks,
not head advancement; replay includes ordinary aligned-snapshot resimulation.

| Route ticks | 01-A | 02-B | 03-B | 04-A |
| --- | --- | --- | --- | --- |
| 0–119 | 121 / 123 / 257 | 126 / 127 / 259 | 117 / 119 / 244 | 123 / 124 / 253 |
| 120–239 | 120 / 122 / 218 | 119 / 122 / 220 | 120 / 122 / 213 | 119 / 122 / 227 |
| 240–359 | 120 / 123 / 237 | 120 / 122 / 242 | 120 / 122 / 235 | 120 / 122 / 252 |
| 360–479 | 112 / 112 / 259 | 112 / 113 / 240 | 117 / 118 / 235 | 112 / 113 / 260 |

Push **mean / p95 / max callbacks per frame**:

| Callback | 01-A | 02-B | 03-B | 04-A |
| --- | --- | --- | --- | --- |
| Server | 8.000 / 8 / 8 | 7.933 / 9 / 9 | 8.000 / 9 / 9 | 7.933 / 9 / 9 |
| New client | 8.133 / 9 / 9 | 8.133 / 9 / 9 | 8.133 / 9 / 9 | 8.133 / 9 / 9 |
| Replay | 14.533 / 18 / 18 | 14.667 / 18 / 18 | 14.200 / 18 / 18 | 15.133 / 17 / 17 |

Late replay mean/p95/max: **18.500/22/22**, **17.143/20/20**,
**15.667/21/21**, **18.571/22/22** in process order. Full per-process callback
histograms and timing p50/p95/p99/max for every window are in `analysis.json` and
each `*-summary.json`; do not pool those distributions.

Mean **ms/rendered frame**; slash-separated server/client rows include all client
callbacks, except the explicitly split simulation row:

| Push boundary | 01-A | 02-B | 03-B | 04-A |
| --- | ---: | ---: | ---: | ---: |
| ClientGame process | 254.282 | 243.351 | 235.487 | 272.572 |
| Server / client update | 67.258 / 186.827 | 63.761 / 179.423 | 62.494 / 172.831 | 70.506 / 201.909 |
| Server / new-client / replay simulation | 65.670 / 64.823 / 117.598 | 62.250 / 60.285 / 113.666 | 61.180 / 60.075 / 107.606 | 69.166 / 68.137 / 129.108 |
| Physics phase sum, server / client | 56.306 / 158.690 | 52.998 / 150.163 | 52.135 / 144.692 | 59.163 / 171.785 |
| Narrowphase, server / client | 8.077 / 22.588 | 8.409 / 23.422 | 7.980 / 21.962 | 8.608 / 24.594 |
| Solve, server / client | 30.633 / 86.575 | 26.977 / 76.417 | 26.398 / 73.306 | 32.201 / 93.586 |
| Copy / views | 0.793 / 1.419 | 0.767 / 1.355 | 0.757 / 1.377 | 0.752 / 1.407 |

| Late boundary | 01-A | 02-B | 03-B | 04-A |
| --- | ---: | ---: | ---: | ---: |
| ClientGame process | 251.593 | 194.837 | 211.550 | 194.056 |
| Server / client update | 57.986 / 193.407 | 46.227 / 148.433 | 52.263 / 159.142 | 45.019 / 148.876 |
| Server / new-client / replay simulation | 57.798 / 56.905 / 133.075 | 45.977 / 46.102 / 99.068 | 51.894 / 52.102 / 103.592 | 44.793 / 43.921 / 101.766 |
| Physics phase sum, server / client | 52.088 / 171.918 | 40.649 / 128.762 | 47.012 / 141.365 | 39.498 / 128.979 |
| Solve, server / client | 30.892 / 102.295 | 21.948 / 69.820 | 25.865 / 77.748 | 22.516 / 74.037 |
| Copy / views | 0.707 / 1.287 | 0.675 / 1.276 | 0.662 / 1.384 | 0.532 / 0.966 |

**Do not add nested phases.** D's Solve includes scatter before CCD. Physics is
only the sum of phase timers, not the complete simulation boundary; full simulation
also contains mover/gameplay and phase gaps. Copies are inside client update;
updates are inside ClientGame; views are outside it. Disabled debug callbacks total
about 0.013–0.017 ms/push frame. Latest engine render CPU/setup samples may lag;
Godot GPU counters are zero. Residual wall time is **not assigned to rendering**.
No new GPU attribution was necessary; no Metal trace was taken for this experiment.

Callback-normalized complete simulation = summed simulation time / actual callbacks,
**ms/callback**, not per-tick samples or equal-workload microbenchmarks:

| Window / boundary | 01-A | 02-B | 03-B | 04-A |
| --- | ---: | ---: | ---: | ---: |
| Push server | 8.209 | 7.847 | 7.648 | 8.718 |
| Push new client | 7.970 | 7.412 | 7.386 | 8.377 |
| Push replay | 8.092 | 7.750 | 7.578 | 8.531 |
| Push combined client | 8.048 | 7.629 | 7.508 | 8.478 |
| Late server | 7.225 | 5.747 | 6.653 | 5.599 |
| Late combined client | 7.169 | 5.757 | 6.616 | 5.468 |

Push normalized complete server/client costs are lower by **4.4%/5.2%** in
pair 1 and **12.3%/11.4%** in pair 2. Push Solve/callback is also lower in both
pairs (about **11–19%** server and **12–18%** client), including D's scatter.
This is consistent with D helping the active workload, but normalized costs still
depend on which ticks/contact workloads were actually executed. `analysis.json`
also reports p50/p95 of per-frame simulation/callback ratios; these are averages
within frames, **not individual callback timing distributions**.

#### Workload compatibility and acceptance limits

The authored content, route request, simulation settings and code outside D match.
Actual push forward counts are close (119–120 server and 122 new-client callbacks),
but replay totals differ (213–227), and late differences are larger. Final-run
logs contain **47/51/47/43 dropped out-of-window inputs** and **42/47/42/40
misprediction warnings** in process order. These counts include final-run startup
and discarded terminal work, not just push frames; they are not replay counters.
Normal frame-sampled input is retained, with substantial catch-up.

Available client-position samples demonstrate unequal trajectories. At the stored
frame nearest server head 220, player X is **22.272 / 21.665 / 22.887 / 22.682**
(actual server heads 218/218/220/219). Last stored X/Z are approximately
**34.795/−33.899**, **33.998/−34.091**, **32.266/−33.913**,
**32.035/−33.682**. Samples use predicted client poses and different client heads;
they are evidence of mismatch, not authoritative same-tick state comparisons.
Movement persists well beyond the nominal stop in these stalled processes.

This capture has no per-callback awake/constraint/SAT-hit/input-provenance columns.
LastStep values or headless counters cannot retrospectively establish those
rendered workloads. Equal nominal ticks and similar forward totals therefore do
**not** prove equal contact work. D's existing deterministic 480-tick oracle remains
the exact-result evidence; these rendered runs do not replace it. Two pairs and
coarse tails support only the stated directional push observation. The late overlap,
trajectory differences, large absolute-cost discrepancy and unmeasured host/JIT
variation prohibit a precise isolated complete-simulation or frame-speedup claim.

#### Startup, copying, artifacts and next slice

| Separate process-cold observation, ms | 01-A | 02-B | 03-B | 04-A |
| --- | ---: | ---: | ---: | ---: |
| First enter engine uptime | 1823.949 | 1543.826 | 1530.440 | 1502.275 |
| Run 0 setup | 2064.993 | 2091.758 | 2112.114 | 2087.871 |
| Run 1 / 2 fresh-world setup | 151.621 / 130.993 | 91.072 / 102.868 | 108.103 / 128.171 | 111.543 / 121.708 |
| Run 0 pre-route wall p50 / p95 / p99 / max | 394.349 / 910.697 / 910.697 / 910.697 | 420.651 / 763.856 / 763.856 / 763.856 | 419.610 / 872.762 / 872.762 / 872.762 | 469.284 / 778.969 / 778.969 / 778.969 |

Pre-enter time is not internally attributed; fresh-world setup and pre-route
allocation/growth are separate from run-2 route windows. Push copies remain one
per stored frame: **0.752–0.793 ms/call**, **2345–2366 managed B/call**,
**279116–279543 serialized B/call**. Even the larger fresh Debug copy time is below
0.4% of measured ClientGame push CPU; E cannot explain these simulation stalls.
The evidence does not change E's allocation/architecture value or lifecycle gates.

All prior bundles/captures and `physics-d-comparison/` were found and preserved;
none supplied either new build. New local root:
`/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode/physics-d-rendered-20261003/`.
It contains A/B checkouts and imports, initial/final build logs,
`manifest-final.json`, `A-diagnostics-final.patch`, `B-diagnostics-final.patch`,
`build-hashes.json`, `build-mvids.json`, `run-order.json`, four `paired-*.csv` with
`.csv.json` sidecars, launch logs, per-process summaries and `analysis.json`.
The executable scripts `run-paired.py` and `analyze-paired.py` and offline identity
reader record the exact commands/derivation. Setup script is in the parent directory.
Use fresh artifact names when reproducing; the retained runner is the historical
sequence and should not be rerun over its existing captures.

```sh
ROOT="/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode/physics-d-rendered-20261003"
GODOT="$HOME/Applications/Godot_mono-4.7.2.app/Contents/MacOS/Godot"
# In each isolated checkout, sequentially:
dotnet build Client/Space.csproj -c Debug -m:1
# Run A,B,B,A sequentially; change LABEL and choose a NEW output for each process.
"$GODOT" --path "$ROOT/LABEL/Client" --max-fps 60 -- \
  --level level_pipeline_test --physics-lab-profile "<new-absolute-output.csv>" \
  --physics-lab-replay
python3 benchmarks/summarize_client_capture.py "<new-absolute-output.csv>" --json
python3 -m unittest discover -s benchmarks -p 'test_client_capture.py'
```

Verification: capture regressions **7/7**; final sequential A/B Debug builds zero
warnings/errors; all four runtime identities and three-run completion gates pass.
Reviewed diagnostic/documentation changes against the raw summaries and source
diff: no remaining actionable findings after replacing the failed location-based
identity diagnostic. No physics suite rerun merely for capture; D's prior fidelity,
allocation and Release-budget evidence remains applicable. `git diff --check`
passes. Production simulation/snapshot code, quality settings and E/F–H untouched.

**Evidence-supported next slice, requiring approval:** diagnostic workload matching
and fresh-Debug cost investigation before another optimization: capture per-callback
awake/constraint/SAT work and effective input provenance outside snapshots, verify
the fresh-build/runtime/JIT identity against the historical workflow, then repeat
paired default-runtime runs with those compatibility checks. Preserve normal
frame-sampled input/transport and quality settings. Do not change rollback policy
or choose F–H based solely on the nominal-route comparison. E remains deferred as
an immediate responsiveness fix; no next slice was implemented here.

### Workload-matching diagnostic continuation: 2026-10-03

**Approved scope:** continue the diagnostic investigation recommended above. Added
opt-in per-callback workload/input evidence and repeated the D/pre-D rendered Debug
comparison. **Result:** push observations again favor D, but the effective server
input and contact workloads do not match. Late results do not show a repeatable D
benefit. The severe current Debug stalls also reproduce using the retained main
Debug binaries; they are not specific to fresh isolated builds. The historical
33.706 ms versus current ~220–250 ms push-p95 discrepancy remains unresolved.
E remains deferred; no simulation/snapshot/quality or rollback-policy changes.

#### Feedback loop, build audit and bounded diagnosis

The retained `paired-03-B.csv` fails the existing warmed gate in all four windows:
`python3 benchmarks/summarize_client_capture.py <paired-03-B.csv>
--max-warm-p95-ms 25` reports push p95 **280.429 ms**. The four prior processes
reproduced severe stalls, so the accepted three-route workflow remains the rendered
feedback loop. It is not replaced by a faster but different nominal-input harness.

Investigated four ranked explanations: compiler/dependency differences, effective
input/contact workload divergence, runtime/JIT/host variation, and warning output.

- Evaluated main and isolated D GameCore Debug properties: SDK **10.0.301**,
  MSBuild **18.6.4**, `Optimize=false`, `DefineConstants=TRACE;DEBUG`, `net8.0`.
  An offline PEReader audit found **identical raw method IL** in retained-main and
  isolated D GameCore, FixedPoint, StaticEcs, rollback, transport and NavBuilder.
  Third-party DLLs (StaticPack.Debug, GodotSharp/Editor, LiteNetLib, DotRecast) are
  byte-identical. File hashes/MVIDs of source-built DLLs differ; source paths and
  metadata are not asserted byte-identical. Space contains different diagnostic
  metadata/generated type layouts, so its raw IL comparison is not an equivalent
  instruction-token comparison. This audit weakens a changed-simulation-compiler
  explanation; it does not establish identical generated native code/JIT behavior.
- Snapshotted the retained main Debug output tree, then launched it **without
  rebuilding** as `retained-control.csv`. On-disk DLL hashes were unchanged across
  the launch. Its older probe has no loaded MVID/workload stream, so this is a
  separately labelled workflow control, not part of the equal-instrumentation pairs.
  All six assemblies report Debug/optimization disabled, .NET 10.0.9, matching
  settings/content, three completions and no fixture errors. Final push wall
  **202.563 / 233.668 / 233.668 / 233.668 ms**, server/client complete simulation
  **6.133 / 5.953 ms per callback**. Late wall p95 **187.361 ms**. Therefore current
  severe stalls do not require freshly rebuilt simulation dependencies or an
  isolated checkout. Historical process/runtime/host conditions cannot be recovered
  from the earlier capture; no cause is assigned to a particular JIT or host setting.
- Read-only `pmset` inspection reports low-power mode off for AC/battery and no
  recorded thermal/performance warnings. Two existing editor processes were left
  untouched. This is a current observation, not historical host-state equivalence.
- Warning output remains outside the measured simulation root. New push inclusive
  updates exceed measured complete simulation by only about **3.7–4.3 ms/frame**
  combined, including copy/probes/transport/snapshots/logging. That does not support
  warnings as the dominant cause of the ~180–210 ms frames; warning CPU was not
  independently profiled or suppressed. No logging-policy change was made.

#### Equal diagnostic changes and fidelity

`--physics-lab-workload`, together with profile/replay flags, adds:

- `<capture>.ticks.csv`: one value-only record per actual callback, including
  run/frame/kind/tick, full player GID and channel, consumed Move/Attack/Jump input,
  `TicksPassed`, freshness and approval, post-update player position, complete
  simulation and physics/prepare/solve/narrowphase timings, and all 18 available
  LastStep workload counters (awake/sleeping, proxies/pairs, contacts, constraints,
  CCD, islands/wakes, SAT searches/fallbacks/feature hits).
- Input is read **before the production update** from the same session slot as
  PlayerIntentSystem. Audited `GetInput/GetInputs/Get` to confirm these are reads,
  not input insertion/population. `tick - TicksPassed` identifies the originating
  freshness tick for nonsentinel input, not packet arrival time. `IsApproved` is
  session approval, not proof that the current tick received a fresh packet.
- Frame CSV records the existing frame-sampled input request tick/channel/MoveX/Y
  and existing acceptance boolean, via one diagnostic call **after** the unchanged
  SetPredictionInput operation in `ClientGame.cs`. No sampling or write order changes.
- Callback storage is a preallocated list of structs; GIDs/values only, no retained
  Entity handles or ECS refs. Diagnostic storage/query reads are outside
  `simulation_ms` and physics timers, inside inclusive updates, and measured in
  `workload_probe_ms`. File formatting/output occurs only at capture flush.
  Storage is diagnostic-only and may grow; no steady-state simulation allocation
  was introduced. Observer effects are not claimed zero.
- Summarizer automatically loads the callback sidecar when declared in metadata,
  validates callback kinds and valid-input freshness/age, joins by **run+frame**,
  and checks every complete frame's actual server/new/replay totals. Raw terminal
  callbacks are retained but excluded from frame-window results. It reports
  individual callback timing distributions, counter totals/distributions and input
  age/movement/approval summaries. Legacy captures remain supported.

Added a regression covering terminal-frame exclusion, actual callback-count
validation, aged approved input after nominal stop, source-tick derivation and
freshness inconsistency. Capture regressions now **8/8**.

Created new detached A/B checkouts with independent submodule clones/output trees.
**A=`665585a`, B=`531c9c1`**, B+C present in both; authored content/presentation and
all non-D simulation code preserved. Same five diagnostic files in both:
`ClientPerformanceCapture.cs`, `ClientGame.cs`, `GameInterpolationReceiver.cs`,
`summarize_client_capture.py`, `test_client_capture.py`. Exact patches/file hashes
are retained. Built fresh Debug clients sequentially. First A resource import
before compilation crashed in Godot's OGG import with a thread-notification error;
retained `A-import.log`. Building first and importing afterward succeeded for
both. No runtime capture used the failed import state.

All measured assemblies' loaded MVIDs match their fresh output DLLs, Debug with
JIT optimization disabled. Default runtime/JIT policy; no tiering override.
All four processes use .NET 10.0.9, Godot 4.7.2/Metal 4.0 mobile, Apple M5,
1920×1080, VSync Enabled, MaxFps=60, both overlays disabled in every frame,
unchanged level hash `6AB8DFF2116D59D270A13CF10BB5E3319D91B4F8D36BF247673DD57F7A484BE5`.
All three routes per process complete with empty fixture errors and no runtime
`ERROR:` lines. No client overlap or builds inside the ABBA sequence.

| Order / artifact prefix | Local time (+02:00) | PID | Terminal heads, runs 0 / 1 / 2 |
| --- | --- | ---: | --- |
| `workload-01-A` | 18:42:09–18:42:57 | 84711 | 540:549 / 541:550 / 546:555 |
| `workload-02-B` | 18:42:57–18:43:39 | 85060 | 541:550 / 542:552 / 540:549 |
| `workload-03-B` | 18:43:40–18:44:22 | 85341 | 542:551 / 540:549 / 541:550 |
| `workload-04-A` | 18:44:22–18:45:07 | 85525 | 542:552 / 546:555 / 545:555 |

#### Repeated paired results — final warmed run only

Wall **p50 / p95 / p99 / max**, milliseconds, with complete frame counts.
Nearest rank; frame windows still use server head at frame start.

| Route ticks | 01-A | 02-B | 03-B | 04-A |
| --- | --- | --- | --- | --- |
| 0–119 | 28.095 / 127.032 / 304.775 / 304.775 (47) | 35.913 / 90.671 / 196.034 / 196.034 (47) | 30.299 / 93.225 / 179.334 / 179.334 (50) | 41.249 / 112.463 / 226.813 / 226.813 (42) |
| 120–239 | 184.389 / 241.248 / 241.248 / 241.248 (15) | 182.021 / 235.585 / 235.585 / 235.585 (15) | 185.704 / 219.033 / 219.033 / 219.033 (15) | 213.769 / 250.378 / 250.378 / 250.378 (15) |
| 240–359 | 208.459 / 257.090 / 257.090 / 257.090 (15) | 201.133 / 226.719 / 226.719 / 226.719 (15) | 187.871 / 224.907 / 224.907 / 224.907 (15) | 225.440 / 250.510 / 250.510 / 250.510 (15) |
| 360–479 | 193.961 / 234.330 / 234.330 / 234.330 (15) | 191.217 / 253.509 / 253.509 / 253.509 (14) | 189.568 / 222.858 / 222.858 / 222.858 (14) | 164.670 / 196.760 / 196.760 / 196.760 (14) |

Push p50/p95 favor D by **1.3%/2.3%** and **13.1%/12.5%** in the two chronological
pairs. A push-p95 process range **241.248–250.378 ms**, D **219.033–235.585 ms**;
still only two processes per variant and 15 frames per window. Late p95 ranges
overlap (**196.760–234.330** A, **222.858–253.509** D); D's late p95 is worse in
both pairs, and late complete simulation does not consistently improve. Do not
pool these with the prior ABBA series or interpret the change between series as
a diagnostic-code speedup: host/JIT state and effective workloads differ.

Actual callback totals **server / new client / replay**:

| Route ticks | 01-A | 02-B | 03-B | 04-A |
| --- | --- | --- | --- | --- |
| 0–119 | 120 / 121 / 315 | 115 / 116 / 304 | 115 / 116 / 304 | 120 / 121 / 282 |
| 120–239 | 119 / 121 / 208 | 118 / 120 / 206 | 118 / 120 / 201 | 120 / 122 / 221 |
| 240–359 | 120 / 122 / 245 | 120 / 123 / 237 | 121 / 123 / 240 | 120 / 122 / 233 |
| 360–479 | 117 / 120 / 243 | 111 / 113 / 224 | 110 / 112 / 237 | 111 / 113 / 222 |

Push **mean ms/frame**, inclusive boundaries:

| Boundary | 01-A | 02-B | 03-B | 04-A |
| --- | ---: | ---: | ---: | ---: |
| ClientGame process | 189.401 | 178.534 | 181.936 | 206.757 |
| Server / client update | 51.509 / 137.788 | 47.839 / 130.612 | 49.982 / 131.785 | 54.952 / 151.669 |
| Server / new / replay simulation | 49.845 / 49.040 / 85.769 | 46.857 / 46.496 / 81.198 | 48.755 / 47.432 / 81.154 | 53.799 / 51.947 / 96.614 |
| Physics phase sum, server / client | 43.144 / 118.097 | 40.517 / 111.882 | 42.074 / 112.426 | 46.970 / 131.213 |
| Solve, server / client | 23.428 / 64.071 | 21.204 / 58.647 | 21.913 / 58.627 | 26.113 / 72.697 |
| Copy / views | 0.497 / 0.966 | 0.488 / 0.893 | 0.546 / 1.140 | 0.509 / 1.077 |
| Workload probe | 0.206 | 0.235 | 0.134 | 0.125 |

Probe overhead is about **0.06–0.13%** of measured ClientGame push CPU, excluding
indirect cache/JIT observer effects. D Solve includes pre-CCD scatter. Nested rows
must not be added; physics phase sums are not the complete simulation boundary.
GPU samples remain zero; unmeasured wall time is not assigned to rendering.

**Individual server callback** simulation/solve **mean / p95**, milliseconds:

| Window / work | 01-A | 02-B | 03-B | 04-A |
| --- | ---: | ---: | ---: | ---: |
| Push simulation | 6.283 / 7.587 | 5.956 / 7.120 | 6.198 / 7.157 | 6.725 / 7.738 |
| Push Solve | 2.953 / 3.640 | 2.695 / 3.360 | 2.786 / 3.447 | 3.264 / 3.809 |
| Late simulation | 5.668 / 6.847 | 5.732 / 7.596 | 5.576 / 6.538 | 5.091 / 6.622 |
| Late Solve | 2.981 / 3.503 | 2.898 / 3.977 | 2.848 / 3.352 | 2.702 / 3.659 |

Combined-client simulation/callback push means **6.146 / 5.876 / 6.009 / 6.497 ms**;
late **5.636 / 5.664 / 5.452 / 4.973 ms**. Thus push normalized complete server/client
costs favor D by approximately **5.2%/4.4%** and **7.8%/7.5%**. These remain
observations on unequal effective workloads. Every per-frame and per-callback
p50/p95/p99/max, prepare/narrowphase distribution, counter total/distribution and
callback-count histogram is retained in each `workload-*-summary.json`.

#### Why equal nominal routes are not matched workloads

Actual **server** push input/counters:

| Evidence | 01-A | 02-B | 03-B | 04-A |
| --- | ---: | ---: | ---: | ---: |
| Originating freshness tick, throughout push | 161 | 156 | 156 | 124 |
| Input age mean / max | 79.0 / 138 | 84.5 / 143 | 85.5 / 144 | 121.5 / 181 |
| Fresh inputs consumed | 0 | 0 | 0 | 0 |
| +X callbacks after nominal stop at absolute tick 220 | 80 | 80 | 81 | 86 |
| Awake bodies/callback mean | 40.403 | 41.653 | 41.263 | 41.675 |
| Constraints/callback mean | 56.126 | 58.102 | 57.712 | 58.708 |
| Updated contacts/callback mean | 62.857 | 65.102 | 64.127 | 65.925 |
| SAT full / face / separation mean | 1.966 / 17.479 / 2.487 | 1.805 / 19.119 / 2.805 | 1.898 / 19.297 / 2.331 | 2.017 / 19.225 / 2.983 |

All push server callbacks consume **approved but aged +X**, not a fresh zero stop.
New-client push callbacks instead consume +X only **40/39/40/33** times and zero
**81/81/80/89** times, with input age means around 3.5 ticks. Replay consumes +X
throughout these push frames (**208/206/201/221** callbacks), including
**130/130/130/145** callbacks after nominal stop. This directly observes differing
effective input histories; approval and freshness are distinct.

Late A1 still consumes +X in **24 server / 55 replay** callbacks while the other
three late processes consume zero throughout. Late server awake/constraint means
are **37.017/49.359**, **35.036/49.261**, **41.745/58.200**, **34.946/49.450**.
Similar nominal endpoints therefore hide different contact work. Final server
poses are near X **35.005**, with Z **−33.923 / −34.009 / −33.945 / −34.005**;
earlier paths and client corrected timelines also differ.

Final-run dropped-input warnings **46/48/46/55**, misprediction warnings
**37/39/34/49**. Counts include pre-route and terminal work. Normal transport and
frame-sampled writes are unchanged; the evidence links sustained stale movement to
the mismatched rendered workloads, without proving which historical host/JIT change
first triggered slower callbacks.

For each chronological pair and push/late server/new/replay group, a multiset
signature of **actual tick + input validity/age/approval/movement + all 18 counters**
has **zero matching callbacks** across variants; adding post-update pose also
matches zero. This strict diagnostic signature is not full-state equivalence, and
its failure is not a D fidelity failure. It means these captures cannot supply a
matched-work subset from which to infer an isolated full-simulation percentage.
The deterministic D golden-route checks remain the exact-result evidence.

#### Startup, artifacts, verification and recommendation

Process-cold/fresh-world observations, separate from warmed tables:

| Milliseconds | 01-A | 02-B | 03-B | 04-A |
| --- | ---: | ---: | ---: | ---: |
| First enter uptime | 1364.718 | 1185.255 | 1266.900 | 1187.202 |
| Run 0 setup | 2119.496 | 1604.033 | 1728.613 | 1594.076 |
| Run 1 / 2 setup | 61.480 / 88.135 | 102.196 / 80.233 | 68.409 / 58.182 | 55.932 / 103.388 |
| Run 0 pre-route wall p50 / p95 / p99 / max | 477.710 / 761.120 / 761.120 / 761.120 | 375.830 / 628.629 / 628.629 / 628.629 | 329.138 / 617.949 / 617.949 / 617.949 | 358.482 / 647.894 / 647.894 / 647.894 |

No guaranteed cold filesystem/driver cache; pre-enter time is not internally
attributed. Keep startup/pool growth, fresh-world setup, and warmed route costs
separate. The additional diagnostic allocation is not a production allocation gate.

New artifacts root:
`/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode/physics-d-workload-20261003/`:
A/B checkouts, `retained-main-debug/` binary snapshot, manifest, exact diagnostic
patches, build hashes/MVIDs, import/build/launch logs, `run-order.json`, four
`workload-*.csv` with metadata and `.csv.ticks.csv` streams, per-process summaries,
`compatibility.json`, retained-control raw capture/metadata/summary/build hashes,
and `run-workload.py`/`analyze-workload.py`. `setup-d-workload.py` and
`inspect-debug-difference.py` are in the parent; PEReader utility/audit JSON
(`retained-main-vs-isolated-il-v2.json`) are in the preceding rendered-comparison
root. Earlier captures/bundles were preserved.

```sh
# Build isolated A then B Debug sequentially; choose NEW outputs when reproducing.
dotnet build Client/Space.csproj -c Debug -m:1
"$HOME/Applications/Godot_mono-4.7.2.app/Contents/MacOS/Godot" \
  --path "<isolated-checkout>/Client" --max-fps 60 -- \
  --level level_pipeline_test --physics-lab-profile "<new-absolute-output.csv>" \
  --physics-lab-replay --physics-lab-workload
python3 benchmarks/summarize_client_capture.py "<new-absolute-output.csv>" --json
# Optional explicit callback path; otherwise discovered from metadata:
python3 benchmarks/summarize_client_capture.py "<capture.csv>" \
  --callbacks "<capture.csv.ticks.csv>" --json
python3 -m unittest discover -s benchmarks -p 'test_client_capture.py'
```

Verification: **8/8** capture regressions in main and isolated B; A/B Debug builds
and subsequent B ExportRelease compile sequentially with zero warnings/errors.
All four loaded identities, completion gates and every complete frame's callback
join/count checks pass. Reviewed diagnostics against session read semantics,
callback boundaries and raw results; no remaining actionable findings. No physics
suite rerun for diagnostic-only changes. `git diff --check` passes. Existing work
and submodule artifacts preserved; no commit/push.

**Recommendation:** keep D and its existing fidelity evidence, but describe rendered
benefit as repeated directional push observations, not a quantified matched-work
gain. Continue deferring E. The next diagnostic slice is **PID-targeted managed CPU
sampling plus callback thread-CPU versus wall-time correlation** under unchanged
default Debug policy, retaining input/counter provenance. This can distinguish
expensive managed hot paths from host scheduling/JIT effects before choosing a
simulation optimization. A transport/input-delivery investigation is also justified
by the aged-input evidence, but changing lead/rollback policy or replacing
frame-sampled input requires separate approval and cannot be folded into a D
performance comparison. No new optimization or policy change is approved by these
results alone.

### Bounded PROFILE → IMPLEMENT → VERIFY: 2026-10-03

**Implemented and retained:** cache exact delta-rotation coefficients in D's
tick-local packed solver state. The profile identified repeated quaternion/vector
rotation inside `ContactSolverSystem.Solve`; this pass removed that repeated work
and completed correctness and untraced before/after checks. This is a scalar cache
optimization, not another implementation of D or an E/F–H expansion.

#### Profile and source identity

Started at `531c9c1` with the six existing diagnostic/documentation modifications
listed in the preceding session. Both rollback submodules still contained only
untracked `Runtime/bin` and `Runtime/obj` outputs. Preserved those files, existing
diagnostics and unrelated authored/navigation work. No commit or push.

Built the current Debug client, retained its entire Debug output as **A**, then
attached dotnet-trace **10.0.745401** to the exact rendered client **PID 22269** for
five seconds. Attachment began three seconds after the third world's `Level:` log,
so process startup and the first two fresh worlds are outside the sampling period.
The trace's process/thread labels identify Godot and its managed game thread
27336932. `profile.csv`, metadata and callback stream retain the surrounding run.
All six loaded MVIDs match A's on-disk DLLs. Default runtime/JIT policy throughout:
no tiered-compilation or COMPlus override; inherited `DOTNET_CLI_UI_LANGUAGE=en`
only. Debug uses .NET 10.0.9/Godot 4.7.2 on M5, Metal/mobile, 1920×1080,
VSync enabled, explicit MaxFps=60 and overlays disabled.

Sampling attribution, from a 5036.818 ms main-thread sampled timeline:

- Complete client/server simulation roots cover approximately **96.2%** inclusively.
- Client plus server `Solve` covers **1408.968 ms / 28.0%** inclusively.
- Quaternion/vector rotation beneath those Solve stacks covers **304.165 ms**,
  approximately **21.6% of sampled Solve**. Across all callers, that operator
  covers 499.818 ms / 9.9% inclusively. Scalar FP addition and multiplication
  contribute 9.9% and 8.8% exclusive sampled time respectively.
- Runtime `PollGC` stacks are prominent (24.5% exclusive); Stopwatch also appears
  prominently inclusively. These are sampled stacks under an attached profiler,
  not independently measured GC pauses, timer overhead or thread-CPU accounting.
  Do not assign the historical slowdown to them or infer OS scheduling from the
  Speedscope synthetic `CPU_TIME` leaf. The trace locates repeated arithmetic;
  it does not resolve the historical runtime/host discrepancy.

The traced process's final push callbacks report complete server simulation
**6.723 ms/callback**, Solve **2.905 ms/callback**, mean awake bodies **41.832**,
constraints **57.790**, SAT full/face/separation **2.101/18.992/3.454**. All 119
server callbacks consume aged approved +X (source tick 159, age mean/max 84/143),
83 after the nominal stop. These callback summaries correlate the trace with the
known active simulation workload; they are the surrounding push window, not an
exact timestamp join of individual samples to callbacks. Traced performance is
kept separate from the untraced comparison below. Thread-CPU instrumentation was
not needed to select this arithmetic optimization, and no scheduling diagnosis is
claimed.

#### Narrow implementation and fidelity

`GameCore/Physics/ContactSolverSystem.cs` adds `DeltaRotationMatrix` to transient
`SolverBodyState`, an extra 36 bytes per scratch entry. `MakeDeltaRotationMatrix`
hoists **exactly** the coefficients of `FQuaternion * FVector3`: double quaternion
components first, then multiply and combine in the original order. Ordinary
`FMatrix3.FromQuaternion` multiplies before doubling and would change fixed-point
rounding, so it is deliberately not used. Existing matrix/vector multiplication
has exactly the original per-component product and left-associated sum order.

- Gather initializes coefficients from each body's actual delta quaternion,
  including referenced statics. Awake reset sets quaternion and matrix identities
  together. Every `IntegratePositions` refreshes the matrix immediately after the
  unchanged quaternion integration. Solve cannot change delta rotations, so every
  constraint/point in the following pass reuses valid coefficients by body index.
- Both biased and relaxed Solve use the cached matrix; velocities, mass,
  separation, impulses and point/constraint traversal are otherwise unchanged.
  Warm start, restitution, solver settings and motion locks are unchanged.
- Cache lifetime follows D's existing scratch clear/rebuild/finally boundary.
  No new entity handles, components, registration, snapshots or persistent history.
  B's calculation/support fix and C's authoritative SAT history remain intact.
- Scatter still writes only velocities and deltas **before CCD**. The derived
  matrix is never scattered or consulted after CCD; hit-event anchors continue
  reading CCD-adjusted ECS quaternions. Finalization/sleep behavior is unchanged.
- Capacity growth is warm-up work. No new managed hot-path allocation.

#### Untraced rendered before/after evidence

Retained A/B Debug output snapshots were installed sequentially into the **same
main project resource tree**, in A,B,B,A order, with no overlapping client/build
processes. The candidate was built between A1 and B1; B2 and A2 reused retained
outputs without rebuilding.
All capture MVIDs match the selected snapshot, all three worlds complete, fixture
errors are empty, callback joins/count checks pass, and content/settings match the
accepted route. Source changes between snapshots are confined to this optimization;
Space/NavBuilder were rebuilt against the changed GameCore reference. FixedPoint,
StaticEcs, rollback, transport and third-party DLL hashes are unchanged. Raw DLL
hashes/MVIDs, source patches and diagnostic hashes are retained.

Final warmed run only; milliseconds, individual server callbacks and combined
client time divided by actual callbacks. Solve includes scatter. Do not add nested
phases or equate physics phase sums with complete simulation.

| Push ticks 120–239 | A1 (PID 22800) | B1 (24206) | B2 (24425) | A2 (24713) |
| --- | ---: | ---: | ---: | ---: |
| Complete frames | 16 | 22 | 18 | 17 |
| Wall p50 / p95 / p99 / max | 157.092 / 186.405 / 186.405 / 186.405 | 86.830 / 154.166 / 155.247 / 155.247 | 125.293 / 146.727 / 146.727 / 146.727 | 166.758 / 195.548 / 195.548 / 195.548 |
| Server / new / replay callbacks | 121 / 123 / 202 | 122 / 124 / 227 | 126 / 128 / 221 | 122 / 124 / 216 |
| Server complete simulation mean / p95 | 5.367 / 6.129 | 4.534 / 5.007 | 4.525 / 5.026 | 5.577 / 6.196 |
| Server Solve mean / p95 | 2.357 / 2.708 | 1.915 / 2.234 | 1.901 / 2.157 | 2.509 / 2.870 |
| Combined client simulation / Solve mean per callback | 5.178 / 2.316 | 4.394 / 1.881 | 4.385 / 1.867 | 5.429 / 2.472 |
| Awake / constraints mean per server callback | 40.645 / 55.496 | 41.574 / 58.049 | 41.278 / 56.325 | 41.574 / 58.049 |
| Server input age mean / max | 89 / 149 | 54.721 / 115 | 62.5 / 125 | 57.574 / 118 |

**Push improves directionally in both pairs**, for Solve, complete simulation and
wall p95. This supports retaining the optimization. It is not an isolated causal
percentage: all server push callbacks still consume +X; effective source ticks,
replay/contact/SAT work differ. Strict tick/input/all-18-counter compatibility
signatures match **zero callbacks** in every paired push/late kind. Equal nominal
route ticks do not establish matched work, and tiny frame counts give coarse tails.

| Late ticks 360–479 | A1 | B1 | B2 | A2 |
| --- | ---: | ---: | ---: | ---: |
| Wall p95 (frames) | 158.282 (17) | 279.480 (15) | 144.146 (26) | 134.444 (22) |
| Server / new / replay callbacks | 112 / 114 / 255 | 116 / 118 / 240 | 115 / 116 / 319 | 117 / 119 / 319 |
| Server complete simulation / Solve mean | 3.918 / 1.969 | 4.622 / 2.094 | 3.562 / 1.601 | 3.365 / 1.700 |
| Combined client simulation / Solve mean | 3.901 / 1.965 | 4.652 / 2.102 | 3.487 / 1.592 | 3.276 / 1.658 |

**Late results overlap and mix directions.** B1/A2 still consume aged +X during
23/32 late server callbacks; A1/B2 consume zero throughout. Awake/constraint means
also differ. No repeatable late complete-simulation or frame benefit is claimed.
Every candidate push/late window still exceeds the 25 ms responsiveness diagnostic.
Godot GPU counters remain zero; residual wall time is not assigned to rendering.

Startup remains separate. A1/B1/B2/A2 first-enter uptime is
**883.739/1001.371/1152.537/1154.926 ms**; run-0 setup
**1161.318/1512.555/1443.598/1398.621 ms**. Run-1/run-2 fresh-world setup is
**32.723/42.905**, **56.323/57.326**, **67.899/37.403**, **63.276/63.296 ms**.
These are not guaranteed cold disk/driver-cache measurements.

A supplementary **headless Debug, default-policy ABBA** uses identical test
binaries/dependencies with only the retained A/B GameCore DLL replaced, three
fresh worlds each, production authored diagnostic and unchanged direct approved
input. Run-2 awake/sleeping/constraint/CCD counters match in every process.
It is separately labelled, not substituted for the rendered route:

| Window / mean ms per headless session tick | A1 | B1 | B2 | A2 |
| --- | ---: | ---: | ---: | ---: |
| Push complete session / Solve / prepare | 12.348 / 5.210 / 1.719 | 8.138 / 3.099 / 1.173 | 5.451 / 2.150 / 0.804 | 6.616 / 2.788 / 0.848 |
| Late complete session / Solve / prepare | 5.474 / 2.475 / 0.863 | 4.381 / 1.959 / 0.722 | 6.635 / 2.683 / 1.139 | 5.882 / 2.769 / 0.922 |

Push again favors B in both pairs; late complete costs mix directions. Unchanged
phases vary substantially as well, so these process timings do not isolate all
observed gains to the cache. All four diagnostics fail the existing **Release**
1 ms budget when run in Debug, as expected; raw failure output is retained.
Structural/session allocations in that diagnostic are not the steady-state gate.

#### Verification, review, artifacts and reproduction

- **153/153** focused physics/manifold/toy/cache/equivalence/platform/packed tests
  pass in **Debug, Release and ExportRelease**, run sequentially. Includes the
  unchanged **all-480-tick pre-D golden oracle**, poisoned-scratch restoration,
  rollback/distinct-world full sync, sleep/wake, mover/platform and CCD cases.
- New independent raw-result test checks **4096** seeded quaternion/vector inputs,
  including non-unit and negative-W quaternions, against the existing quaternion
  operator. Every component agrees exactly. No oracle regeneration.
- Steady-state physics allocation gate passes at **0 bytes/tick**. Debug focused
  packed/allocation-only invocation passes **4/4** before broad verification.
- Release budgets pass: regular mean **0.613 ms**, worst **15.309 ms** (mean is the
  gate), 30-tick burst **5.503 ms**, 125-tick burst **29.779 ms**. Unpaired budget
  observations, not a Release optimization percentage.
- Client Debug and ExportRelease builds have **zero warnings/errors**. Capture
  regressions pass **8/8**; no diagnostic or callback-validation weakening.
- Review checked exact coefficient/sum order, all delta-rotation writes, static
  participation, buffer lifetime, pre-CCD scatter and post-CCD hit anchors. No
  remaining actionable findings. Existing diagnostic/input reads and nested timing
  semantics are preserved. `git diff --check` passes. Full suite was not repeated
  merely to profile; relevant production-change gates were run above.

New artifact root:
`/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode/physics-hotspot-20261003/`.
Contains A/B binary snapshots/identities, `warm.nettrace`, `warm.speedscope.json`,
`trace.log`, `profile-analysis.json`, `profile.csv` with metadata/callback stream,
four untraced `before-01-A`, `after-02-B`, `after-03-B`, `before-04-A` captures,
launch logs and full summaries, `comparison.json`, headless ABBA logs, source patches,
manifest and reproduction/analysis scripts. Supplemental test outputs reside in
ignored `tests/GameCore.Tests/bin/Hotspot{A,B}/Debug/net10.0/`. Previous artifacts
are preserved. Retained scripts describe this historical sequence; use new roots
and names rather than rerunning them over existing artifacts.

```sh
# Sequential builds/tests; use the broad filter from D plus the new packed case.
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Debug -m:1 \
  --filter 'FullyQualifiedName~PackedSolverTests|FullyQualifiedName~SteadyStateAllocationTest'
# Repeat appropriate physics filters in Release and ExportRelease sequentially.
dotnet run --project benchmarks/GameCore.Benchmarks/GameCore.Benchmarks.csproj \
  -c Release -- --physics --enforce
dotnet build Client/Space.csproj -c Debug -m:1
GODOT="$HOME/Applications/Godot_mono-4.7.2.app/Contents/MacOS/Godot"
"$GODOT" --path Client --max-fps 60 -- --level level_pipeline_test \
  --physics-lab-profile "<new-root>/new-capture.csv" \
  --physics-lab-replay --physics-lab-workload
# Separate profiler launch; attach to its exact PID after third-world warm-up.
"<artifact-root>/tools/dotnet-trace" collect --process-id <PID> \
  --profile dotnet-sampled-thread-time --duration 00:00:05 \
  --format Speedscope --output "<new-root>/warm.nettrace"
python3 benchmarks/summarize_client_capture.py "<new-root>/new-capture.csv" --json
python3 -m unittest discover -s benchmarks -p 'test_client_capture.py'
```

**Next action:** retain this verified cache. If continuing, target the remaining
scalar solver work with another bounded profile-backed change, with complete
simulation and fidelity checks. Debug is still severely slow; historical absolute
costs, runtime/host variation and aged transport-delivered input remain unresolved.
Investigating input delivery separately is justified, but policy changes need
separate approval. E remains deferred; F–H remain conditional.

### Physics-only scalar solver continuation: 2026-10-03

**Scope correction:** the user clarified that the next step should continue
**physics performance**, not investigate input delivery. The attempted input
diagnostic patch failed atomically and applied no changes. All five existing
client/capture diagnostic files have the same SHA-256 hashes as the preceding
rotation-cache pass. No input, transport, lead or rollback policy changes were made.

**Implemented and retained:** `MultiplySolverMatrix` is an internal scalar
matrix/vector helper used only in `ContactSolverSystem.Solve`. It removes the
unoptimized FP operator/value-copy/constructor chain while retaining exact Q16.16
arithmetic. D and the preceding rotation cache remain in both comparison variants.
This pass is complete; the overall Debug responsiveness goal is **not** complete.

#### Profile-backed selection

Fresh five-second dotnet-trace attachment to warmed rendered **PID 35546**, under
unchanged default Debug/.NET 10.0.9 policy, three seconds after the third world's
`Level:` log. A's DLL hashes exactly match the retained rotation-cache candidate
from the preceding pass; the new profile and all untraced captures validate loaded
MVIDs against their selected binary snapshots. Accepted level, route, resolution,
renderer, VSync/cap and disabled overlays are unchanged.

On the main thread's 5059.707 ms sampled timeline:

- `FMatrix3 * FVector3` accounts for **375.617 ms / 7.4%** inclusively across
  callers. Under Solve, exclusive FP addition/multiplication contribute
  **206.055/180.503 ms**, vector construction **82.488 ms** and matrix multiplication
  itself **81.700 ms**. Nested inclusive/exclusive figures are not additive.
- Contact-constraint GetPoint/SetPoint accessors contribute only a few milliseconds
  each. The profile did **not** justify a contact-point-copy optimization; that
  exploratory idea was rejected without implementing it.
- ZeroMemory and runtime sampling/safepoint stacks also appear. Their presence is
  not an independently measured GC/scheduling diagnosis. Sampling locates source
  work and is kept separate from untraced performance validation.

The traced final push window retains **121 server / 122 new / 221 replay callbacks**,
mean server simulation/Solve **8.727/3.258 ms**, awake/constraints
**40.471/56.744**, SAT full/face/separation **2.050/17.702/2.579** per server callback.
All server push callbacks consume aged approved +X (source tick 151, age mean/max
93/153). These are surrounding-window callback correlations, not an exact timestamp
join of samples to individual callbacks. No historical-cost/JIT explanation is
inferred from this traced process.

#### Arithmetic and fidelity

The helper takes matrix/vector by `in` and directly evaluates the nine raw
64-bit products, right-shifts each by `FP.FractionalBits`, **narrows each product
to int before summing**, and performs the original left-associated 32-bit additions
under unchecked wrapping. Output uses FP/vector value-field initialization rather
than the constructor/operator call chain. It is not a wide accumulated dot product,
saturating math, SIMD or an algebraic reassociation. It matches
`FixedPoint/Runtime/Fixed32/FP.cs` and `Structs/FMatrix3.cs` exactly. If the existing
opt-in `CHECK_OVERFLOW` symbol is enabled, it falls back to the original operator
to retain checked diagnostics; no build flag or default runtime/JIT policy changed.

Only Solve's delta-rotation, normal/twist/rolling and tangent angular matrix/vector
products use the helper. Integration, preparation, warm start, restitution, CCD,
hit-event anchors and other math-library callers are unchanged. Cross products,
impulse order, every contact/point, settings and tolerances remain unchanged.
No component layout, snapshot contents, registrations, persistent handles or
managed simulation allocations. B's full-box correction, C's serialized SAT history
and D's scatter-before-CCD boundary remain intact.

#### Verification and isolated operation measurement

- **154/154** focused physics/manifold/toy/cache/equivalence/platform/packed cases
  pass in **Debug, Release and ExportRelease**, sequentially. Includes every tick
  of the unchanged **480-tick pre-D golden oracle**, poisoned-scratch restoration,
  rollback/full sync, sleep/wake, mover/platform and CCD regressions.
- New raw-result test checks **8192** seeded matrix/vector inputs against the
  existing library operator. Includes full-range int raw values, int minima/maxima,
  signed Q16.16 boundaries and negative products, exercising per-product narrowing
  and sum wrapping. Every component agrees exactly. No oracle regenerated.
- Packed/allocation-only Debug checks pass **5/5**; the steady-state allocation
  gate remains **0 bytes/tick**. Capture regressions remain **8/8**.
- Release budgets pass: regular mean **0.473 ms**, worst **3.373 ms** (mean is the
  gate), 30-tick burst **6.834 ms**, 125-tick burst **28.038 ms**. These are unpaired
  gates, not a Release gain claim. Client Debug/ExportRelease builds: zero warnings
  or errors. No full suite repeated merely to profile.

A separate Debug/default-policy .NET 10 utility compares original and candidate
in the **same process** on identical frozen inputs, with matching `in`-matrix/
`in`-vector delegate signatures. Twelve paired warm-ups, then nine alternating-order
paired batches of **131072 products**; raw results and every checksum agree. Median
original **12.8964 ms**, candidate **3.6858 ms**, an isolated **71.4%** reduction.
Ranges are **11.3895–15.3281 / 3.3076–4.5864 ms**. The loaded candidate GameCore MVID
matches rendered B. This establishes a useful arithmetic-helper gain; it excludes
remaining solver, complete simulation, transport, rollback and rendering costs.

#### Untraced rendered ABBA, final warmed run only

Same resource tree and identical diagnostic sources; retained A/B output snapshots
installed sequentially, no overlapping builds/clients. B was built between A1/B1;
B2/A2 reuse outputs. Only GameCore source changed; Space/NavBuilder rebuilt against
its reference. FixedPoint/StaticEcs/rollback/transport/third-party DLLs are unchanged.
All three routes complete, fixture/runtime errors are absent, and callback
joins/counts and loaded identities pass. Windows exclude terminal incomplete work.

| Push ticks 120–239 | A1 (36147) | B1 (37593) | B2 (37692) | A2 (37887) |
| --- | ---: | ---: | ---: | ---: |
| Frames | 16 | 16 | 15 | 16 |
| Wall p50 / p95 / p99 / max, ms | 166.901 / 206.584 / 206.584 / 206.584 | 158.901 / 203.055 / 203.055 / 203.055 | 174.104 / 204.031 / 204.031 / 204.031 | 192.304 / 224.986 / 224.986 / 224.986 |
| Server / new / replay callbacks | 118 / 119 / 202 | 121 / 123 / 206 | 119 / 121 / 201 | 126 / 128 / 221 |
| Server simulation mean / p95, ms/callback | 5.764 / 6.761 | 5.365 / 6.218 | 5.460 / 6.653 | 6.099 / 7.110 |
| Server Solve mean / p95, ms/callback | 2.358 / 2.920 | 1.941 / 2.243 | 1.935 / 2.418 | 2.456 / 2.976 |
| Server prepare mean, ms/callback | 0.770 | 0.765 | 0.765 | 0.821 |
| Combined client simulation / Solve, ms/callback | 5.557 / 2.318 | 5.199 / 1.930 | 5.408 / 1.939 | 5.906 / 2.451 |
| Awake / constraints mean per server callback | 41.551 / 57.585 | 40.397 / 56.207 | 40.689 / 55.689 | 40.325 / 56.214 |

Solve and complete simulation improve directionally in **both push pairs**.
Wall p95 improves slightly in pair 1 and more in pair 2; frame counts make tails
coarse. Server inputs/contact/SAT work differ and strict tick/input/all-counter
signatures again match **zero callbacks** across every paired push/late kind.
Thus the helper's isolated result is causal operation-level evidence, while these
rendered percentages must remain unequal-workload observations.

| Late ticks 360–479 | A1 | B1 | B2 | A2 |
| --- | ---: | ---: | ---: | ---: |
| Wall p95 (frames), ms | 193.495 (15) | 203.302 (15) | 209.342 (14) | 191.195 (14) |
| Server / new / replay callbacks | 118 / 119 / 251 | 117 / 118 / 235 | 110 / 112 / 223 | 109 / 110 / 217 |
| Server simulation / Solve mean, ms/callback | 4.753 / 2.064 | 4.941 / 1.977 | 5.083 / 2.082 | 5.332 / 2.453 |
| Combined client simulation / Solve mean, ms/callback | 4.717 / 2.038 | 4.920 / 1.972 | 5.043 / 2.064 | 5.225 / 2.445 |

Late Solve is lower in both pairs, but complete simulation mixes directions and
wall p95 **worsens in both**. No late responsiveness gain is claimed. All warmed
candidate windows still fail the 25 ms responsiveness diagnostic. The current
Debug slowdown is not solved, and absolute comparisons to the previous session
are not causal. Phase sums are not complete simulation; Solve includes scatter.
Godot GPU counters remain zero and residual wall time is not assigned to rendering.

Process-cold observations remain separate: A1/B1/B2/A2 first-enter uptime
**2195.380/1840.092/1421.882/1825.345 ms**, run-0 setup
**1975.862/1705.577/1740.623/2134.022 ms**. Fresh-world run-1/run-2 setup:
**71.101/92.857**, **138.085/91.736**, **100.492/80.938**, **117.975/113.985 ms**.
No guaranteed cold filesystem/driver caches.

#### Review, artifacts and next physics action

Review checked signed shifting, product narrowing, wrapping, sum order and all
changed Solve sites against library expressions; no actionable findings remain.
Existing diagnostics and the previous rotation cache are preserved. No input
diagnostics or transport source changes applied. `git diff --check` passes;
no commit/push.

Artifact root (the exploratory `rows` name predates hotspot selection):
`/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode/physics-solver-rows-20261003/`.
Includes A/B outputs/hashes/MVIDs, profile capture and five-second trace/Speedscope,
profile analysis, four untraced captures and callback streams/full summaries,
per-process launch commands/timestamps/logs, comparison/compatibility JSON,
`matrix-bench/` source/binary and `matrix-bench-result.json`, exact A/B source
patches relative to HEAD, unchanged diagnostic patch/hashes and manifest. Historical
artifacts remain intact. Use fresh names/roots when reproducing captures.

```sh
# Sequentially for Debug, Release and ExportRelease; broader filters as above.
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Debug -m:1 \
  --filter 'FullyQualifiedName~PackedSolverTests|FullyQualifiedName~SteadyStateAllocationTest'
dotnet run --project benchmarks/GameCore.Benchmarks/GameCore.Benchmarks.csproj \
  -c Release -- --physics --enforce
# The utility references the retained B DLLs; it does not rebuild the client.
dotnet run --project "<artifact-root>/matrix-bench/matrix-bench.csproj" -c Debug
# Rendered/profiler launch commands remain those in the preceding bounded pass.
# New run.py records exact commands and identities; don't overwrite retained captures.
python3 benchmarks/summarize_client_capture.py "<new-capture.csv>" --json
```

**Recommended next action stays physics-only:** retain both verified scalar changes.
Remaining scalar vector operations in Solve are the next narrow candidate, subject
to a fresh post-change profile and the same exact-result/performance gates. The
historical cost discrepancy remains unresolved. Input/transport are retained as
workload provenance, not promoted to the next optimization scope. E and broad F–H
remain deferred.

### Physics-only scalar cross-product pass: 2026-10-03

**Implemented and retained:** `CrossSolverVectors`, an exact scalar cross-product
helper used only in `ContactSolverSystem.Solve`. This completes one bounded
PROFILE → IMPLEMENT → VERIFY slice. Both earlier uncommitted optimizations remain
in the before baseline and candidate. **Rendered Debug responsiveness is not solved.**

#### Baseline, fresh profile and selection

HEAD remains `531c9c1105eb54dec3b6024b2715b00114a550a5`. Inspected status before
editing: the expected eight main-project files were modified; both rollback
submodules contained only untracked `Runtime/bin/` and `Runtime/obj/` outputs.
Preserved all existing work. Built Debug and snapshotted the entire output tree
as A, including exact rotation coefficients and `MultiplySolverMatrix`. B adds
only the cross helper and its eight Solve call sites. Both source patches relative
to HEAD, source SHA-256 manifests, DLL hashes and directly read MVIDs are retained.
All five client/capture diagnostic files are byte-identical between A, B and the
final tree. No input/transport/prediction/rollback changes, commit or push.

Attached dotnet-trace to **PID 42648** for five seconds, three seconds after the
third world's `Level:` log. Default Debug/.NET **10.0.9** runtime/JIT policy;
no tiering/COMPlus override (only inherited `DOTNET_CLI_UI_LANGUAGE=en`). Main
managed thread **27465029**, sampled timeline **5020.458 ms**:

- Solve accounts for **1082.648 ms** inclusively; cross products beneath Solve
  account for **198.435 ms**, about **18.3% of sampled Solve / 4.0% of the timeline**.
- Cross-product leaf attribution: FP multiply **84.710 ms**, FP subtract
  **44.564 ms**, Cross itself **51.156 ms**, vector constructor **18.004 ms**.
  These are the leaves within that cross subtree, not extra additive phases.
- Across Solve, FP addition remains prominent (**201.880 ms exclusive**), along
  with other vector/operator work. Selected only cross products, rather than
  blanket-rewriting the math library. GetPoint/SetPoint remain a small target.
- ZeroMemory/runtime stacks are not proof of GC pauses, scheduling effects or the
  historical slowdown's cause. Thread-CPU evidence was unnecessary to select this
  local arithmetic change; no scheduling diagnosis is claimed.

The traced process's surrounding final push window has **120 server / 120 new /
509 replay callbacks**, complete server simulation/Solve **2.614/1.086 ms/callback**,
awake/constraints **41.700/55.983**, SAT full/face/separation
**1.525/19.425/3.892** per server callback. Server input is +X/zero **47/73** times,
fresh **82/120**, age mean/max **1.108/14**; seven movement callbacks occur after
the nominal stop. This correlates sampling with active physics but is not an exact
timestamp join. Traced timings are separate from all untraced validation. This
process is markedly faster than the prior profile without a source-policy change;
the absolute-cost variation remains unexplained.

All five rendered processes use the accepted level/hash, three fresh worlds, start
`(9, 1.5, -33)`, nominal +X for 160 ticks then settle through 479, Godot 4.7.2,
Apple M5/Metal mobile, **1920×1080**, VSync Enabled, explicit **MaxFps=60** and both
overlays disabled throughout. Loaded Debug/JIT-optimization-disabled MVIDs match
the selected A/B DLLs. All completion/fixture checks and complete-frame callback
joins/counts pass; no runtime `ERROR:` lines. Normal frame-sampled input is retained.

#### Exact arithmetic and correctness

`CrossSolverVectors(in FVector3 a, in FVector3 b)` evaluates the original six
signed 64-bit products, arithmetic-right-shifts each by `FP.FractionalBits`,
**narrows each product to int before its component subtraction**, and wraps under
`unchecked`. Components preserve `a.Y*b.Z - a.Z*b.Y`, `a.Z*b.X - a.X*b.Z`,
`a.X*b.Y - a.Y*b.X`. Direct value-field initialization removes FP operator/copy
and vector-constructor calls. It neither subtracts wide products before shifting
nor saturates/reassociates arithmetic. Under existing `CHECK_OVERFLOW`, it calls
the original library Cross to preserve opt-in diagnostics.

Only normal-row and tangent-friction velocity/impulse cross products inside Solve
use the helper. Preparation, warm start, restitution, integration, CCD, other
math callers, contact/constraint order and all settings remain unchanged. B's
support correction/calculation caches, C's authoritative serialized SAT history,
D's scatter-before-CCD boundary and both previous scalar optimizations are intact.
No snapshot/schema/registration change, persistent handles or new allocation.

Sequential verification:

- **155/155** focused cases pass in **Debug, Release and ExportRelease**. Includes
  the unchanged **480-tick pre-D golden oracle**, scratch restoration, rollback/
  full sync, sleep/wake, mover/platform, manifold/cache and CCD regressions.
- **8192** seeded cross-product raw comparisons pass, including full-range raw
  values, int minima/maxima, signed fixed-point boundaries and wrapping. Previous
  **4096 quaternion / 8192 matrix** comparisons pass unchanged. No oracle regenerated.
- Packed/allocation-only Debug selection: **6/6**. Steady-state physics allocation
  remains **0 bytes/tick**. Capture regressions: **8/8**.
- Release budgets pass: regular mean **0.335 ms**, worst **1.231 ms**, 30-tick burst
  **5.412 ms**, 125-tick burst **24.421 ms**. Unpaired gate results, not Release gains.
- Client Debug/ExportRelease builds: **zero warnings/errors**. Configuration builds,
  tests and rendered processes ran sequentially; full suite not repeated to profile.

Separate same-process frozen-input **Debug/default-policy** utility: matching
`in`-vector delegate signatures, 8192 identical input pairs, twelve paired warm-ups,
then nine alternating-order batches of **131072 cross products**. Raw results and
all batch checksums agree; candidate MVID matches rendered B. Original/candidate
median **6.4710/2.7160 ms**, an isolated **58.0%** reduction; ranges
**6.3823–9.1830 / 2.7002–3.4360 ms**. This establishes useful operation-level
benefit, not a whole-solver, complete-simulation or frame percentage.

#### Untraced rendered ABBA — final warmed run only

Same main resource tree; A1, build B, B1, B2, restore A, A2, restore B. No overlapping
clients/builds. Only GameCore/Space/NavBuilder DLL hashes differ between snapshots;
simulation dependencies and third-party DLLs match. Space/NavBuilder rebuild against
the changed GameCore reference. Each process is retained separately, not pooled.
Wall distributions use nearest rank and server head at frame start; terminal
incomplete work is excluded. Individual callback means/p95 come from sidecars.

| Push ticks 120–239 | A1 (42994) | B1 (43908) | B2 (44002) | A2 (44264) |
| --- | ---: | ---: | ---: | ---: |
| Frames | 19 | 91 | 88 | 23 |
| Wall p50 / p95 / p99 / max, ms | 113.680 / 148.194 / 148.194 / 148.194 | 21.850 / 29.973 / 31.486 / 31.486 | 22.847 / 30.951 / 34.559 / 34.559 | 83.455 / 145.312 / 154.940 / 154.940 |
| Server / new / replay callbacks | 117 / 119 / 217 | 120 / 120 / 555 | 120 / 121 / 533 | 122 / 124 / 207 |
| Server simulation mean / p95, ms/callback | 4.193 / 5.222 | 2.376 / 2.665 | 2.463 / 2.744 | 4.575 / 5.213 |
| Server Solve mean / p95, ms/callback | 1.611 / 1.939 | 0.905 / 1.059 | 0.924 / 1.048 | 1.717 / 2.133 |
| Server prepare mean, ms/callback | 0.589 | 0.357 | 0.368 | 0.633 |
| Combined client simulation / Solve, ms/callback | 3.957 / 1.545 | 2.322 / 0.899 | 2.407 / 0.913 | 4.467 / 1.729 |
| Awake / constraints mean per server callback | 41.462 / 56.709 | 41.142 / 53.442 | 41.492 / 53.258 | 40.631 / 54.746 |
| Fresh server input callbacks / age mean | 3 / 76.957 | 92 / 0.233 | 88 / 0.267 | 4 / 45.123 |

Push Solve, complete simulation and wall p95 favor B in both chronological pairs.
**The large magnitude is not attributable solely to the helper:** unchanged
prepare costs also fall substantially. A server push consumes +X **107/122** times,
B **40/40**; movement after nominal stop is **71/82** versus **0/0**. Actual replay,
contact, SAT histories and input provenance differ. Strict actual tick/input/
all-18-counter signatures match **zero callbacks** in all paired push/late kinds.
The isolated operation measurement supports retention; rendered results are
unequal-workload directional observations, not causal whole-simulation percentages.

| Late ticks 360–479 | A1 | B1 | B2 | A2 |
| --- | ---: | ---: | ---: | ---: |
| Wall p95 (frames), ms | 41.950 (64) | 48.680 (92) | 71.133 (36) | 64.421 (36) |
| Server / new / replay callbacks | 117 / 117 / 576 | 119 / 119 / 570 | 117 / 118 / 352 | 117 / 116 / 409 |
| Server simulation / Solve mean, ms/callback | 2.376 / 1.015 | 2.260 / 0.787 | 3.158 / 1.375 | 2.967 / 1.276 |
| Combined client simulation / Solve, ms/callback | 2.294 / 1.001 | 1.951 / 0.664 | 3.078 / 1.362 | 2.927 / 1.280 |
| Awake / constraints mean per server callback | 36.556 / 51.393 | 25.748 / 28.160 | 37.137 / 55.667 | 33.735 / 50.188 |

Late simulation/Solve improve in pair 1 and worsen in pair 2; late wall p95 worsens
in both. All late server inputs are zero, but that does not equate prior trajectories
or current contact work. B initial windows pass the 25 ms p95 diagnostic; **all
three later windows fail in both candidates**, including push **29.973/30.951 ms**
and ticks 240–359 **40.783/132.972 ms**. Responsiveness remains incomplete.
Solve includes scatter; physics phase sums are not complete simulation. Nested
timings must not be added. Godot GPU counters remain zero; residual wall time is
not assigned to rendering. Startup/fresh-world setup remains separate in each raw
summary (`firstEnterMs`, run `setupMs`, pre-route distributions); no guaranteed cold
filesystem/driver caches and no attribution of pre-enter time.

#### Review, artifacts, reproduction and recommended next slice

Reviewed every changed call site and raw arithmetic against FP/Cross definitions:
signed shifts, per-product narrowing, component orientation, subtraction wrapping,
unchanged impulse order and checked fallback. No actionable findings. Diagnostic
SHA-256 audit passes; baseline scalar changes and submodule outputs preserved.
`git diff --check` passes; no commit/push.

New root:
`/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode/physics-solver-cross-20261003/`.
Contains A/B output snapshots, identities/source manifests/patches, PID-targeted
trace/Speedscope/profile analyses, profile capture, four untraced CSV/metadata/
callback streams/full summaries, launch logs/commands/timestamps, comparison and
compatibility JSON, cross-bench source/result and audit/verification summary.
Scripts reuse the retained previous pass's analysis/runner definitions with a new
root; no previous artifact was overwritten. Verification console output is also
retained in session tool output. Use new roots/names for reproduction:

```sh
dotnet build Client/Space.csproj -c Debug -m:1
# Snapshot both-current-optimizations baseline before applying the cross helper.
GODOT="$HOME/Applications/Godot_mono-4.7.2.app/Contents/MacOS/Godot"
"$GODOT" --path Client --max-fps 60 -- --level level_pipeline_test \
  --physics-lab-profile "<new-root>/new.csv" --physics-lab-replay --physics-lab-workload
# Separate profile process; attach after third-world warm-up to its exact PID.
"<artifact-parent>/physics-hotspot-20261003/tools/dotnet-trace" collect \
  --process-id <PID> --profile dotnet-sampled-thread-time --duration 00:00:05 \
  --format Speedscope --output "<new-root>/warm.nettrace"
python3 benchmarks/summarize_client_capture.py "<new-root>/new.csv" --json
# Sequential Debug, Release, ExportRelease focused filters from D, including PackedSolverTests.
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Debug -m:1 \
  --filter 'FullyQualifiedName~PackedSolverTests|FullyQualifiedName~SteadyStateAllocationTest'
dotnet run --project benchmarks/GameCore.Benchmarks/GameCore.Benchmarks.csproj \
  -c Release -- --physics --enforce
dotnet run --project "<artifact-root>/cross-bench/cross-bench.csproj" -c Debug
python3 -m unittest discover -s benchmarks -p 'test_client_capture.py'
```

**Recommended next slice stays physics-only:** retain all three verified scalar
changes. For the next bounded pass, profile the candidate and select one remaining
Solve vector add/subtract/scale or dot-product call chain if it remains material;
require the same exact raw/golden/allocation checks and isolated plus complete
simulation comparisons. The historical **33.706 ms** Debug push p95 versus later
severe stalls, and current process-to-process cost variation, remain unexplained.
No particular JIT/host setting or arithmetic change is assigned as their cause.
Input provenance remains comparison evidence, not the next target. E and broad
F–H remain deferred; this completed slice does not close the responsiveness goal.

### Physics-only scalar vector-subtraction pass: 2026-10-03

**Implemented and retained:** `SubtractSolverVectors`, used only in Solve.
This completes the next bounded PROFILE → IMPLEMENT → VERIFY pass. A retains
the exact rotation cache, matrix helper and cross helper; B adds scalar vector
subtraction. **Both candidate push windows now pass the 25 ms p95 diagnostic,
but later windows still fail: rendered Debug responsiveness is not solved.**

#### Fresh profile and selection

Inspected status before editing: HEAD remains `531c9c1`, the expected eight
main-project files are modified, and both rollback submodules have only untracked
`Runtime/bin/obj` outputs. Preserved them and all previous work. Built/snapshotted
Debug A; its production and diagnostic source hashes match the preceding cross
candidate. All five client/capture diagnostic files remain byte-identical between
A, B and the final tree. No input/transport/lead/rollback or JIT policy changes.

PID-targeted five-second dotnet-trace attachment to **47681**, three seconds after
the third `Level:` log, default Debug/.NET **10.0.9**. Main thread **27511142**,
sampled timeline **5017.614 ms**, Solve inclusive **1193.493 ms**:

- Vector subtraction beneath Solve: **275.678 ms**, **23.1% of sampled Solve /
  5.5% of the timeline**. Its leaf costs include FP addition **66.288 ms**, vector
  constructor **51.347 ms**, FP negation **47.874 ms**, vector addition **47.143 ms**,
  vector negation **37.833 ms**, subtraction dispatch **25.193 ms**.
- Dot beneath Solve: **73.329 ms**; vector scale **117.891 ms**. Vector addition
  is **278.954 ms**, but is also nested inside subtraction because the library
  implements subtraction as `-b + a`. These inclusive rows overlap and must not
  be added. Selected subtraction's local call chain rather than assuming dot was
  the next hotspot. The artifact directory's exploratory `dot` name predates
  selection; **the implemented optimization is subtraction, not dot**.
- Runtime/ZeroMemory stacks do not establish GC pauses, scheduling causes or the
  historical slowdown's cause. No thread-CPU instrumentation was needed to select
  arithmetic work, and no scheduling diagnosis is claimed.

The traced final push surrounds **120 server / 121 new / 577 replay callbacks**;
complete server simulation/Solve **2.366/0.911 ms/callback**, awake/constraints
**41.892/56.183**, SAT full/face/separation **2.000/19.667/4.750**. Server inputs
are +X/zero **40/80**, fresh **97/120**, age mean/max **0.192/1**, no movement after
nominal stop. This is surrounding-window correlation, not an exact sample/callback
timestamp join. Profile timings remain separate from untraced comparisons.

All five rendered processes use the accepted level/hash, route and three fresh
worlds, **1920×1080**, VSync Enabled, **MaxFps=60**, overlays disabled, Godot 4.7.2,
M5/Metal mobile and default runtime/JIT policy. Only inherited
`DOTNET_CLI_UI_LANGUAGE=en`; no tiering/COMPlus overrides. All loaded Debug MVIDs
match selected DLL snapshots with JIT optimization disabled. Three-run completion,
fixture checks and every complete frame's callback join/count validation pass;
no runtime `ERROR:` lines. Frame-sampled production input remains unchanged.

#### Implementation and verification

The library's vector subtraction returns `-b + a`, which constructs two vectors
and calls component FP negation/addition. `SubtractSolverVectors(in a, in b)`
directly initializes each raw component as **`-b.RawValue + a.RawValue`**, under
`unchecked`, retaining negation-before-addition and int wrapping even for
`int.MinValue`. There is no multiplication, shift, precision loss, saturation or
wide accumulated expression. Under existing `CHECK_OVERFLOW`, the original vector
operator preserves checked diagnostics and their order.

Thirteen binary vector-subtraction sites in Solve now use the helper: deltas,
normal/friction relative velocity, A-side linear/angular impulse updates and
twist/rolling differences. Scalar subtraction and unary vector negation remain
unchanged. All addition/scale/dot callers and math outside Solve remain unchanged.
No contact/constraint reorder, snapshot/schema/registration changes, persistent
handles, settings changes or simulation allocations. B/C fidelity and D's
scatter-before-CCD boundary are intact; all three previous scalar changes retained.

- **156/156 focused tests pass in each of Debug, Release and ExportRelease**,
  sequentially, including all **480 unchanged pre-D oracle ticks**, scratch restore,
  rollback/full sync, sleep/wake, mover/platform, cache/manifold and CCD regressions.
- **8192 raw subtraction comparisons** pass, full-range seeded values and signed
  fixed-point/int overflow boundaries. Previous quaternion/matrix/cross comparisons
  remain passing. No golden oracle regenerated.
- Packed/allocation-only Debug: **7/7**, zero steady-state physics bytes/tick.
  Capture regressions **8/8**; Debug/ExportRelease client builds zero warnings/errors.
- Release budgets pass: regular mean/worst **0.220/0.853 ms**, 30-tick burst
  **5.355 ms**, 125-tick burst **22.783 ms**. Unpaired gates, not Release gain claims.
  No full suite repeated merely to profile.

Same-process frozen-input Debug/default-policy subtraction utility: matching
`in`-vector delegates, 8192 identical pairs, twelve paired warm-ups, nine
alternating-order batches of **131072 subtractions**. Raw results and all batch
checksums match; utility candidate MVID matches rendered B. Original/candidate
median **5.1345/1.7915 ms**, **65.1% isolated reduction**; ranges
**4.2667–6.1082 / 1.5619–2.5467 ms**. Both series drift during the run; alternating
order limits order bias, but this remains a local operation measurement and does
not establish a whole-solver, complete-simulation or frame percentage.

#### Untraced rendered ABBA, final warmed run only

Same main resource tree, A1, build B, B1, B2, restore A, A2, restore B. Sequential
clients/builds. Only GameCore/Space/NavBuilder DLL hashes differ; dependencies and
third-party binaries match. Source patches, hashes, MVIDs, exact commands/PIDs and
timestamps retained. No historical captures overwritten. Terminal incomplete work
excluded, nearest-rank wall percentiles, windows grouped by server head at frame
start; callback statistics from actual sidecars, not inferred head advancement.

| Push ticks 120–239 | A1 (48122) | B1 (48804) | B2 (48951) | A2 (49188) |
| --- | ---: | ---: | ---: | ---: |
| Frames | 23 | 120 | 106 | 86 |
| Wall p50 / p95 / p99 / max, ms | 95.004 / 113.917 / 115.281 / 115.281 | 16.496 / 19.676 / 20.808 / 21.129 | 18.549 / 23.974 / 24.816 / 25.349 | 22.855 / 31.919 / 37.206 / 37.206 |
| Server / new / replay callbacks | 120 / 121 / 234 | 120 / 120 / 600 | 120 / 120 / 674 | 120 / 120 / 542 |
| Server simulation mean / p95, ms/callback | 4.152 / 4.749 | 2.007 / 2.248 | 2.074 / 2.306 | 2.422 / 2.803 |
| Server Solve mean / p95, ms/callback | 1.471 / 1.729 | 0.711 / 0.761 | 0.710 / 0.789 | 0.904 / 1.064 |
| Server prepare mean, ms/callback | 0.594 | 0.316 | 0.326 | 0.360 |
| Combined client simulation / Solve, ms/callback | 4.020 / 1.450 | 1.940 / 0.703 | 2.015 / 0.707 | 2.362 / 0.894 |
| Awake / constraints mean per server callback | 42.325 / 57.300 | 41.942 / 57.133 | 41.550 / 53.858 | 41.525 / 52.483 |
| Fresh server input callbacks / age mean | 3 / 51.025 | 120 / 0.000 | 106 / 0.117 | 86 / 0.283 |

Push Solve, complete simulation and wall p95 favor B in both pairs. Pair 1 has
substantially different inputs and costs in unchanged phases; A1 consumes +X all
120 times, including 80 after nominal stop, while B1 consumes +X/zero 40/80. Pair 2
has +X/zero 41/79 in both variants, but different freshness, SAT/contact work and
replay counts. Equal movement totals or nominal ticks do not prove matching work.
Strict actual tick/input/all-18-counter signatures match **one push replay callback
in pair 1**, **zero in all other paired push/late groups**. This is not full-state
equivalence, and one callback is insufficient for a meaningful matched-work gain.
The isolated operation benefit supports retention; rendered results remain
directional unequal-workload observations. A1/A2 costs themselves differ markedly.

| Late ticks 360–479 | A1 | B1 | B2 | A2 |
| --- | ---: | ---: | ---: | ---: |
| Wall p95 (frames), ms | 33.956 (71) | 36.822 (101) | 44.918 (66) | 32.237 (85) |
| Server / new / replay callbacks | 118 / 119 / 645 | 117 / 118 / 539 | 117 / 118 / 484 | 118 / 119 / 558 |
| Server simulation / Solve mean, ms/callback | 2.161 / 0.887 | 2.174 / 0.863 | 2.560 / 1.040 | 2.312 / 0.926 |
| Combined client simulation / Solve, ms/callback | 2.098 / 0.885 | 2.044 / 0.839 | 2.472 / 1.028 | 2.215 / 0.915 |
| Awake / constraints mean per server callback | 37.534 / 57.441 | 35.427 / 56.795 | 40.137 / 56.325 | 32.915 / 41.695 |

Late server complete simulation is slightly worse in pair 1 and worse in pair 2;
Solve improves in pair 1, worsens in pair 2. Late wall p95 worsens in both. Zero
late movement in every process does not equate trajectories/contact work. Candidate
initial/push windows pass the 25 ms diagnostic in both runs; B1 ticks 240–359 also
pass (**17.697 ms**), B2 fails (**48.567 ms**). Both late windows fail. **No overall
responsiveness completion or repeatable late benefit is claimed.**

Solve includes scatter. Do not add nested timings or equate physics phase sums
with complete simulation. GPU counters remain zero; residual wall time is not
assigned to rendering. Startup/fresh-world setup is separate in raw summaries
(`firstEnterMs`, `setupMs`, pre-route distributions); no guaranteed cold caches or
attribution of pre-enter time. Historical 33.706 ms versus later severe Debug
stalls, and current within-variant cost variation, remain unexplained.

#### Review, artifacts and next physics action

Reviewed the new-only source delta against the retained A patch: exact component
negation/addition, overflow fallback, all thirteen Solve sites, unchanged B/C/D,
previous helpers and impulse ordering. No actionable findings. Source hash audit
confirms prior candidate is the baseline and five diagnostic files are unchanged.
`git diff --check` passes; no commit/push. Navigation/level work untouched.

New artifact root (exploratory name):
`/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode/physics-solver-dot-20261003/`.
Contains A/B snapshots/source patches/manifests/hashes/MVIDs, five-second trace,
Speedscope and vector profile analysis, profile capture, four untraced captures
with metadata/callbacks/full summaries, launch logs/commands/timestamps, comparison/
compatibility JSON, audit/verification JSON, new-only patch delta, and
`subtract-bench/` source/binary/result. Correctness/build/budget console output is
retained in session tool output. Historical artifacts are preserved.

Reproduction uses the preceding cross-pass build/render/profile/test commands,
with new roots/capture names and all three earlier changes in A. Use sequential
Debug/Release/ExportRelease focused filters and Release budgets as above. Frozen
operation utility:

```sh
dotnet run --project "<artifact-root>/subtract-bench/subtract-bench.csproj" -c Debug
# Result: subtract-bench/bin/Debug/net10.0/subtract-bench-result.json
python3 benchmarks/summarize_client_capture.py "<new-root>/new.csv" --json
```

**Recommended next physics action:** retain all four verified scalar changes.
Profile the candidate and, if still material, target one remaining Solve vector
addition or scale chain (dot remains a smaller candidate in this profile), with
the same exact-result, allocation, isolated-operation and complete-simulation
gates. Do not infer native code/runtime causes from source timings. Input remains
workload provenance, not the next target; E and broad F–H remain deferred.

### Physics-only scalar vector-addition pass: 2026-10-03

**Implemented and retained:** `AddSolverVectors`, used only in Solve. This bounded
PROFILE → IMPLEMENT → VERIFY pass preserves the preceding four scalar changes in
both A and B. The isolated operation improves; rendered results are mixed and
**all candidate warmed windows fail the 25 ms p95 diagnostic in this series**.
The prior subtraction pass's passing push captures remain separate observations,
not evidence that this pass regressed the same effective workload. **Rendered Debug
responsiveness remains unresolved.**

#### Baseline, profile and selected hotspot

Inspected status before editing: HEAD remains `531c9c1`, expected eight modified
main files, rollback submodules with only untracked `Runtime/bin/obj`. Preserved
all work and built Debug A. Source SHA-256 audit verifies A matches the preceding
subtraction candidate's production/tests and five diagnostic files. All five
client/capture diagnostic files are unchanged in B and final source. No input,
transport, prediction lead, rollback, runtime/JIT policy or authored/navigation
changes. No commit/push.

Fresh five-second dotnet-trace attachment to warmed **PID 52577**, three seconds
after the third world's `Level:` log; main managed thread **27541875**, sampled
timeline **5029.752 ms**, Solve inclusive **946.678 ms**. Default Debug/.NET 10.0.9;
only inherited `DOTNET_CLI_UI_LANGUAGE=en`, no tiering/COMPlus overrides.

- Vector addition beneath Solve: **153.587 ms**, **16.2% of sampled Solve / 3.1%
  of the timeline**. Leaves: FP addition **76.048 ms**, vector addition dispatch
  **38.865 ms**, vector construction **38.673 ms**.
- Vector scaling beneath Solve: **115.537 ms**; dot **60.462 ms**. Selected only
  addition, the largest remaining vector operator subtree in this fresh profile.
  Scalar FP addition elsewhere in Solve is not all attributable to vector addition.
- Runtime/ZeroMemory stacks do not establish GC pauses, scheduling causes or the
  historical slowdown's cause. No thread-CPU instrumentation was necessary to
  select this arithmetic change; no scheduling explanation is claimed.

Traced final push surrounding the sampling: **126 server / 126 new / 379 replay**
callbacks, complete server simulation/Solve **3.430/1.202 ms/callback**, awake/
constraints **41.389/53.484**, SAT full/face/separation **2.317/17.675/5.167**.
Server inputs +X/zero **42/84**, fresh **50/126**, age mean/max **3.476/27**, two
movement callbacks after nominal stop. This is surrounding-window correlation,
not an exact timestamp join. Traced timings are separate from untraced validation.

All five rendered processes use the accepted content/hash, route and three fresh
worlds, Godot 4.7.2/M5/Metal mobile, **1920×1080**, VSync Enabled, explicit
**MaxFps=60**, both overlays disabled and .NET 10.0.9. Loaded Debug MVIDs/JIT
optimization-disabled flags match the selected A/B snapshots. Completion/fixture
checks, every complete frame's callback join/count validation and runtime-error
checks pass. Production frame-sampled input and normal transport are retained.

#### Exact implementation and correctness

`AddSolverVectors(in FVector3 a, in FVector3 b)` directly initializes X/Y/Z with
the same **int raw component addition** as the library operator, under `unchecked`.
No widening, saturation, shift, reassociation or result change; operands retain
their original order. The existing `CHECK_OVERFLOW` branch calls the library
operator to preserve opt-in checked diagnostics. Fourteen binary vector-addition
sites inside Solve use it: separation delta, normal/friction point velocities,
B-side impulse updates, rolling impulse accumulation and tangent impulse assembly.

All scalar sums, scale/dot operations and math outside Solve remain unchanged.
Previous rotation, matrix, cross and subtraction helpers are untouched. B's
calculation/support fix, C's authoritative serialized SAT history and D's
scatter-before-CCD boundary remain intact. No settings/tolerance/substep/contact
order, snapshot/schema/registration, persistent-handle or allocation changes.

Sequential verification:

- **157/157 focused cases in each of Debug, Release and ExportRelease**. Includes
  every tick of the unchanged **480-tick pre-D golden oracle**, scratch restoration,
  rollback/full sync, sleep/wake, mover/platform, cache/manifold and CCD tests.
- **8192 seeded full-range raw addition comparisons** pass, including int minima/
  maxima and signed Q16.16 boundaries. All previous raw arithmetic cases pass.
  No golden oracle regenerated.
- Packed/allocation Debug selection: **8/8**, zero steady-state physics bytes/tick.
  Capture regressions: **8/8**. Debug/ExportRelease client builds zero warnings/errors.
- Release budgets pass: regular mean/worst **0.297/4.005 ms**, 30-tick burst
  **6.693 ms**, 125-tick burst **29.913 ms**. Unpaired gates, not Release gains.
  Full suite not repeated merely to profile; builds/tests/clients ran sequentially.

Same-process frozen-input Debug/default-policy utility, matching `in`-vector
delegates, 8192 identical pairs, twelve paired warm-ups and nine alternating-order
batches of **131072 additions**. Raw results/all checksums agree; utility GameCore
MVID matches rendered B. Original/candidate median **2.7982/1.6455 ms**, **41.2%
isolated reduction**; ranges **2.5000–3.3037 / 1.5391–2.0191 ms**. Both series drift;
alternating order limits order bias. This establishes useful local arithmetic
benefit, not a whole-solver, simulation or frame-speedup percentage.

#### Untraced rendered ABBA — final warmed run only

Same main resource tree, A1, build B, B1, B2, restore A, A2, restore B; no overlapping
build/client processes. GameCore/Space/NavBuilder DLL hashes differ; dependencies
and third-party binaries match. Source patches, manifests, MVIDs and exact launch
commands/timestamps retained. Terminal incomplete work excluded. Nearest-rank wall
percentiles; windows grouped by server head at frame start, individual callback
statistics from actual sidecars. No pooling across processes or historical series.

| Push ticks 120–239 | A1 (53037) | B1 (53757) | B2 (54105) | A2 (54472) |
| --- | ---: | ---: | ---: | ---: |
| Frames | 20 | 39 | 39 | 20 |
| Wall p50 / p95 / p99 / max, ms | 106.923 / 150.629 / 151.599 / 151.599 | 44.189 / 107.721 / 119.025 / 119.025 | 38.690 / 172.326 / 192.964 / 192.964 | 99.761 / 140.044 / 141.015 / 141.015 |
| Server / new / replay callbacks | 126 / 127 / 231 | 120 / 121 / 306 | 120 / 120 / 295 | 124 / 126 / 219 |
| Server simulation mean / p95, ms/callback | 4.507 / 5.053 | 3.666 / 4.540 | 4.213 / 5.997 | 4.495 / 4.971 |
| Server Solve mean / p95, ms/callback | 1.473 / 1.683 | 1.251 / 1.687 | 1.477 / 2.261 | 1.479 / 1.653 |
| Server prepare mean, ms/callback | 0.676 | 0.607 | 0.710 | 0.681 |
| Combined client simulation / Solve, ms/callback | 4.379 / 1.459 | 3.552 / 1.220 | 3.935 / 1.359 | 4.259 / 1.436 |
| Awake / constraints mean per server callback | 40.659 / 55.540 | 41.383 / 55.700 | 41.383 / 55.700 | 40.718 / 56.210 |
| Fresh server input callbacks / age mean | 3 / 59.556 | 32 / 10.233 | 31 / 16.550 | 0 / 65.500 |

Push complete simulation is lower in both pairs, but server Solve improves in
pair 1 and is effectively flat in pair 2; push wall p95 improves in pair 1 and
**worsens in pair 2**. Unchanged prepare and other phases vary. B1/B2 have identical
server push counter totals and movement totals, yet their timings/freshness differ;
equal counters alone do not establish equal full state or host/runtime conditions.
A1/A2 server movement persists through every push callback; B1/B2 consume +X/zero
**41/79**, with one movement callback after stop. Replay totals differ substantially.
Strict actual tick/input/all-18-counter signatures match **zero callbacks** across
all paired push/late kinds. Rendered figures are unequal-workload directional
observations; no isolated causal full-simulation or frame percentage is claimed.

| Late ticks 360–479 | A1 | B1 | B2 | A2 |
| --- | ---: | ---: | ---: | ---: |
| Wall p95 (frames), ms | 160.020 (15) | 76.503 (58) | 51.833 (56) | 63.347 (40) |
| Server / new / replay callbacks | 110 / 112 / 243 | 117 / 118 / 608 | 115 / 116 / 582 | 116 / 117 / 424 |
| Server simulation / Solve mean, ms/callback | 4.028 / 1.523 | 2.258 / 0.844 | 2.263 / 0.844 | 2.794 / 1.017 |
| Combined client simulation / Solve, ms/callback | 3.959 / 1.503 | 2.049 / 0.780 | 2.160 / 0.824 | 2.721 / 1.011 |
| Awake / constraints mean per server callback | 42.518 / 53.282 | 34.923 / 50.692 | 34.939 / 50.722 | 35.948 / 46.241 |

Late Solve, complete simulation and wall p95 favor B in both pairs, but A1 still
has 15 late +X callbacks and workload histories/counters differ. These observations
support no matched-work causal magnitude. Candidate p95 across all warmed windows:

| Route window | B1 | B2 |
| --- | ---: | ---: |
| 0–119 | 38.850 | 26.073 |
| 120–239 | 107.721 | 172.326 |
| 240–359 | 149.419 | 165.726 |
| 360–479 | 76.503 | 51.833 |

**Every window fails 25 ms in both candidates.** Previous passing push captures
are not reproducible in this series, including with retained unchanged A binaries.
No source/JIT/host cause is assigned to that absolute-cost discrepancy. Keep this
series separate; don't call its difference from the previous series an addition
regression or claim the route is now responsive. Solve includes scatter; physics
phase sums are not complete simulation and nested rows must not be added. Godot
GPU counters remain zero; residual wall time is not assigned to rendering.
Startup/fresh-world setup stays separate in each raw summary (`firstEnterMs`,
`setupMs`, pre-route distributions); no guaranteed cold caches/pre-enter attribution.

#### Review, artifacts, reproduction and next action

Reviewed the new-only patch delta against A and library FP/vector addition:
component/raw order and wrapping, checked fallback, fourteen Solve sites, untouched
previous helpers and B/C/D boundaries. No actionable findings. Audit confirms
previous candidate source is A and all five diagnostic files are byte-identical.
`git diff --check` passes; existing work/submodule outputs preserved, no commit/push.

New root:
`/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode/physics-solver-vector-20261003/`.
Contains A/B snapshots/source patches/manifests/hashes/MVIDs, five-second trace/
Speedscope/profile analyses, profile capture, four untraced CSV/metadata/callback
streams/full summaries, commands/PIDs/timestamps/logs, comparison/compatibility,
audit/verification JSON, new-only source delta and `add-bench/` source/binary/result.
Build/test/budget console output is retained in session tool output. All historical
roots are preserved. Use new roots/names with the preceding build/render/profile
commands and sequential focused Debug/Release/ExportRelease filters. Utility:

```sh
dotnet run --project "<artifact-root>/add-bench/add-bench.csproj" -c Debug
# Result: add-bench/bin/Debug/net10.0/add-bench-result.json
python3 benchmarks/summarize_client_capture.py "<new-root>/new.csv" --json
```

**Recommended next physics action:** keep all five verified scalar changes. The
next narrow candidate is Solve's remaining vector-scaling chain, if a fresh
candidate profile still finds it material; retain exact per-product signed shift/
narrowing and the same correctness/allocation/isolated plus complete-simulation
gates. This pass does not demonstrate that another arithmetic helper will solve
the residual stalls. Historical and current process-to-process cost variation is
unresolved. Input remains comparison evidence; E and broad F–H remain deferred.

### Physics-only scalar vector-scaling pass: 2026-10-04

**Implemented and retained:** `ScaleSolverVector`, used only in Solve. This
completes another bounded PROFILE → IMPLEMENT → VERIFY slice with all five earlier
scalar optimizations retained in A/B. **All four warmed route windows pass the
25 ms p95 diagnostic in both candidate processes—and both before processes.**
This is encouraging current full-route evidence, not proof that scaling fixed the
historical severe stalls or that Debug responsiveness is solved across sessions.

#### Baseline and fresh profile

HEAD remains `531c9c1`; status inspected before editing showed the expected eight
modified main files and rollback submodules with only untracked `Runtime/bin/obj`.
Preserved all existing work. Built/snapshotted Debug A; its production/test and
diagnostic hashes match the previous addition candidate. Five client/capture
diagnostic files remain byte-identical in A, B and final source. No input/transport/
prediction/rollback, authored/navigation, runtime/JIT-policy or solver-setting
changes. No commit/push.

Five-second PID-targeted dotnet-trace attachment to warmed **14990**, three seconds
after the third `Level:` log; main managed thread **28005810**. Default Debug/.NET
10.0.9; only inherited `DOTNET_CLI_UI_LANGUAGE=en`, no tiering/COMPlus override.
Sampled timeline **5019.761 ms**, Solve inclusive **909.137 ms**:

- Vector scaling beneath Solve: **119.448 ms**, **13.1% of sampled Solve / 2.4%
  of the timeline**. Leaves: FP multiply **68.217 ms**, vector-right-scale dispatch
  **28.707 ms**, constructor **13.649 ms**, scalar-left dispatch **8.874 ms**.
- Dot beneath Solve remains **76.464 ms**. Selected scaling from fresh evidence,
  not a blanket math-library rewrite or another previous-cache implementation.
- Runtime/ZeroMemory stacks are not measured GC pauses or scheduling explanations.
  No thread-CPU instrumentation was necessary to select this arithmetic change;
  the historical absolute-cost discrepancy is not attributed to a JIT/host setting.

Traced final push surrounding sampling: **120 server / 121 new / 548 replay**
callbacks, complete server simulation/Solve **2.481/0.823 ms/callback**, awake/
constraints **41.942/57.133**, SAT full/face/separation **1.917/19.842/4.250**.
Server +X/zero **40/80**, fresh **92/120**, age mean/max **0.233/1**, no movement
after nominal stop. This is surrounding-window correlation, not a sample/callback
timestamp join. Traced timings remain separate from untraced validation.

All five rendered processes use accepted level/hash/route, three fresh worlds,
Godot 4.7.2/M5/Metal mobile, **1920×1080**, VSync Enabled, **MaxFps=60**, overlays
disabled and .NET 10.0.9. Loaded Debug MVIDs/JIT-optimization-disabled flags match
the selected A/B snapshots. Completion/fixture checks and complete-frame callback
joins/counts pass, with no runtime `ERROR:` lines. Normal frame-sampled input retained.

#### Exact scaling and verification

`ScaleSolverVector(in FVector3 v, FP scale)` directly computes each signed 64-bit
component product, arithmetic-right-shifts by `FP.FractionalBits`, then **narrows
that product to int independently**, under `unchecked`. This is exactly the FP
multiplication used by the original vector operator. The scalar-left operator
already delegates to vector-right scaling, so the helper retains vector-component
then scalar product order. No saturation, rounding change, wide accumulation or
reassociation. `CHECK_OVERFLOW` calls the original vector-right operator to retain
opt-in checked diagnostics.

Eight Solve scaling sites use the helper: normal impulse, A/B inverse-mass linear
updates, twist impulse, two tangent impulse components and A/B tangent linear
updates. Scalar/scalar math, dot products and all non-Solve callers remain
unchanged. All prior helpers/cache, B calculation/support fix, C serialized SAT
history, D scatter-before-CCD boundary, contact/point ordering and settings remain
intact. No snapshot/schema/registration, persistent-handle or allocation changes.

- **158/158 focused cases pass in each of Debug, Release and ExportRelease**,
  sequentially. Includes all **480 unchanged pre-D golden ticks**, scratch restore,
  rollback/full sync, sleep/wake, mover/platform, cache/manifold and CCD regressions.
- **8192 raw scaling inputs pass in both operand orders** (`vector*scale` and
  `scale*vector`), full-range signed raw values and int/Q16.16 boundaries. Previous
  raw helper tests remain passing. No oracle regenerated.
- Packed/allocation-only Debug **9/9**; zero steady-state physics allocation.
  Capture regressions **8/8**; Debug/ExportRelease client builds zero warnings/errors.
- Release budgets pass: regular mean/worst **0.307/1.577 ms**, 30-tick burst
  **2.970 ms**, 125-tick burst **13.373 ms**. Unpaired gates, not Release gains.
  Builds/tests/rendered processes sequential; full suite not repeated to profile.

Frozen-input same-process Debug/default-policy utility uses matching `in`-vector/
value-FP delegates. Original invokes scalar-left scaling as the changed production
sites did. 8192 identical pairs, twelve paired warm-ups, nine alternating-order
batches of **131072 products**, exact raw results/checksums; candidate MVID matches
rendered B. Original/candidate median **3.3819/1.4176 ms**, **58.1% isolated gain**;
ranges **2.4573–4.6070 / 1.3300–1.8920 ms**. Operation-level evidence only, not
whole-solver/simulation/frame percentages; local timing variation remains visible.

#### Untraced rendered ABBA — final warmed run only

Same main resource tree: A1, build B, B1, B2, restore A, A2, restore B; no overlapping
clients/builds. GameCore/Space/NavBuilder DLL hashes differ; dependencies/third-party
DLLs match. Source patches/manifests/MVIDs and exact commands/PIDs/timestamps retained.
Terminal incomplete work excluded; nearest-rank wall percentiles, windows grouped
by server head at frame start and actual callback sidecars used throughout.

| Push ticks 120–239 | A1 (15456) | B1 (16102) | B2 (16380) | A2 (16615) |
| --- | ---: | ---: | ---: | ---: |
| Frames | 120 | 119 | 120 | 111 |
| Wall p50 / p95 / p99 / max, ms | 16.572 / 19.688 / 20.680 / 21.263 | 16.719 / 21.412 / 23.419 / 24.414 | 16.586 / 21.095 / 22.621 / 32.875 | 17.813 / 23.802 / 25.941 / 27.404 |
| Server / new / replay callbacks | 120 / 120 / 600 | 120 / 120 / 702 | 120 / 120 / 663 | 120 / 120 / 656 |
| Server simulation mean / p95, ms/callback | 1.990 / 2.219 | 1.977 / 2.194 | 1.999 / 2.284 | 2.109 / 2.436 |
| Server Solve mean / p95, ms/callback | 0.669 / 0.717 | 0.639 / 0.684 | 0.647 / 0.708 | 0.701 / 0.766 |
| Server prepare mean, ms/callback | 0.318 | 0.324 | 0.331 | 0.342 |
| Combined client simulation / Solve, ms/callback | 1.910 / 0.660 | 1.907 / 0.634 | 1.911 / 0.641 | 2.015 / 0.699 |
| Awake / constraints mean per server callback | 41.942 / 57.133 | 41.842 / 57.925 | 41.608 / 56.192 | 41.942 / 57.133 |
| Fresh server callbacks / age mean | 120 / 0.000 | 119 / 0.008 | 119 / 0.008 | 110 / 0.083 |

Push Solve and complete simulation favor B in both pairs, modestly; wall p95
worsens in pair 1 and improves in pair 2. Every push consumes +X/zero **40/80**,
with no aged movement after nominal stop, but SAT/contact work and replay differ.
Strict actual tick/input/all-18-counter signatures match **zero callbacks** across
all paired push/late kinds. Equal movement totals, nominal ticks or forward counts
do not establish equal complete physics state. These are directional observations,
not causal whole-simulation percentages; the isolated scaling utility supplies
operation-level benefit evidence.

| Late ticks 360–479 | A1 | B1 | B2 | A2 |
| --- | ---: | ---: | ---: | ---: |
| Wall p95 (frames), ms | 18.309 (119) | 17.200 (119) | 17.440 (119) | 18.638 (119) |
| Server / new / replay callbacks | 119 / 119 / 713 | 119 / 119 / 713 | 119 / 119 / 713 | 119 / 119 / 713 |
| Server simulation / Solve mean, ms/callback | 1.734 / 0.655 | 1.606 / 0.556 | 1.630 / 0.566 | 1.782 / 0.673 |
| Combined client simulation / Solve, ms/callback | 1.635 / 0.647 | 1.486 / 0.546 | 1.509 / 0.557 | 1.710 / 0.676 |
| Awake / constraints mean per server callback | 35.471 / 56.832 | 33.790 / 49.815 | 32.437 / 48.798 | 35.437 / 56.798 |

Late observations favor B in both pairs, but lower awake/constraint/contact work
prevents isolated attribution. Late inputs are fresh zero in all processes; that
does not equate earlier trajectories. All-window warmed wall p95, milliseconds:

| Route window | A1 | B1 | B2 | A2 |
| --- | ---: | ---: | ---: | ---: |
| 0–119 | 20.012 | 20.114 | 21.939 | 22.023 |
| 120–239 | 19.688 | 21.412 | 21.095 | 23.802 |
| 240–359 | 20.465 | 18.282 | 18.070 | 19.394 |
| 360–479 | 18.309 | 17.200 | 17.440 | 18.638 |

**All four untraced processes pass the full warmed p95 diagnostic.** B2 still has
an isolated push frame at **32.875 ms**; passing p95 does not mean every frame is
below 25 ms. A already passes without scaling, so the current absence of sustained
stalls cannot be assigned to this change. Prior severe-stall series and current
series have different effective workloads; no historical JIT/host cause is proven.
The overall rendered Debug goal is not declared solved across sessions solely
because this bounded pass completed. Solve includes scatter; phase sums are not
complete simulation, nested rows must not be added. GPU counters remain zero;
residual wall time is not assigned to rendering. Startup/fresh-world costs remain
separate in raw summaries (`firstEnterMs`, `setupMs`, pre-route distributions),
with no guaranteed cold caches or attribution of pre-enter time.

#### Review, artifacts and recommended next physics action

Reviewed the new-only delta against A and library FP/vector operators: signed
product/shift/narrowing, scalar-left delegation, checked fallback, eight Solve sites,
unchanged impulse order and B/C/D boundaries. No actionable findings. Audit verifies
previous candidate is A and diagnostic files are unchanged. `git diff --check`
passes; existing work/submodule outputs preserved, no commit/push.

New root (the `20261003` suffix follows the retained series naming; command JSON
records the actual **2026-10-04** execution timestamps):
`/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode/physics-solver-scale-20261003/`.
Contains A/B snapshots/source patches/manifests/hashes/MVIDs, five-second trace/
Speedscope/profile analyses, profile capture, four untraced CSV/metadata/callback
streams/full summaries, commands/PIDs/timestamps/logs, comparison/compatibility,
audit/verification JSON, new-only delta and `scale-bench/` source/binary/result.
Build/test/budget console output is retained in session tool output; historical
roots preserved. Reproduce with the existing sequential focused filters and
build/render/profile commands above, using fresh roots/names. Utility:

```sh
dotnet run --project "<artifact-root>/scale-bench/scale-bench.csproj" -c Debug
# Result: scale-bench/bin/Debug/net10.0/scale-bench-result.json
python3 benchmarks/summarize_client_capture.py "<new-root>/new.csv" --json
```

**Recommended next physics action:** retain all six verified scalar changes and
prioritize confirming full-route physics-cost/responsiveness repeatability across
fresh sessions before another solver edit. This series passes the target already;
do not assume another helper is necessary. If stalls recur, profile complete
simulation and physics boundaries in that process; dot products remain a possible
narrow arithmetic candidate only if fresh evidence supports them. Historical cost
variation remains unresolved. Input is comparison provenance, not a new target;
E and broad F–H remain deferred.

### Final scalar optimization and close-out: 2026-10-04

**Approved close-out:** finish the scoped physics work, review it, then commit and
push all accumulated main-project changes. Added the final profile-supported
`DotSolverVectors` helper at five Solve sites, retaining the six prior changes.
The **seven-change scalar optimization program is implemented and verified**.
This closes that implementation slice, **not the overall rendered Debug
responsiveness goal**: two final candidate processes pass all warmed windows,
but a third confirmation misses push p95 at **27.759 ms**. No further optimization
or policy change is folded into this close-out.

#### Profile, arithmetic and fidelity

Started with the same eight modified main files, HEAD `531c9c1`, branch
`feat/navigation`; both rollback submodules have only untracked `Runtime/bin/obj`
outputs. Preserved existing work and snapshotted the current Debug output as A.
A's production/tests and five diagnostic source hashes match the scaling candidate.
The repository formatter was applied to the intended C# files before B's build;
the diagnostic hashes remain identical across A/B/final source. Only GameCore,
Space and NavBuilder DLL hashes differ; dependencies/third-party DLLs match.
No input/transport/lead/rollback, navigation/level, JIT policy or physics settings
changed. Source patches, DLL hashes/MVIDs and exact commands retained.

Five-second PID-targeted warmed profile of **19918**, three seconds after third
world `Level:` log, default Debug/.NET 10.0.9, main thread **28042027**, sampled
timeline **5019.543 ms**. Solve inclusive **832.979 ms**, dot inclusive beneath
Solve **73.768 ms**, **8.9% of Solve / 1.5% of the timeline**. Dot leaves: FP multiply
**32.689 ms**, FP addition **20.870 ms**, Dot dispatch **20.209 ms**. This is the
fresh evidence supporting the final helper; runtime/ZeroMemory stacks are not
diagnoses of GC/scheduling or the historical stall cause.

Traced surrounding final push: **120 server / 119 new / 618 replay callbacks**,
complete server simulation/Solve **2.245/0.732 ms/callback**, awake/constraints
**41.517/55.525**, SAT full/face/separation **1.892/19.850/4.575**. Server movement
+X/zero **41/79**, fresh **104/120**, age mean/max **0.133/1**, one post-stop moving
callback. Surrounding-window correlation only, not an exact sample/callback join.
The profile is separate from all untraced validation.

`DotSolverVectors(in a, in b)` computes three signed 64-bit products, arithmetic
right-shifts each by `FP.FractionalBits`, **narrows each to int before summing**, then
performs the original left-associated int additions under unchecked wrapping.
It is not wide accumulated/saturating/reassociated math. `CHECK_OVERFLOW` uses
original library Dot to preserve opt-in diagnostics. Only separation, normal
relative velocity, twist speed and the two tangent velocity projections in Solve
change. No scalar formula, impulse/contact/point order or non-Solve caller changes.
B caches/full-box support correction, C authoritative SAT history, D scatter-before-
CCD and all previous helpers are intact. No schema/registration/authoritative
snapshot contents, persistent handles, settings or simulation allocations change.

#### Final correctness and isolated benefit

- Full **Release suite 352/352 non-explicit tests passed**, run once at close-out.
  Explicit diagnostic benchmarks remain separately invoked experiments.
- Focused **Debug 159/159**, **ExportRelease 159/159**, sequentially. All three
  configurations pass the unchanged **480-tick pre-D golden oracle**, scratch
  restoration, rollback/full sync, sleep/wake, manifold/cache, mover/platform and CCD.
- New **8192 raw dot comparisons** include full-range int values and overflow/
  Q16.16 boundaries. Previous quaternion/matrix/cross/subtract/add/scale comparisons
  remain passing. No oracle regenerated.
- Packed/allocation-only Debug **10/10**; steady-state physics **0 bytes/tick**.
  Capture regressions **8/8**. Client Debug/ExportRelease builds zero warnings/errors.
- Final Release budgets pass: regular mean/worst **0.378/1.438 ms**, 30-tick burst
  **5.328 ms**, 125-tick burst **25.494 ms**. Unpaired gate results, not Release gains.
- Repository formatter/check and `git diff --check` pass. Configurations and
  rendered processes run sequentially; no warnings suppressed or hooks bypassed.

Same-process frozen-input Debug/default-policy utility: matching `in`-vector
delegate signatures, 8192 identical pairs, twelve paired warm-ups and nine
alternating-order batches of **131072 dots**. Exact raw results and all checksums
match; candidate MVID matches rendered B. Original/candidate median
**3.8755/2.0469 ms**, **47.2% isolated reduction**, ranges
**2.8499–5.5757 / 1.3101–2.3050 ms**. Local timing variation remains; no whole-solver,
simulation or frame-speedup percentage is inferred.

#### Final untraced rendered comparison and repeatability

Same accepted content/hash, start `(9, 1.5, -33)`, nominal +X 160 ticks then settle
through 479, three fresh worlds/process, Godot 4.7.2/M5/Metal mobile, **1920×1080**,
VSync Enabled, explicit **MaxFps=60**, overlays disabled, .NET 10.0.9/default JIT.
Only inherited `DOTNET_CLI_UI_LANGUAGE=en`, no tiering/COMPlus overrides. Loaded
Debug MVIDs and disabled-JIT-optimization flags match A/B output identities. All
completion/fixture, runtime-error and actual callback join/count checks pass.

A1, build B, B1, B2, restore A, A2, restore B, then a **separate unpaired third B
confirmation**. No overlapping clients/builds. Final run only, nearest-rank wall
percentiles, server head at frame start, terminal incomplete work excluded. All
processes retained separately; do not pool confirmation into a better paired result.

| Push ticks 120–239 | A1 (20299) | B1 (21023) | B2 (21167) | A2 (21399) | B confirmation (23609) |
| --- | ---: | ---: | ---: | ---: | ---: |
| Frames | 115 | 113 | 117 | 107 | 103 |
| Wall p50 / p95 / p99 / max, ms | 17.024 / 22.115 / 23.615 / 23.892 | 17.062 / 23.078 / 26.954 / 29.138 | 16.673 / 21.287 / 22.463 / 23.024 | 18.452 / 25.300 / 26.138 / 26.380 | 19.060 / 27.759 / 29.343 / 29.570 |
| Server / new / replay callbacks | 120 / 120 / 672 | 120 / 120 / 635 | 120 / 119 / 630 | 120 / 120 / 641 | 120 / 120 / 614 |
| Server simulation mean / p95, ms/callback | 2.042 / 2.346 | 2.092 / 2.346 | 1.945 / 2.235 | 2.113 / 2.383 | 2.174 / 2.510 |
| Server Solve mean / p95, ms/callback | 0.657 / 0.708 | 0.638 / 0.703 | 0.612 / 0.672 | 0.677 / 0.724 | 0.646 / 0.743 |
| Server prepare mean, ms/callback | 0.335 | 0.346 | 0.328 | 0.349 | 0.354 |
| Combined client simulation / Solve, ms/callback | 1.953 / 0.651 | 2.004 / 0.640 | 1.877 / 0.602 | 2.023 / 0.675 | 2.080 / 0.646 |
| Awake / constraints mean per server callback | 41.942 / 57.133 | 41.892 / 56.183 | 41.942 / 57.133 | 41.925 / 56.925 | 41.617 / 52.792 |

Push Solve improves directionally in both pairs; **complete simulation worsens in
pair 1 and improves in pair 2**, as does wall p95. Dot's isolated benefit is useful
but not a demonstrated consistent complete-tick/frame improvement. Most movement
totals match 40/80; A2 has 41/79. Input freshness/contact/SAT/replay histories differ.
Strict actual tick/input/all-18-counter signatures match zero in pair 1; pair 2
push matches **1 server / 1 new / 4 replay callbacks**, all late groups zero. These
signatures are not full-state equality and those tiny subsets do not establish
meaningful matched-work percentages. Equal nominal ticks/totals are insufficient.

| Late ticks 360–479 | A1 | B1 | B2 | A2 | B confirmation |
| --- | ---: | ---: | ---: | ---: | ---: |
| Wall p95 (frames), ms | 19.007 (119) | 17.269 (119) | 18.335 (119) | 20.428 (119) | 21.280 (115) |
| Server / new / replay callbacks | 119 / 119 / 713 | 119 / 119 / 713 | 119 / 119 / 594 | 119 / 119 / 713 | 119 / 119 / 693 |
| Server simulation / Solve mean, ms/callback | 1.733 / 0.624 | 1.638 / 0.547 | 1.733 / 0.615 | 1.917 / 0.685 | 1.927 / 0.688 |
| Combined client simulation / Solve, ms/callback | 1.641 / 0.623 | 1.540 / 0.545 | 1.652 / 0.610 | 1.819 / 0.684 | 1.842 / 0.690 |
| Awake / constraints mean per server callback | 35.471 / 56.832 | 33.361 / 50.185 | 35.471 / 56.832 | 36.471 / 59.857 | 38.059 / 56.832 |

Late observations favor B in both pairs, but unequal workload/replay and unchanged
phase variation prohibit isolated magnitude attribution. All late movement is zero.
Full-route warmed wall p95, milliseconds:

| Window | A1 | B1 | B2 | A2 | B confirmation |
| --- | ---: | ---: | ---: | ---: | ---: |
| 0–119 | 20.200 | 23.309 | 20.403 | 28.400 | 21.911 |
| 120–239 | 22.115 | 23.078 | 21.287 | 25.300 | **27.759** |
| 240–359 | 19.688 | 19.085 | 18.226 | 19.976 | 20.567 |
| 360–479 | 19.007 | 17.269 | 18.335 | 20.428 | 21.280 |

**B1/B2 pass all four windows; third B confirmation fails push.** A1 already passes;
A2 fails initial/push. Historical severe-stall variation remains unexplained, so no
causal claim that this final change solves it. The full responsiveness target is
not consistently met. Startup/fresh-world setup stays separate in raw summaries
(`firstEnterMs`, `setupMs`, pre-route distributions); no guaranteed cold caches or
pre-enter attribution. Solve includes scatter; nested timings must not be added
or physics phase sums equated with complete simulation. Godot GPU counters remain
zero; residual wall time is not assigned to rendering.

#### Review, artifacts and closure

Two-axis accumulated-working-tree review against `531c9c1`: Standards finds no
code/StaticEcs issue; Spec finds no correctness/scope issue. The Standards review's
documentation finding—missing final dot evidence/current recommendation—was
addressed by this section and updated Recommended next slice. Final evidence
review also clarified that finding's Standards-axis attribution. Raw FP/vector definitions, helper
call sites, scratch lifetime/rotation writes, scatter/CCD boundaries, diagnostics
read semantics and capture regressions were checked. No remaining actionable
findings after final documentation review.

Artifact root:
`/var/folders/96/8pcw9l5n1070j9xz006yzwp80000gn/T/opencode/physics-final-20261004/`.
Includes A/B output snapshots/identities/source manifests/patches, PID-targeted
trace/Speedscope/profile analyses, profile capture, four ABBA captures plus third
confirmation (all CSV/metadata/callback streams/full summaries), commands/PIDs/
timestamps/logs, comparison/compatibility and audit/verification JSON, new-only
delta, `dot-bench/` source/binary/result. All historical roots remain preserved.
Build/test/budget output is retained in session tool output. Use fresh root/names
when reproducing; no retained runner should overwrite historical captures.

```sh
# Sequential configurations; full Release close-out suite once.
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Release -m:1
# Debug and ExportRelease broad focused filters are unchanged from D, including PackedSolverTests.
dotnet run --project benchmarks/GameCore.Benchmarks/GameCore.Benchmarks.csproj \
  -c Release -- --physics --enforce
dotnet build Client/Space.csproj -c Debug -m:1
"$HOME/Applications/Godot_mono-4.7.2.app/Contents/MacOS/Godot" \
  --path Client --max-fps 60 -- --level level_pipeline_test \
  --physics-lab-profile "<new-root>/new.csv" --physics-lab-replay --physics-lab-workload
# Separate PID-targeted profiler launch/attachment as above; default policy.
dotnet run --project "<artifact-root>/dot-bench/dot-bench.csproj" -c Debug
python3 benchmarks/summarize_client_capture.py "<new-root>/new.csv" --json
python3 -m unittest discover -s benchmarks -p 'test_client_capture.py'
```

**Close-out decision:** retain and commit the seven exact-result scalar changes,
their tests, accumulated opt-in capture diagnostics and evidence documentation.
This approved scalar program is finished; stop adding helpers automatically.
The measured operation reductions do not multiply into a whole-physics percentage.
If the remaining responsiveness target is pursued later, the next physics-only
action is a profile of complete simulation/physics in a reproducibly failing
process, preserving existing workload provenance and default policy, before
selecting another algorithm or architecture change. No E or broad F–H work approved.

## Dependency map

```text
A. Optimized-client baseline + frame-time breakdown
   |
   +-- Physics dominates -----------------------------------------+
   |                                                              |
   |   B. Cache repeated box SAT calculations                     |
   |      |                                                       |
   |      +--> C. Port persistent Box3D SAT feature cache          |
   |                                                              |
   |   D. Gather/solve/scatter packed body state                   |
   |                                                              |
   |   Re-measure B/C/D individually and together                  |
   |      |                                                       |
   |      +--> F. Awake sets / persistent islands, if justified     |
   |      +--> G. CCD pruning, if CCD becomes significant          |
   |      +--> H. Graph coloring / parallelism / SIMD, last        |
   |                                                              |
   +-- Presentation/copying dominates --> E. Render-pose buffer    |
   |                                      + measured view/render  |
   |                                        optimizations         |
   |                                                              |
   +-- Rollback dominates --> count burst work; apply relevant ----+
                             physics and snapshot improvements
```

B and D can be developed independently. C can be ported directly, but B offers
a smaller, easier-to-verify first step. E is independent of the physics changes.
F–H are conditional follow-ups, not prerequisites for a smoother lab.

## Change cards

### A. Establish the optimized-client baseline

**Priority:** first. **Scope:** measurement and build configuration.

**Current status:** complete for the user-accepted defined route/settings; see
2026-10-03 completion above. Matched rendered workflows, actual overlay states,
manual warmed-window gates and independent warmed Metal GPU occupancy are captured.
ExportRelease is healthy on this route; Debug is physics/replay-bound.

Compare the same route in editor Debug and an ExportRelease client. Capture cold
startup separately from repeated, warmed traversal. Record frame-time percentiles
and simulation ticks per frame, not just average FPS or the last physics tick.

Measure boundaries around:

- `OfflineServer.Update` and `CLNT.Update` in `Client/setup/ClientGame.cs`.
- `GameInterpolationReceiver.SaveInterpolationState`.
- `EntityViewUpdater` and any enabled physics/navigation debug views.
- Rendering using Godot's profiler.

**Box3D impact:** none; diagnostic timing must never influence simulation state.

**Done when:** the user-agreed route is reproduced in both Debug and an optimized
client, with cold/warm frame percentiles, actual tick counts and CPU/GPU attribution
sufficient to decide the next slice. If only Debug reproduces sustained slowdown,
record that result rather than requiring or claiming an optimized-client slowdown.

### B. Cache repeated calculations inside box SAT

**Priority:** first small narrowphase optimization.

**Current status:** implemented and verified in the follow-up above, after the
user explicitly requested this slice. Full manifolds and SAT winners match the
pre-B reference; paired active-lab box-manifold evaluation improves. This does
not close A's exact-user-route/GPU attribution or establish a full-frame speedup.

**Location:** `GameCore/Physics/Manifold.cs`, especially
`QueryEdgeDirectionsBoxBox` and `TryEdgeAxis`.

The current edge query considers 12 × 12 edge pairs. Hoist reusable edge geometry,
normalized directions, rotated face normals, and inverse rotations out of inner
loops where the exact existing calculations can be reused.

Initially preserve edge traversal order, feature IDs, tie-breaking, tolerances,
and fixed-point arithmetic results. Do not replace the search with an unrelated
collision algorithm.

**Box3D impact:** same SAT/contact construction, less repeated work. Retain the
full-box support separation fix that prevents tilted crates losing ground contact.

**Done when:** the original and optimized implementations produce identical
manifolds for regression fixtures and a deterministic corpus of box poses, with
lower measured narrowphase cost on the active lab.

### C. Port Box3D's persistent SAT feature cache

**Priority:** next narrowphase optimization with the strongest direct Box3D precedent.

**Current status:** implemented and verified; see the 2026-10-03 C completion
above for reference rules, invalidation/snapshot semantics, cache rates, paired
narrowphase measurements, fidelity checks and reproduction commands.

**Locations:**

- `GameCore/Physics/Contact.cs`
- `GameCore/Physics/ContactSystem.cs`
- `GameCore/Physics/Manifold.cs`
- Reference: `references/box3d/src/convex_manifold.c`, `b3CollideHulls`
  and `b3SATCache`.

Store the previous separating/contact face or edge features per contact. Try the
cached features and rebuild the manifold using Box3D's validity checks before
falling back to the full SAT search. Cached feature selection is distinct from
the normal/friction impulse persistence already implemented.

Define invalidation for geometry changes, shape-order changes, and contact
lifecycle transitions. Cache state must be restored consistently through rollback
and full synchronization; it must not retain entity handles across frames.

**Box3D impact:** closer to the reference's temporal-coherence optimization.
Feature selection may change compared with the current full-search-only port,
so this needs behavior and determinism tests, not just a timing comparison.

**Done when:** cache hits demonstrably avoid full searches, fallback cases remain
correct, pushed stacks stay stable, and replay produces identical per-tick state.

### D. Use packed solver body state

**Priority:** first solver optimization; can proceed independently of B/C.

**Current status:** implemented and verified; see D completion above for the
gather/scatter boundary, exact pre-D replay oracle, transient-state lifetime,
fidelity verification and modest controlled solver-phase measurements.

**Locations:**

- `GameCore/Physics/ContactSolverSystem.cs`
- `GameCore/Physics/PhysicsRuntime.cs`
- Reference: `references/box3d/src/contact_solver.c` and `solver.c`.

Gather awake bodies into reusable contiguous state buffers once per tick. Store
buffer indices in prepared constraints, solve against those buffers, and scatter
results back at a defined boundary before CCD and finalization need them.

Keep ECS components as authoritative persistent state. Treat indices and scratch
buffers as tick-local; rebuild them after snapshot loads. Preserve shared-static
body behavior, motion locks, substep count, constraint order, warm starting,
friction, restitution, and fixed-point operation order in the first version.

**Box3D impact:** adopts the reference's indexed body-state layout without
requiring parallel solving or a change in numerical behavior.

**Done when:** the lab's solve phase improves, steady-state buffers do not
allocate, existing physics checks pass, and an unchanged-arithmetic version
matches the previous implementation's state hashes.

### E. Replace full-world interpolation copies with render poses

**Priority:** secondary; promote if A shows presentation copying is significant.

**Current status:** assessed after fresh combined B+C+D captures above; defer as
an immediate performance fix. Redundant copying/managed allocation is established,
but its measured cost is small relative to the remaining Debug simulation/replay
stall. The proposed pose-only boundary and verification gates are recorded above;
implementation requires explicit approval.

**Locations:**

- `Client/setup/GameInterpolationReceiver.cs`
- `Client/setup/GameInterpolationSetup.cs`
- `Client/synchronizer/RenderInterpolation.cs`
- `Client/synchronizer/view_behavior/TransformViewBehavior.cs`

Retain only the previous poses and other values actually needed by view behaviors,
rather than copying contacts, broad-phase trees, and the entire simulation world.
Audit previous-world readers before removing the existing interpolation world.

Account for entities spawning/despawning, rollback-induced GID changes,
projectile-origin view identity, teleports, correction smoothing, and full sync.
Keep authoritative rollback snapshots intact; this change concerns presentation
state only.

**Box3D impact:** no intended change to simulated physics.

**Done when:** interpolation-copy cost and allocations fall while moving props,
platforms, projectiles, and corrected characters remain visually smooth.

### F. Move toward awake sets and persistent islands

**Priority:** conditional scaling work.

**Locations:** `GameCore/Physics/PhysicsSleep.cs`, `ContactSystem.cs`,
`ContactSolverSystem.cs`, and `PhysicsRuntime.cs`.

The port skips sleeping work but still scans bodies/contacts and rebuilds islands.
Box3D maintains persistent islands and solver sets. Consider explicit awake sets
and persistent connectivity only if measurements show scanning or wake propagation
is a meaningful remaining cost at target body counts.

**Done when:** sleeping-scene cost depends less on total world size, wake/support
removal semantics remain correct, and derived state restores safely after rollback.

### G. Prune CCD candidates spatially

**Priority:** conditional on substantial CCD phase time.

**Location:** `ContactSolverSystem.SolveContinuousCollisions` and `BroadPhase`.

CCD currently gathers every proxy once when a continuous body exists and scans
the candidates. Use spatial queries over conservative swept bounds, retaining
stable GID order. Target bounds must include motion over the tick, including
rotation; querying only old unswept target bounds can miss moving targets.

**Done when:** fewer candidates are tested with unchanged thin-wall, rotating-body,
moving-target, bullet-policy, and rollback behavior.

### H. Graph coloring, parallelism, and SIMD

**Priority:** last, only if optimized scalar code still misses the target budget.

Follow Box3D's constraint graph and solver-stage design. Avoid parallel ECS
structural changes; operate on prepared buffers with a defined deterministic
ordering policy. Fixed-point SIMD feasibility must be measured rather than
assumed from the reference's floating-point implementation.

**Done when:** real target-machine gains justify the complexity and deterministic
simulation remains verified across supported configurations.

## Fidelity and acceptance gates

For each change, measure the active push window as well as the settled scene.
Retain these gates:

- Pushed/toppled crates do not fall through the large ground.
- Stable manifold features and warm-start impulses remain valid.
- Character pushing, slopes, grounding, and moving-platform behavior remain correct.
- Sleep/wake propagation and support removal remain correct.
- Rollback and full-sync restore all behavior-affecting state.
- No new steady-state physics allocation; report structural churn separately.
- Existing physics budgets remain satisfied in Release. Add actual client frame-time
  targets after A establishes the relevant hardware and workload.

Do not start by reducing substeps, contact points, mover iterations, precision, or
collision coverage, or by increasing sleep thresholds. Those trade simulation
quality for speed and obscure whether repeated work was the real problem.

## Reproduction and verification

The opt-in diagnostic is
`tests/GameCore.Tests/PhysicsLabPerformanceTests.cs`:

```sh
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Release \
  --filter FullyQualifiedName~PhysicsLabPerformanceTests \
  --logger "console;verbosity=normal"

# Compare developer/editor-style physics cost.
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Debug \
  --filter FullyQualifiedName~PhysicsLabPerformanceTests \
  --logger "console;verbosity=normal"
```

At baseline both Release cases passed. The Debug lab case exceeded the documented
1 ms physics-plus-mover regular-tick budget; the no-stress-area case passed.
The fixture is explicit because wall-clock assertions are machine-sensitive.
Passing this probe does not prove that the client frame-time problem is resolved.

For physics changes, also run the relevant regression tests and the existing
Release load budgets:

```sh
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Release
dotnet run --project benchmarks/GameCore.Benchmarks/GameCore.Benchmarks.csproj \
  -c Release -- --physics --enforce
```

Related references:

- [Physics budgets](physics-budgets.md)
- [Box3D migration assessment](physics-box3d-migration-assessment.md)
- [Fixed-point operating limits](physics-limits.md)

## Recommended next slice

1. A is complete on the accepted route. Retain the explicit 60 FPS cap and separate
   startup/warm windows for future comparisons. Investigate isolated spikes or
   other target workloads separately if they matter to the user's experience.
2. B is now implemented at the user's request, with equality and paired narrowphase
   measurements above. Do not infer an optimized-client bottleneck from Debug.
3. C and D are implemented and verified. Fresh combined rendered measurements
   above show observational Debug improvement, modest export push simulation-cost
   improvement and no export frame-tail improvement. Do not assign causal
   full-frame gains from isolated phases or historical unpaired captures.
4. Defer E on this workload: copying is small, while Debug simulation/replay
   remains dominant. Keep F–H conditional and out of scope. For further optimization
   attribution, two paired D/pre-D Debug series show directional push improvement,
   late overlap and workload mismatch. Per-callback diagnostics now establish aged
   authoritative movement and unequal contact work. A bounded managed-profile pass
   has now implemented exact delta-rotation coefficient caching, with passing fidelity
   checks and repeated directional push improvement; late results mix directions.
   A subsequent physics-only pass retains an exact scalar matrix/vector helper in
   Solve, with an isolated 71.4% Debug operation gain and directional rendered push
   improvement, but no late frame gain. The latest bounded physics-only pass adds
   exact scalar cross products in Solve: 58.0% isolated Debug operation gain,
   directional rendered push improvement, mixed late simulation and worse late
   wall tails. The following subtraction pass adds exact -b + a raw component
   arithmetic in Solve: 65.1% isolated operation gain, directional rendered push
   improvement and both push p95s below 25 ms, but later windows still fail and
   late wall tails worsen. The latest addition pass retains exact raw component
   addition in Solve: 41.2% isolated operation gain, mixed rendered push results
   and directional late improvement, but all current candidate warmed windows fail.
   The scaling pass adds exact per-component product/shift/narrowing in Solve:
   58.1% isolated operation gain and directional Solve/complete-simulation improvement.
   All four warmed windows pass in the scaling series. Final close-out adds the
   exact dot helper (47.2% isolated operation gain), with full Release 352/352 and
   focused Debug/ExportRelease 159/159 checks. Two final candidates pass all warmed
   windows, but a third confirmation misses push p95 at 27.759 ms. Keep all seven
   changes and close the approved scalar program; overall Debug responsiveness
   remains unproven. Stop adding helpers automatically. Any later physics action
   should profile complete simulation/physics in a reproducibly failing process
   before choosing another change. Preserve input/transport as workload provenance. The historical
   absolute-cost discrepancy remains unresolved. If E is approved as allocation/architecture
   cleanup, first implement the GID-keyed previous-Transform boundary with WP-oracle
   pose/lifecycle parity tests; remove the previous world only after those gates pass.
