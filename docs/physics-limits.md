# GameCore Physics: Supported Fixed-Point Operating Limits

Phase 0 deliverable of the Box3D migration plan
(docs/physics-box3d-migration-assessment.md). It defines the operating envelope
the Q16.16 fixed-point math can actually support. Enforcement (validation at
creation, checked conversions) is Phase 5; until then these limits are a
contract callers must honor, backed by the tests noted below.

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
| Hard bound, any world coordinate | abs() < 32768 | Broad-phase AABBs are absolute Fixed32; `FWorldTransform` narrows Fixed64 positions without a range check, and any |p| + extent + margin >= 32768 wraps |
| Supported envelope | abs() <= 8192 | 4x headroom over the largest supported shape extent plus AABB margins |

World ray-cast origins (`PhysicsQueries.CastRay`) also narrow the absolute
origin to Fixed32 for tree traversal, so the envelope applies to queries too.

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

Dynamic-shape mass math runs entirely in Fixed32:

- Sphere: `mass = density * 4/3*pi*r^3`, `inertia ~ 2/5 * mass * r^2`
- Box: `mass = density * 8*h^3` (cube), `inertia ~ mass * h^2` terms
- Body aggregation adds Steiner (parallel-axis) terms and a 3x3 determinant +
  inversion (`Body.MassUpdate`).

Consequences:

| Rule | Value |
| --- | --- |
| Dynamic shape density | Set explicitly; `1` is the only validated value |
| Default density 1000 | **Unsafe — known issue** (assessment finding 10). Overflows mass/inertia for any realistic size; every production/test call site already overrides it. Fix (wider arithmetic or new default) is Phase 5. |
| Static/kinematic density | Irrelevant — `BodyMassUpdate` returns early and never computes mass |
| Body mass | <= 1000 |
| Every inertia matrix entry | <= 1000, determinant > 0 (required for the inversion) |

Worked example of the boundary: a dynamic sphere with density 1 is safe up to
about r = 5 (r = 8 gives inertia ~ 5.5e4, already beyond 32768). Satisfying the
dimension table above (dynamic r <= 4) keeps both mass and inertia comfortably
in range.

## Speeds and per-tick motion

`ContactSolverSystem.IntegratePositions` clamps speeds by comparing squared
magnitudes in Fixed32, so any squared magnitude must stay below 32768:

| Limit | Value | Rationale |
| --- | --- | --- |
| Hard math bound, linear speed | < 181 (sqrt(32768)) | `LengthSqr(v) > maxLinearSpeed^2` wraps beyond this |
| `PhysicsWorld.MaximumLinearSpeed` default 400 | **Unsafe — known issue** | 400^2 = 160000 wraps in Q16.16; the clamp itself misbehaves. Phase 5 replaces the default. Keep gameplay speeds well under 181 meanwhile. |
| Supported linear speed | <= 60 | Validated gameplay speeds are <= 12 (projectile); 60 keeps translation per tick ~1 unit at 60 Hz |
| Angular speed | Auto-clamped at ~47.1 rad/s (MaxRotation * 60); supported <= 30 | Clamp arithmetic is safe; leave headroom for solver cross products |
| Translation per tick | <= 1 unit (60 Hz) | Keeps swept motion inside AABB margins and avoids tunneling until CCD lands (Phase 4) |

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
