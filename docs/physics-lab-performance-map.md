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
3. Re-measure the current bottleneck before tackling C and D as separate,
   independently verified changes; both remain unimplemented.
4. Promote E or F–H only when the updated profile supports them.
