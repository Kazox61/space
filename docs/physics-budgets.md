# GameCore Physics: Allocation and Timing Budgets

Phase 6 of the Box3D migration (`docs/physics-box3d-migration-assessment.md`)
requires explicit budgets for regular ticks and rollback bursts. This document
defines them, the scene they are measured on, and where they are enforced.

## Reference load scene

`BuildLoadScene` in `Test/Phase6Tests.cs` is shaped like a busy production
match:

| Content | Count |
| --- | --- |
| Static ground (80 x 80) and static obstacles | 1 + 10 |
| Kinematic patrollers (capsules, always moving) | 10 |
| Dynamic box piles (4 high, fall asleep) | 8 piles, 32 boxes |
| Rolling dynamic spheres (stay awake) | 28 |
| Character movers (collide, solve, cast, push each tick) | 4 |

After settling, about 47 bodies are awake, 23 are asleep, and about 90 contacts
exist, 45 of them solved each tick.

## Budgets

| Budget | Limit | Typical measurement | Enforced by |
| --- | --- | --- | --- |
| Managed allocation, steady-state tick | 0 bytes/tick | 0 bytes/tick (Debug and Release) | `SteadyStateAllocationTest`, every harness run |
| Regular tick, average (physics + 4 movers) | 1.0 ms | ~0.2 ms | `bench --enforce` |
| Typical rollback burst: snapshot load + 30 ticks | 8.0 ms (half a 60 Hz frame) | ~3.5-5 ms | `bench --enforce` |
| Full-capacity rollback burst: snapshot load + 125 ticks | 33.3 ms (two 60 Hz frames) | ~12-20 ms | `bench --enforce` |

Timing figures are Release builds on a developer laptop. Budgets keep roughly
2x headroom so shared CI runners do not flake.

The typical burst covers 500 ms of rollback, which is more than the
round trips the game is expected to see, and it fits in half a frame alongside
rendering. The full-capacity burst is the session's maximum rollback window
((framesCapacity 26 - 1) x saveEachNthTick 5 = 125 ticks, about 2 s). Reaching
it means the client is recovering from a severe stall. Dropping one extra frame
is acceptable there, but not a longer freeze.

Allocation is budgeted at zero because rollback resimulation multiplies any
per-tick garbage by the burst length. Structural churn is excluded: creating
entities and contacts, spawning, and one-time buffer growth. The measured window
follows a warm-up in which reusable buffers reach their working size.

## Running

```sh
# Full NUnit suite, including the allocation budget
dotnet test tests/GameCore.Tests/GameCore.Tests.csproj -c Release

# Timing report plus the resimulation and interpolation benchmarks
dotnet run --project benchmarks/GameCore.Benchmarks/GameCore.Benchmarks.csproj -c Release

# Timing budgets only; exits non-zero when any budget is exceeded (the CI "budgets" job)
dotnet run --project benchmarks/GameCore.Benchmarks/GameCore.Benchmarks.csproj -c Release -- --enforce
```

## Where the time goes

`PhysicsDiagnostics.LastStep` (`PhysicsStepStats`) reports per-phase timings
for the most recent step. The phases are proxy update, pair update,
narrowphase, solver prepare, solve, continuous, and finalize. It also reports
counters: awake and sleeping bodies, moved proxies, new pairs, narrowphase
evaluations, sleeping contacts, constraints, CCD bodies and hits, islands,
islands that fell asleep, and bodies woken. `PhysicsDiagnostics.CaptureBroadPhase()`
reports tree height against a balanced tree, plus the area ratio. The client
overlay (F3, see `Client/setup/PhysicsDebugView.cs`) shows both live.

On the load scene, the solve phase dominates the physics step, and character
movers cost about as much as the rest of the physics step. The next scaling
steps would be broad-phase CCD candidate pruning (CCD currently scans every
proxy once per tick when any fast body exists) and, only if profiling
justifies it, Phase 7's graph coloring and SIMD.
