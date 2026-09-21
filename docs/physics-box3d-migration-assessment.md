# GameCore Physics and Box3D Migration Assessment

## Purpose

This document compares `GameCore/Physics` with the reference implementation in
`box3d/`. It identifies what has been migrated, where the current migration is
incomplete or unsafe, what is required for the current game, and which Box3D
features should remain optional until gameplay requires them.

## Executive Summary

`GameCore/Physics` is a substantial deterministic fixed-point subset of Box3D.
It is not a placeholder: it contains a functioning convex collision pipeline,
a sub-stepped contact solver, rollback-aware broad phase, spatial queries, and
a capable character mover.

The implementation is nevertheless not production-ready. The most urgent
problems are lifecycle and state-consistency defects rather than missing
advanced features. Destroying a body can leave shape entities, broad-phase
proxies, contacts, and pair-cache entries alive. Contact filtering and warm
starting are incomplete, fast projectiles have no continuous collision
detection, and direct mutation of bodies and shapes can invalidate derived
physics state.

Full Box3D parity should not be the immediate objective. The current game first
needs reliable lifecycle management, deterministic rollback, projectile CCD,
stable contacts, safe mutation APIs, fixed-point validation, and automated
tests. Features such as joints, height fields, SIMD, and parallel solving should
be added only when gameplay or profiling justifies them.

## Current Capabilities

The current implementation includes:

- Static, kinematic, and dynamic ECS bodies.
- Sphere, capsule, and oriented-box shapes.
- Multiple shapes linked to one body.
- Dynamic-tree broad phase with static, kinematic, and dynamic trees.
- Rollback serialization of dynamic trees, moved proxies, and pair state.
- Sphere/sphere, sphere/capsule, capsule/capsule, hull/sphere,
  hull/capsule, and hull/hull manifolds.
- Up to four manifold points for box contacts.
- Sub-stepped soft contact solving.
- Friction, restitution, and rolling resistance.
- Mass, center-of-mass, and inertia computation.
- Linear and angular motion locks.
- GJK distance and linear shape casts.
- World ray casts.
- A swept-capsule character mover with grounding and dynamic-body pushing.
- Contact begin and end events.
- Fixed-point adaptations intended to reduce overflow in several hot paths.

Important implementation locations include:

- World configuration: `GameCore/Physics/PhysicsWorld.cs`
- Bodies and mass: `GameCore/Physics/Body.cs` and
  `GameCore/Physics/Body.MassUpdate.cs`
- Shape lifecycle: `GameCore/Physics/ShapeFactory.cs`
- Broad phase: `GameCore/Physics/BroadPhase.cs`
- Contact lifecycle: `GameCore/Physics/ContactSystem.cs`
- Narrow phase: `GameCore/Physics/Manifold.cs`
- GJK and shape casts: `GameCore/Physics/GJK.cs` and
  `GameCore/Physics/Distance.cs`
- Solver: `GameCore/Physics/ContactSolverSystem.cs`
- Character movement: `GameCore/Physics/CharacterMover.cs` and
  `GameCore/Physics/MoverSolver.cs`
- Production system order: `GameCore/SimulationSetup.cs`

## Box3D Comparison

| Area | GameCore status | Box3D reference |
| --- | --- | --- |
| Sphere, capsule, and box collision | Implemented | `box3d/src/convex_manifold.c` |
| General convex hulls | Missing; GameCore `Hull` is box-only | `box3d/src/hull.c` |
| Dynamic broad phase | Implemented, simplified | `box3d/src/broad_phase.c` |
| Contact solver | Implemented, single-threaded subset | `box3d/src/contact_solver.c` |
| Character mover | Largely implemented | `box3d/src/mover.c` |
| Persistent warm starting | Incomplete | `box3d/src/contact.c` |
| Continuous collision detection | Missing | `box3d/src/distance.c`, `box3d/src/solver.c` |
| Sleeping and islands | Missing | `box3d/src/island.c`, `box3d/src/solver_set.c` |
| Sensors | Partial | `box3d/src/sensor.c` |
| World queries | Ray casts plus low-level casts | Full overlap and shape-cast API |
| Forces and impulses | Missing public API | `box3d/src/body.c` |
| Joints | Enum only | `box3d/src/*_joint.c` |
| Mesh, height field, and compound shapes | Enum only | Dedicated Box3D subsystems |
| Parallelism and SIMD | Missing | Scheduler, graph, and SIMD solver |
| Debug draw and profiling | Missing | World counters and debug draw |
| Rollback | Strong ECS snapshot integration | Recording and replay system |

## What Was Migrated Well

### Convex Collision Subset

The supported primitive combinations have real analytic or SAT-based manifold
generation rather than relying exclusively on a single GJK point. Box contacts
use clipping and can produce multi-point manifolds.

### Solver Structure

The solver follows the Box3D soft-step structure closely enough to provide
gravity, damping, speculative contacts, biased and unbiased solving,
restitution, friction, rolling resistance, and sub-stepping.

### Character Mover

The character mover is one of the most complete migrated subsystems. It
collects collision planes, solves position constraints, clips velocity, probes
ground, supports slopes, and pushes dynamic bodies.

### Rollback-Aware Broad Phase

The broad-phase resource explicitly serializes its trees, pair set, and moved
proxy list. Physics relation types use stable GUIDs for cross-world snapshot
compatibility. These are important adaptations for the ECS and rollback
architecture rather than direct copies from Box3D.

### Fixed-Point Adaptations

Several GJK and solver paths explicitly account for Q16.16 overflow. This is
necessary because a direct floating-point-to-fixed-point translation would not
be safe.

## Critical Findings

### 1. Body Destruction Leaks Physics State

`DeathSystem` destroys a body entity directly:

- `GameCore/Systems/DeathSystem.cs:14-18`

It does not destroy owned shape entities, broad-phase proxies, contacts, or
pair-cache entries. The required proxy cleanup exists only in
`ShapeFactory.DestroyShape`:

- `GameCore/Physics/ShapeFactory.cs:38-50`

There are no production callers that cascade body destruction through this
operation. Repeated projectile and dummy destruction can therefore leave:

- Orphan shape entities.
- Stale broad-phase proxies.
- Contacts whose body owner no longer resolves.
- Pair-cache entries that are never released.
- Increasing snapshot size and query cost.

This is the highest-priority production blocker.

### 2. Broad-Phase Pair Lifecycle Is Incorrect

The broad phase reserves a pair before `ContactSystem.TryCreateContact` applies
full filtering. If filtering rejects the pair at
`GameCore/Physics/ContactSystem.cs:88-90`, no contact exists to release the
pair later.

Consequences include:

- Rejected pairs accumulating indefinitely.
- Runtime filter changes failing to enable a previously rejected pair.
- Some invalid contacts being destroyed without making the pair eligible for
  recreation.
- Snapshot state growing with historical overlaps.

Existing contacts also do not re-evaluate filters.

### 3. Same-Body and Non-Responsive Pairs Are Created

Contact creation does not reject shapes owned by the same body. The solver later
skips such contacts, but they can still consume resources and emit events.

Static/static and other non-responsive body combinations are also generated.
Box3D suppresses these pairs before creating contacts.

### 4. Normal Warm Starting Is Lost

The solver stores normal impulses in each manifold point, but
`ContactSystem` replaces the complete manifold on the next tick:

- `GameCore/Physics/ContactSystem.cs:53-57`

The implementation has no stable contact feature IDs and does not match old
points to new points. Normal impulses are therefore reset each tick. Friction
and rolling impulses persist at contact level but can be applied in a changed
tangent basis.

This differs materially from Box3D's contact persistence and can reduce stack
stability.

### 5. No Continuous Collision Detection

`Body.IsBullet` exists but has no behavior. Time of impact is explicitly not
ported in `GameCore/Physics/Distance.cs`.

The current pipeline updates proxies before the solver moves bodies. It uses
endpoint AABBs rather than swept AABBs. A fast projectile can cross a thin
collider without generating a broad-phase pair.

Speculative contacts do not solve this case because they require a pair to
already exist.

### 6. Direct Mutation Can Corrupt Derived State

`Body` and `Shape` expose values whose mutation requires coordinated updates:

- Body transform and center of mass.
- Body type and broad-phase tree membership.
- Motion locks and inverse inertia.
- Shape geometry and AABB.
- Shape density and body mass.
- Collision filters and existing contacts.

No safe equivalents of Box3D's body and shape mutation operations exist. Direct
transform mutation can desynchronize `Body.Transform` and `Body.Center`.
Changing a static transform does not update its proxy, and changing a body type
does not migrate its proxy between trees.

### 7. Declared API Exceeds Implemented Behavior

Several fields or enum values suggest features that do not exist:

- `Body.EnableSleep`
- `Body.IsAwake`
- `Body.IsEnabled`
- `Body.IsBullet`
- `Body.EnableContactRecycling`
- Shape event flags
- Custom filtering
- Material tangent velocity
- Joint types
- Compound, mesh, and height shape types

Unsupported geometry produces an empty manifold in release builds. The API
should implement these contracts or stop exposing them as supported behavior.

### 8. Sensor Semantics Are Incomplete

Sensors are excluded from rigid response, but they use ordinary contact
entities and contact events. `EnableSensorEvents` is unused. There is no
dedicated overlap state, sensor polling API, or separate sensor begin/end event
type.

Ray and character queries always skip sensors rather than allowing callers to
choose an inclusion policy.

### 9. Motion Locks Are Applied Incompletely

Motion locks are applied during integration, but contact solving can reintroduce
locked velocity components. Per-axis angular locks do not consistently affect
effective inverse inertia.

Locks should be represented in solver mass/inertia and enforced after every
velocity-changing stage.

### 10. Fixed-Point Limits Are Not Enforced

Most simulation math uses Q16.16 `Fixed32`, with a magnitude range of roughly
32768. Current risks include:

- Default density `1000` overflowing ordinary mass and inertia calculations.
- Absolute broad-phase coordinates wrapping outside the Fixed32 range.
- Large dimensions overflowing SAT, cross products, or matrix inversion.
- Fixed64-to-Fixed32 narrowing without range validation.

The game already overrides density in some call sites because of observed
overflow. Numeric limits need to be part of the public physics contract.

### 11. `PhysicsWorld` Is Not Included in Snapshots

`PhysicsWorld` is registered as a world resource, but it does not provide the
serialization identity and methods required by the StaticEcs serializer.
Runtime changes to gravity, solver settings, or substep count therefore do not
roll back with the rest of the world.

The resource must either be immutable startup configuration or be explicitly
serialized.

### 12. Hot Paths Allocate

Current allocation sources include:

- New body and contact-constraint lists in every solver update.
- Array-backed simplex caches during shape casts.
- Clipping and manifold arrays for hull contacts.
- Shape-proxy point arrays.
- Static character-mover scratch collections that are not reentrant.

These allocations are especially costly during rollback resimulation.

## Query and Contract Problems

- Character mover queries ignore collision filters.
- Broad-phase category bits are stored but normal pair queries use
  `ulong.MaxValue`, preventing category pruning.
- World ray casts always exclude sensors.
- There is no public exact world overlap query.
- There is no public generic world shape cast.
- Hollow-sphere ray casting uses inconsistent fraction units.
- Shape-cast documentation conflicts with initial-overlap behavior.
- `Manifold.MinSeparation` documentation describes the shallowest separation,
  but the implementation returns the deepest separation.

## Missing Box3D Subsystems

The following major Box3D areas have not been migrated:

- General convex hull construction and topology.
- Static triangle meshes and mesh contacts.
- Height fields.
- Baked compounds.
- Joint components, lifecycle, and solving.
- Islands, solver sets, sleeping, and wake propagation.
- Constraint graph coloring.
- Continuous collision detection and TOI.
- Dedicated sensor processing.
- Parallel broad phase, narrow phase, and solver stages.
- SIMD contact solving.
- Integrated debug drawing.
- Per-phase profiling and timing metrics (world-count physics counters exist
  via `PhysicsDiagnostics.Capture()`).
- Operation recording, replay files, and state-hash diagnostics.

## Required Scope for the Current Game

The current production path primarily uses boxes, capsules, spheres, kinematic
projectiles, and the capsule mover. The immediate target should therefore be a
reliable focused engine rather than complete Box3D parity.

Required before production use:

1. Correct body, shape, contact, proxy, and pair destruction.
2. Deterministic rollback and full-sync behavior.
3. Continuous collision detection for projectiles.
4. Persistent and stable contact manifolds.
5. Safe body and shape mutation APIs.
6. Correct sensor and contact event semantics.
7. Collision filtering for world and character queries.
8. Enforced fixed-point operating limits.
9. Automated regression and determinism tests.
10. Sleeping and allocation reduction when realistic body counts require them.

Content-dependent features:

| Feature | Add when |
| --- | --- |
| Static meshes or baked compounds | Real levels cannot be represented efficiently with boxes |
| Height fields | The game uses terrain |
| General convex hulls | Collision authoring requires non-box convex objects |
| Joints | Gameplay requires vehicles, doors, ropes, or mechanisms |
| Parallel solver and SIMD | Profiling proves the single-threaded solver misses budgets |
| Recording and replay files | Desync diagnosis or developer tooling requires them |

## Implementation Plan

### Phase 0: Establish the Safety Baseline

Goal: make failures reproducible before changing behavior.

1. Convert the physics harness into a discoverable test project or run the
   executable explicitly in CI.
2. Run physics tests in Debug and Release.
3. Add state-hash comparison across live simulation and rollback replay.
4. Add counters for bodies, shapes, proxies, contacts, and cached pairs.
5. Fix sensor/filter ray tests so proxies exist before querying.
6. Define supported limits for positions, dimensions, density, mass, and speed.

Acceptance criteria:

- Physics checks run automatically and fail CI on error.
- A 10,000-cycle projectile spawn/destruction test has constant physics counts.
- Rollback replay produces identical state hashes.

### Phase 1: Repair Lifecycle and Pair Management

Goal: eliminate stale ECS and broad-phase state.

1. Add a mandatory physics body-destruction operation.
2. Destroy all owned shapes through `ShapeFactory.DestroyShape`.
3. Destroy associated contacts before shape links become invalid.
4. Release every broad-phase pair on every contact-destruction path.
5. Release pairs rejected by filtering or unsupported geometry.
6. Reject same-body pairs.
7. Reject non-responsive body-type combinations.
8. Route `DeathSystem` and every gameplay destruction path through this API.
9. Add validation that every proxy resolves to a live shape and body.

Acceptance criteria:

- No shape, proxy, contact, or pair remains after body destruction.
- Repeated projectile creation and destruction returns counts to baseline.
- Rollback across destruction reproduces identical state.

### Phase 2: Introduce Safe Mutation APIs

Goal: keep all derived physics state synchronized.

Add explicit operations for:

- Body creation and destruction.
- Teleporting or setting transforms.
- Changing body type.
- Enabling and disabling bodies.
- Setting velocity.
- Changing shape filters.
- Changing shape geometry and density.
- Applying linear and angular impulses.
- Applying forces and torques.

These operations must update center of mass, inertia, proxies, tree membership,
contacts, pair state, mass properties, and wake state as applicable.

Acceptance criteria:

- Teleported dynamic bodies do not snap back.
- Moved static bodies update contacts and queries.
- Body-type changes migrate proxies to the correct tree.
- Filter changes immediately create or remove eligible contacts.
- Geometry and density changes update mass and inertia.

### Phase 3: Fix Contact Persistence and Events

Goal: provide stable stacks and reliable gameplay notifications.

1. Port stable contact feature IDs.
2. Match old and new manifold points.
3. Preserve normal impulses only for matching points.
4. Rotate or clear friction impulses when the basis changes.
5. Add the near-parallel capsule two-point manifold refinement.
6. Define actual-touch versus speculative-contact semantics.
7. Honor contact event flags.
8. Add dedicated sensor begin/end events.
9. Add post-solve hit events with point, normal, speed, and impulse.
10. Re-evaluate filters for existing contacts.

Acceptance criteria:

- Warm starting reduces resting jitter and stack drift.
- Long-running box and capsule stacks remain stable.
- Sensors never generate rigid response.
- Event flags and runtime filter changes behave deterministically.

### Phase 4: Add Game-Critical CCD and Queries

Goal: make projectile and character collision reliable.

1. Add swept broad-phase AABBs for moving bodies.
2. Port convex time of impact.
3. Make `IsBullet` functional.
4. Define bullet behavior against static, kinematic, and dynamic bodies.
5. Restrict CCD work to fast or explicitly marked bodies.
6. Add a public world shape-cast API.
7. Add exact world overlap queries.
8. Add configurable sensor inclusion.
9. Add query filters to the character mover.
10. Correct hollow-sphere ray fractions.

Acceptance criteria:

- Maximum-speed projectiles cannot tunnel through the minimum supported
  collider thickness.
- CCD reproduces exactly after rollback.
- Character movement respects category, mask, and group filtering.

### Phase 5: Harden Fixed-Point and Rollback Behavior

Goal: establish a deterministic operating envelope.

1. Replace the unsafe default density or use wider mass and inertia arithmetic.
2. Validate geometry, material, and solver inputs.
3. Add checked diagnostics for narrowing conversions and dangerous arithmetic.
4. Restrict physics to a documented coordinate range or introduce an
   origin-relative broad-phase frame.
5. Make `PhysicsWorld` immutable or serialize it.
6. Freeze global `B3Config` state after initialization.
7. Version physics snapshot data.
8. Test full synchronization between distinct client and server world types.

Acceptance criteria:

- Invalid input fails at creation instead of corrupting simulation.
- Tests cover coordinate, mass, inertia, speed, and dimension boundaries.
- Physics configuration restores correctly after rollback.
- Supported scenarios produce identical Debug and Release state hashes.

### Phase 6: Scale and Observability

Goal: support realistic production loads.

1. Reuse solver body and constraint buffers.
2. Replace allocating simplex caches with inline storage.
3. Remove manifold clipping allocations.
4. Make character-mover scratch state world-scoped and reentrant.
5. Add sleeping and wake propagation.
6. Add islands before attempting parallel solving.
7. Add per-phase counters and timings.
8. Add debug drawing for AABBs, contacts, normals, proxies, and sleep state.
9. Define allocation and timing budgets for regular ticks and rollback bursts.

Acceptance criteria:

- Normal ticks allocate zero or near-zero managed memory.
- Sleeping scenes scale primarily with awake bodies.
- Rollback bursts remain within the game frame budget.
- Diagnostics expose stale state and broad-phase degradation.

### Phase 7: Port Content-Driven Features

Add these only when required by gameplay:

1. Baked static compounds or static meshes.
2. General convex hulls.
3. Height fields.
4. Required joint types.
5. Joint filtering and events.
6. Parallel graph-colored solving.
7. SIMD.
8. Recording and replay tooling.

Each subsystem should be introduced with focused parity scenarios against the
Box3D reference rather than as one large migration.

## Production Release Gate

Treat the physics implementation as game-ready when:

- Body destruction cannot leak shapes, proxies, contacts, or pairs.
- Projectiles use deterministic CCD.
- Contact warm starting persists correctly.
- Runtime changes go through safe mutation APIs.
- Sensors and collision filters behave consistently.
- Fixed-point limits are documented and enforced.
- Rollback and full-sync state hashes remain identical.
- Production setup tests run in CI.
- Realistic load and rollback benchmarks satisfy an explicit budget.

## Verification Status

At the time of this assessment:

- `dotnet run --project Test/Test.csproj` completed with all current checks
  passing.
- `dotnet build Space.sln` compiled GameCore, client, server, and test projects,
  but the solution build failed during packaging because these files were
  missing:
  - `static-rollback/CHANGELOG.md`
  - `static-rollback-litenetlib/CHANGELOG.md`

The solution-build failure is unrelated to physics behavior, but it must be
resolved before the solution build can serve as a CI release gate.

The existing test harness is a console executable rather than a test-runner
project. Its coverage includes useful collision, rollback, ray-cast, sensor,
and character-mover regressions, but it does not yet cover lifecycle leaks,
CCD, runtime filter mutation, large coordinates, cross-runtime determinism, or
the full production system setup.

## Phase 0 Implementation Status

Phase 0 (safety baseline) is implemented:

- CI: `.github/workflows/physics-tests.yml` runs the harness in Debug and
  Release on pull requests, pushes to main, and manual dispatch. It is scoped
  to `Test/Test.csproj` to stay independent of the solution packaging blocker
  above.
- Counters: `BroadPhase` exposes `ProxyCount`, `CachedPairCount`, and
  `MovedProxyCount`; `PhysicsDiagnostics.Capture()` returns the full counter
  set (bodies, shapes, proxies, contacts, cached pairs, moved proxies).
- State hash: `RollbackStateHashReplayTest` hashes the complete world snapshot
  (FNV-1a 64) after every tick and requires a rollback replay to reproduce all
  per-tick hashes. `BroadPhase.Write` now serializes the pair set in sorted
  order so those hashes are independent of HashSet insertion history (a
  restored world re-inserts pairs in a different order than a live one).
- Lifecycle baseline: `ProjectileLifecycleCountsTest` runs 10,000
  spawn/destroy cycles and requires every physics count to stay at baseline.
  Phase 1 now provides the correct sequence (release pairs, destroy contacts,
  destroy shapes via `ShapeFactory.DestroyShape`, then the body) through
  `PhysicsBodyLifecycle.DestroyBody`, and routes physics-body destruction from
  `DeathSystem` through that operation.
- Ray tests: `RayCastMissAndFilterTest` previously cast before proxies
  existed, so its sensor and filter checks passed vacuously. It now ticks
  `ShapeProxySystem` first and asserts positive controls (proxy presence,
  hittability of the filtered shape with an allowing filter).
- Supported limits: `docs/physics-limits.md` defines the Q16.16 operating
  envelope for positions, dimensions, density, mass, and speeds, and records
  the two known-unsafe defaults (shape density 1000,
  `MaximumLinearSpeed` 400) whose enforcement/replacement is Phase 5 work.

Flagged for Phase 1 investigation: production `GameUpdateRoot.Update` calls
`Systems.Update()` without advancing the world tick (`W.Tick()`), while every
harness test does both; event-ring and tracking semantics across production
sessions should be verified against the harness behavior.

## Phase 1 Implementation Status

Phase 1 (lifecycle and pair management) is implemented:

- Body lifecycle: `PhysicsBodyLifecycle.DestroyBody` is the mandatory teardown
  boundary. It snapshots owned shapes, destroys every associated contact,
  destroys shapes through `ShapeFactory.DestroyShape`, and destroys the body
  last. `DeathSystem` now uses this operation for every entity carrying a
  `Body`.
- Contact lifecycle: `ContactLifecycle.DestroyContact` is shared by explicit
  body teardown and every `ContactSystem` destruction path. Contacts retain
  their shape GIDs independently of ECS links, so pair release and end-touch
  events remain possible when a relation target no longer resolves.
- Pair lifecycle: broad-phase pair acceptance is transactional. Pairs rejected
  because of stale entities, missing owners, collision filters, unsupported
  geometry, same-body ownership, or non-responsive body types are removed from
  the cache immediately. Shape destruction also purges any historical cached
  pairs involving that shape.
- Pair eligibility: contact creation now requires supported convex geometry,
  distinct owning bodies, and at least one dynamic body, matching the Box3D
  body-type rule for rigid contacts.
- Validation: `BroadPhase.Validate()` verifies proxy-to-shape-to-body ownership,
  proxy keys and tree membership, pair endpoints, and one-to-one cached-pair to
  contact consistency.
- Regression coverage: the harness now covers rejected pair cleanup,
  multi-shape body teardown, `DeathSystem` routing, rollback across destruction,
  full-state hash reproduction, and the 10,000-cycle lifecycle count check using
  the production API.

The `GameUpdateRoot.Update` tick-advancement question remains open. Phase 1 does
not use tracking filters, and the production `DeathSystem` path is covered with
the current event receiver behavior, but session-level tick ownership should be
resolved separately before adding tick-sensitive tracking logic.
