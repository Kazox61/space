# GameCore Physics: Supported Fixed-Point Operating Limits

This document defines the operating envelope enforced by Phase 5 of the Box3D
migration plan (`docs/physics-box3d-migration-assessment.md`). Public body,
shape, material, solver, character, and world-query boundaries reject values
outside these limits before derived physics state is changed.

## Numeric base

| Property | Value |
| --- | --- |
| Scalar representation | `Fixed32` (Q16.16, 32-bit) |
| Scalar range | [-32768, 32767.99998] |
| Scalar resolution | 1 / 65536 |
| Body positions | `Fixed64` (`FPos`) — large range, but see position limits |
| Broad-phase AABBs, all narrow-phase/solver math | `Fixed32` |

Arithmetic wraps silently on overflow (`CHECK_OVERFLOW` is not defined in any
project), so every bound below exists to keep intermediate results — squares,
cross products, mass sums, inertia determinants — inside the Q16.16 range with
headroom.

## Positions

| Limit | Value | Rationale |
| --- | --- | --- |
| Hard bound, any world coordinate | abs() < 32768 | Broad-phase AABBs are absolute Fixed32; `FWorldTransform` narrows Fixed64 positions without a range check, and any abs(p) + extent + margin >= 32768 wraps |
| Supported envelope | abs() <= 8192 | 4x headroom over the largest supported shape extent plus AABB margins |

World ray-cast origins (`PhysicsQueries.CastRay`) also narrow the absolute
origin to Fixed32 for tree traversal, so the envelope applies to queries too.
Query translations (rays, shape casts, mover casts) are limited to a magnitude
of 100 so their squared length stays inside Q16.16. Query proxies hold at most
`B3Config.MaxShapeCastPoints` (8) points, stored inline so queries never
allocate.

### Runtime escape

Creation and queries are validated against +/-8192, but bodies can still move
there under simulation. Instead of throwing mid-step, anything whose origin
crosses the escape line at +/-8064 (`PhysicsValidation.EscapeCoordinate`, the
envelope minus a 128-unit margin) leaves the simulation:

- A non-static body is disabled at the end of the solver step (its proxies and
  contacts are released) and tagged `OutOfPhysicsBounds`. Gameplay decides
  whether to destroy it, or move it back with `SetTransform` and `Enable` it,
  which clears the tag.
- The player character mover stops simulating (velocity zeroed) while its
  origin is beyond the line.

The 128-unit margin exceeds the reach of any valid shape (a 40-unit static
extent rotated about its body origin) plus one tick of travel, so escaped
geometry is still inside +/-8192 when it is caught.

## Shape dimensions

| Shape | Supported | Validated by |
| --- | --- | --- |
| Sphere radius (static/kinematic) | <= 40 | `DropAndRestTest` (r=5 ground), production ground |
| Sphere radius (dynamic) | <= 4 | `BoxOnSphereSmokeTest` etc. (r=1..3 dynamics) |
| Box half-extent (static/kinematic) | <= 40 | Production ground (40, 0.5, 40), `MoverStressTestAgainstRealGroundSize` |
| Box half-extent (dynamic) | <= 4 | `BoxOnBoxSmokeTest`, `RollbackStateHashReplayTest` crate |
| Capsule radius/length | Same per-axis spirit as box/sphere above | `CapsuleOnSphereSmokeTest`, `BoxOnCapsuleSmokeTest` |

Dynamic bounds are tighter than static bounds because mass properties scale
with r^3 and r^5 (see below). SAT, cross products, and matrix inversion also
grow with extent products, so statics near the bound should not be assumed
safe to rotate arbitrarily.

## Density and mass

Per-shape mass and inertia are computed in Fixed32; body aggregation and inversion
run in Fixed64 (see below):

- Sphere: `mass = density * 4/3*pi*r^3`, `inertia ~ 2/5 * mass * r^2`
- Box: `mass = density * 8*h^3` (cube), `inertia ~ mass * h^2` terms
- Body aggregation adds Steiner (parallel-axis) terms and a 3x3 determinant +
  inversion (`Body.MassUpdate`).

Consequences:

| Rule | Value |
| --- | --- |
| Dynamic shape density | [0, 2]; the default is `1` |
| Default density | `1`; the unsafe Box3D-derived default of `1000` was removed in Phase 5 |
| Static/kinematic density | Irrelevant — `BodyMassUpdate` returns early and never computes mass |
| Body mass | [1/4096, 1000] for a dynamic body with mass; the lower bound keeps `1 / mass` <= 4096 (a density-1 sphere needs r >= ~0.04) |
| Every inertia matrix entry | <= 1000 |
| Inverse inertia | <= 4096 per principal axis, enforced by an inertia floor (below) |

Mass aggregation, the parallel-axis terms, and the inversion run in Fixed64
and are narrowed with checked conversions.

Inertia floor: small or thin shapes (an r = 0.1 bullet, a thin capsule) have
principal moments at or below one Q16.16 ulp, whose exact inverse would
overflow. When the smallest moment of a rotating dynamic body is below 1/4096,
`BodyMassUpdate` adds 1/4096 to every principal moment. The body still rotates,
with slightly more inertia than its geometry implies. Fixed-rotation bodies skip
the inversion entirely.

Geometry, mass, and inertia limits apply together: satisfying the dimension
table above does not by itself guarantee a dynamic shape is accepted, because
its mass and every inertia entry must also stay <= 1000.

Worked example of the boundary: a density-1 sphere has inertia
`8*pi/15 * r^5`, so the inertia cap limits it to about r = 3.59 (below the
geometric r <= 4). A density-1 cube with equal half-extents h has inertia
`16/3 * h^5` per axis, so the cap limits it to about h = 2.85. Doubling the
density lowers both limits further.

## Speeds and per-tick motion

Public velocity operations (`SetLinearVelocity`, `SetAngularVelocity`, and the
impulse functions) clamp to the same limits the solver applies in
`IntegratePositions`, so any velocity the solver can produce is valid input.
Impulse sums are formed in Fixed64 and scaled before squaring, so an oversized
impulse saturates at the limit instead of wrapping. Solver configuration is
validated before each step:

| Limit | Value | Rationale |
| --- | --- | --- |
| Hard math bound, linear speed | < 181 (sqrt(32768)) | `LengthSqr(v) > maxLinearSpeed^2` wraps beyond this |
| `PhysicsWorld.MaximumLinearSpeed` default | 60 |
| Supported linear speed | <= 60 | Validated gameplay speeds are <= 12 (projectile); 60 keeps translation per tick ~1 unit at 60 Hz |
| Angular speed | Clamped at ~47.1 rad/s (MaxRotation * 60) by both the solver and the public API; bodies with `AllowFastRotation` are clamped by the API at 100 | Keeps squared magnitudes inside Q16.16 |
| Translation per tick | <= 1 unit (60 Hz) before CCD is expected to engage | Dynamic bodies whose linear and angular motion exceeds half their smallest shape extent receive automatic CCD against static geometry; explicit bullets sweep against static, kinematic, and non-bullet dynamic bodies |

## Configuration and snapshots

- `B3Config` length units must be selected before the first `PhysicsWorld` is
  created. Creation freezes the process-global scale; later attempts to change
  it fail.
- `PhysicsWorld` and `BroadPhase` snapshots use explicit stable GUIDs.
  `PhysicsWorld` uses schema version 2 and rejects version 1 snapshots;
  `BroadPhase` remains at schema version 1. Unknown versions are rejected.
- `PhysicsWorld` fields are serialized and validated during rollback and full
  synchronization. The saved length scale must match the initialized process.
- Fixed64-to-Fixed32 physics boundaries use checked narrowing and throw rather
  than wrapping.

## Events, filters, and lifecycle invariants (non-numeric Phase 0 contracts)

- Broad-phase proxies exist only after `ShapeProxySystem.Update` has run at
  least once following shape creation — queries before that miss vacuously
  (regression-covered by `RayCastMissAndFilterTest`'s positive controls).
- Correct body teardown order: release broad-phase pairs, destroy contact
  entities, destroy shapes (and their proxies) via
  `ShapeFactory.DestroyShape`, then the body — see
  `ProjectileLifecycleCountsTest` / `DestroyBodyCompletely` in the test
  harness. Production routing through a mandatory destruction API is Phase 1.
- `PhysicsDiagnostics.Capture()` is the canonical counter set (bodies, shapes,
  proxies, contacts, cached pairs, moved proxies) for lifecycle regression
  checks.
