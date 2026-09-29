# Navigation Assessment

> Question: how should Space handle navmesh-based navigation, and can we reuse
> code from `Klotho/`, `box3d/`, or [DotRecast](https://github.com/ikpil/DotRecast)?
> Scope: deterministic runtime pathfinding for rollback. All claims verified
> against local source unless marked otherwise.
>
> Status: Phases 0 to 7 are implemented. Maps classify their static geometry
> for navigation (a `NavigationComponent` on `StaticGeometry` placements since
> format version 6; a `LevelCollider` field before), the level format preserves
> that classification, and `Space.NavBuilder` deterministically triangulates the
> fixed-point boxes. `GameCore/Navigation` holds the fixed-point runtime core
> (mesh, query, A*, funnel), and `Space.NavBuilder` bakes that mesh from a
> level's static geometry with DotRecast and a fixed-point build pipeline. Level
> export bakes the navmesh into format version 4, and client and server install
> it as a per-world resource. A `NavAgent` steers a `NavCharacter` through the
> same `CharacterMoverSystem` the player uses, and navigation is verified
> through rollback, full sync, and re-simulation bursts. A client-only overlay
> (F5) draws the navmesh, its sources, and each agent's route.
>
> Phase 8 is in progress: switchable zones (steps 1 and 2) are implemented. A
> `NavZone` volume in a map splits the baked navmesh, level format version 5
> stores the zone table, a snapshotted `NavZoneState` entity per zone drives
> the mesh's blocked and cost flags every tick, and agents re-plan when any
> zone changes. Gameplay drives zones through map-placed doors: a `Door` is a
> kinematic box that blocks its zone unless fully open, and a `PressurePlate`
> sensor toggles it when a character steps on. Characters touch sensors through
> a `SensorProxy`, a kinematic body with a sensor capsule that follows each
> mover. ORCA avoidance (steps 3 and 4) is not started.

## Summary

Port the core of Klotho's deterministic navigation runtime into
`GameCore/Navigation`. Build the navmesh with our own offline builder:
the `StaticGeometry` placements resolve to fixed-point
`LevelData.NavigationSources`, DotRecast bakes their walkable surface, and a port of
Klotho's build pipeline turns it into a fixed-point navmesh stored in the
level file (see `level-pipeline.md`, Phase 5). Godot's
`NavigationRegion3D` bake is not used. Do not run DotRecast in the simulation.
box3d has no navigation.

## Candidate Sources

| Source | License | Math | Usable for |
| --- | --- | --- | --- |
| Klotho `Runtime/Deterministic/Navigation/` | Apache-2.0 | `FP64` (Q32.32) | Runtime query, A*, funnel; build pipeline (triangles → navmesh) |
| DotRecast | zlib | `float` | Offline baking only (chosen baker) |
| Godot `NavigationRegion3D` | MIT | `float` | Offline baking; not used, to keep the builder independent of Godot |
| box3d | MIT | `float` | Nothing; physics only |

### DotRecast

DotRecast is a C# port of Recast (voxel-based mesh building) and Detour
(runtime queries, crowd). All of it is `float`. Space is rollback-networked:
every peer must produce bit-identical paths, and float pathfinding, funnel
smoothing, and crowd steering do not guarantee that across platforms and JIT
modes. Converting Detour to fixed point is a large port that Klotho has
already done.

Baking determinism does not matter, because the baked result is shipped as
data. DotRecast is therefore the baker: it runs as plain .NET, needs no
Godot, and bakes from the same static colliders the simulation uses. Only its
Recast part (`RcBuilder`) is used; Detour stays out of the runtime.

### box3d

box3d is a physics engine with no navigation. Its triangle-mesh shapes are
collision geometry (see `physics-box3d-migration-assessment.md`, "Missing
Box3D Subsystems"). Collision meshes and navmeshes are separate data.

### Klotho

`references/Klotho/com.xpturn.klotho/Runtime/Deterministic/Navigation/` holds 68 files
(~23.4k lines): a complete fixed-point navmesh runtime, ORCA avoidance, and a
large runtime-rebake subsystem. Documentation: `references/Klotho/Docs/Navigation.md`,
`references/Klotho/Docs/Navigation.Rebake.md`, and
`references/Klotho/Docs/NavMeshVisualizer.Godot.md`. Tests:
`references/Klotho/Samples/Klotho.Runtime.Tests/Deterministic/Navigation/`.

## What to Take From Klotho

### Port (runtime core)

None of these files import Klotho's ECS.

| File | Lines | Role |
| --- | --- | --- |
| `FPNavMesh.cs` | 245 | Vertices, triangles, spatial grid |
| `FPNavMeshTriangle.cs` | 177 | Adjacency, portals, area mask, cost |
| `FPNavMeshQuery.cs` | 1034 | Triangle lookup, height sampling, nearest point |
| `FPNavMeshPathfinder.cs` | 665 | A* over the triangle graph, allocation-free |
| `FPNavMeshBinaryHeap.cs` | 148 | A* open set |
| `FPNavMeshFunnel.cs` | 388 | Corridor to waypoints (simple stupid funnel) |
| `FPNavMeshSerializer.cs` | 278 | `.bytes` read/write |
| `FPNavTuning.cs` | 342 | Buffer sizes and loop budgets (referenced 23 times by the above) |

Plus the build pipeline, `FPNavMeshBuildPipeline.cs` (2733 lines; port only
the full `Build(vertices, indices, areas, cellSize, ...)` path, not the
incremental rebake paths). It takes plain welded triangles, so it accepts
DotRecast's output directly, and produces the adjacency, portals and spatial
grid the runtime needs.

Klotho's Godot exporter (`GodotFPNavMeshExporter.cs`) is not used: it only
reads Godot's `NavigationRegion3D` bake.

External dependencies to replace:

- `xpTURN.Klotho.Deterministic.Math`: `FP64`, `FPVector2`, `FPVector3`
  (see "Number format" below).
- `xpTURN.Klotho.Deterministic.Geometry`: `FPBounds2` (3 uses; small struct).
- `xpTURN.Klotho.Logging`: optional logger parameter; drop or adapt.
- `xpTURN.Klotho.Serialization`: used only by the serializer.

### Rewrite

- `FPNavAgentSystem.cs` (2525 lines) and `NavAgentComponent.cs` depend on
  Klotho's `Frame`/`EntityRef`. Write a small StaticEcs system instead.
- Movement: feed the funnel's next waypoint into the existing
  `CharacterMover` (`GameCore/Physics/CharacterMover.cs`) as desired velocity.
  Collision, grounding, and slopes come for free, and there is only one
  movement path to keep deterministic.

### Port later (dynamic navigation)

Scope is limited to two mechanisms (details in `level-pipeline.md`,
Phase 5, "Dynamic navigation"):

- **A. Avoidance for moving obstacles.** `FPNavAvoidance.cs` (1012 lines) and
  `FPNavMeshObstacleExtractor.cs`. Static-obstacle loading
  (`LoadObstacles`, `Extract`) has no ECS coupling; `ComputeNewVelocity`
  takes Klotho's `Frame`/`EntityRef` and must be rewritten against StaticEcs.
- **B. Zones switched on and off.** Uses the triangle fields
  `isBlocked`, `areaMask` and `costMultiplier` that the pathfinder already
  honours. The on/off state lives in snapshotted ECS state and is written into
  the mesh before pathfinding, following Klotho's "installed mesh is derived
  state" rule (`Klotho/Docs/Navigation.Rebake.md`).

### Skip

- Runtime rebake, placement, constrained Delaunay, abstract graph: the
  majority of the 23k lines, needed only for RTS-style building placement.
  Not planned.

## Porting Constraints

### Number format

| Type | Fractional bits | Integer range |
| --- | --- | --- |
| Klotho `FP64` | 32 | ±2^31 |
| Space `Fixed64.FP` | 31 | ±2^32 |
| Space `Fixed32.FP` | 16 | ±32768 |

- Port to `Fixed64`. The A* heuristic, point-in-triangle tests, and funnel
  cross products square coordinates, which overflows `Fixed32` on ordinary
  level sizes. `Transform.Position` already uses `Fixed.FPos` with Fixed64
  components, so no float conversion is needed at the gameplay boundary.
- Klotho's `.bytes` stores raw Q32.32 values. Space cannot reinterpret them.
  Either write Space's own serializer (preferred; the format is small) or
  shift raws right by one on load.
- Any Klotho code that manipulates `RawValue` or `FRACTIONAL_BITS` directly
  must be reviewed rather than search-replaced.

### Coordinate handedness

Klotho documents that Unity-baked `.bytes` do not work in Godot because of
handedness. This does not affect Space: the navmesh is built from the exported
static colliders, which are already in simulation coordinates.

### Rollback

- The navmesh topology and geometry (vertices, triangles, adjacency, portals,
  grid) are immutable at runtime: store them as a non-snapshotted resource
  loaded identically on server and client. The per-triangle flags
  `isBlocked`, `areaMask` and `costMultiplier` are the exception: they are
  derived state, re-written from the snapshotted ECS zone state before each
  pathfinding pass (B above), so a rollback restores them.
- Agent state (destination, corridor, current triangle, status, repath tick)
  must live in a StaticEcs component so it rolls back. Klotho's corridor is a
  128-entry `fixed int` buffer; keep a fixed-size buffer so the component stays
  unmanaged and cheap to snapshot.
- Pathfinder and funnel scratch buffers are per-call working memory and do not
  need snapshotting, but must not carry state between calls.

### License

Klotho is Apache-2.0. Keep the license header on copied files, add a `NOTICE`
entry crediting xpTURN Klotho, and mark modified files as changed.

## Implementation Status

| Phase | Status | Current result |
| --- | --- | --- |
| 0. Authoring and source geometry | Implemented | `LevelCollider` exports `Walkable`, `ObstacleOnly`, or `Excluded`; `Space.NavBuilder` emits deterministic fixed-point box triangles |
| 1. Fixed-point runtime core | Implemented | `NavMesh`, `NavMeshQuery`, `NavPathfinder`, `NavFunnel` in `GameCore/Navigation`; tested on handcrafted meshes |
| 2. Fixed-point mesh build pipeline | Implemented | `NavMeshBuilder` snaps, welds, removes degenerates, splits T-junctions, canonicalizes, and builds adjacency, portals, bounds, and the grid |
| 3. DotRecast offline bake | Implemented | `NavMeshBaker` rasterizes collider triangles with pinned `DotRecast.Recast` 2026.3.1 and feeds the detail mesh to `NavMeshBuilder` |
| 4. Level export and runtime loading | Implemented | Version 4 stores bake settings and the navmesh; each world installs its own `NavigationRes` |
| 5. Navigation agent and movement | Implemented | Player and navigation write `CharacterMoveIntent`; one `NavCharacter` chases the nearest player through `CharacterMoverSystem` |
| 6. Rollback and multiplayer verification | Implemented | Rollback and full sync reproduce every per-tick world hash; snapshot cost, corridor length, allocations, and burst timing are measured |
| 7. Debug visualization | Implemented | Read-only `NavDebugDraw` in GameCore; the client's F5 `NavDebugView` draws the mesh, sources, clearance, and agent routes |
| 8. Dynamic navigation | In progress | Switchable zones implemented: `NavZone` authoring, format version 5, `NavZoneState` and `NavZoneApplySystem`, zone-change re-planning. ORCA avoidance not started |

### Current Implementation

Phase 0 is complete:

- `NavContribution` has stable serialized values: `Walkable = 0`,
  `ObstacleOnly = 1`, and `Excluded = 2`.
- `Client/level_authoring/NavigationComponent.cs` exposes the classification in
  the inspector and passes it through `EntityRecipe` and
  `EntityPlacementBuilder`. (Originally `LevelCollider`, `ColliderRecipe` and
  `StaticBoxBuilder`; replaced in format version 6.)
- The placement's `Navigation` component stores the classification, and
  `LevelData.NavigationSources` derives a `NavSourceBox` per static geometry
  placement. `LevelDataCodec` (version 3 onwards) writes it
  into the level payload, validates it on read, and therefore includes it in
  the existing whole-level content hash and connection key.
- `Client/maps/level_pipeline_test.tscn` marks the ground as `Walkable` and
  the test box as `ObstacleOnly`; its committed `.level.bytes` file has been
  regenerated with Godot 4.7.2.
- `NavBuilder/Space.NavBuilder.csproj` is a plain .NET 8 build-time module. It
  has no Godot dependency and is not referenced by the client or server.
- `NavSourceTriangulator` (originally `StaticBoxTriangulator`) sorts boxes by ordinal source path, rejects invalid
  or duplicate input, omits `Excluded` boxes, applies the same quaternion
  normalization as physics, and emits eight Fixed64 world vertices and twelve
  consistently wound triangles per included box.
- Each triangle retains its source contribution so the Recast adapter can
  distinguish walkable candidates from obstacle-only geometry.
- Tests cover serialization, hashes, sample export data, transforms, all face
  windings, rotated and non-unit rotations, ordering, exclusion, duplicate
  paths, and invalid geometry. The full GameCore test suite passes.

The current triangle soup is only the source geometry for the offline bake.
It is not a navmesh and is not loaded by the simulation.

Phase 1 is complete:

- `NavMesh` stores exact-size vertices, immutable `NavTriangle` geometry, and
  the spatial grid, and validates index ranges, reciprocal adjacency, and grid
  coverage on construction. The runtime-mutable per-triangle attributes
  (`IsBlocked`, `AreaMask`, `CostMultiplier`) live in a separate
  `NavTriangleArea` span, so the derived-state rule from "Rollback" applies to
  one clearly separated array.
- `NavTriangle.Create` derives the portal orientation and centroid from the
  vertices, so stored fields cannot disagree with geometry and Phase 4 need
  not serialize them.
- `NavMeshQuery` ports triangle lookup (plain, height-aware, passable, and the
  endpoint tie-break), height sampling, and nearest-point/projection search.
  Lookups clamp points on the far bounds edge into the last grid cell.
  Klotho's `MoveAlongSurface` and `Raycast` are not ported; movement goes
  through `CharacterMover`.
- `NavPathfinder` ports A* with Klotho's start exemption and forbidden-region
  escape rule. It writes into a caller-owned corridor span (keeping the start
  side when truncated) and returns a `NavPathStatus` instead of logging and
  diagnostic counters. The open set breaks equal f-scores by ascending triangle
  index. Klotho's partial-path-on-exhaustion mode is not ported.
- `NavFunnel` ports the simple stupid funnel for full paths (`FindPath`) and
  steering corners (`FindCorners`) into caller-owned spans. Apex comparisons
  use `FVector2.Equals`, because Space's `==` is approximate.
- `NavConfig` replaces `FPNavTuning` with `MaxIterations` and `MaxPortals`;
  corridor and waypoint capacities are the caller's buffer lengths.
- Epsilons are built from integer ratios; the runtime performs no float math.
  Scratch buffers are per instance and reset per call; no static state is
  mutable.
- Derived files carry a Klotho attribution header, and the root `NOTICE`
  credits xpTURN Klotho (Apache-2.0).
- Tests cover lookup, height sampling, floor selection, the endpoint
  tie-break, passable and nearest-point search, mesh validation, corridor
  shapes, every failure status, the escape rule, truncation, the iteration
  budget, cost steering, exact corners on both sides of an obstacle, winding
  independence, output capacities, and equal-cost tie-breaking (pinned; it
  fails if the heap's tie order is reversed). They pass in Debug and Release.

Phase 2 is complete:

- `Space.NavBuilder/NavMeshBuilder.Build(vertices, indices, areas, cellSize)`
  ports Klotho's full-build path and returns the `NavMesh` plus a
  `NavMeshBuildReport` (snapped and welded vertices, orientation flips,
  degenerate and duplicate triangles, T-junction splits, non-manifold edges)
  for the Phase 3 tool to log or gate on. Klotho's rebake pooling, resumable
  build task, incremental patch, and conforming fast path are not ported.
- `NavSnapGrid` quantizes X/Z to 1/1024 world units (Klotho's resolution) by
  flooring Space's 31-fractional-bit raw values. Y is never snapped, and the
  weld key includes Y, so stacked floors do not merge.
- Degenerate removal uses Klotho's |XZ area| < 0.0001 threshold, before and
  after T-junction splitting, and also drops vertices that only degenerate
  triangles used.
- T-junction detection is exact integer arithmetic on the snap grid (128-bit
  products) instead of Klotho's doubles, so it is deterministic on every
  platform. A vertex splits an edge when it is within 2 grid steps (about
  0.002 world units, Klotho's tolerance) of the edge's interior and within 0.5
  of its height. Candidates come from a binary search over X-sorted vertices
  instead of Klotho's vertex grid index. Unreferenced vertices can no longer
  split edges. Non-convergence or runaway growth throws.
- Canonicalization sorts vertices by (X, Z, Y), winds every triangle
  counter-clockwise in XZ starting at its lowest vertex, sorts triangles, and
  removes repeats (lowest area wins). It runs before splitting, which makes the
  splits independent of input order, and again at the end. Vertex numbering,
  unused vertices, triangle order, rotation, and winding therefore cannot
  change the output.
- Adjacency pairs sorted edge records. An edge used by three or more triangles
  stays a boundary on all of them and is counted in the report, where Klotho
  paired such edges in arbitrary order. Portals and centroids come from
  `NavTriangle.Create`. Area index `a` becomes `AreaMask = 1 << a`.
- The grid covers the XZ bounds exactly, with a correction for truncating
  fixed-point division, and each triangle is bucketed by its XZ bounding box.
- Input is validated (index and area ranges, and |X|, |Z| <= 2^6 so every
  predicate stays exact), and an empty result throws.
- Tests cover off-grid snapping with exact Y, rebuild idempotence, weld
  healing adjacency, stacked floors, T-junction splits (on the edge, within
  tolerance, and on another floor), degenerate and weld-collapsed triangles,
  orientation flips, byte-identical output for a reordered, rotated,
  re-wound, and re-numbered input with unused and duplicate vertices,
  reciprocal adjacency with swapped portals, centroid lookup through the
  grid, non-manifold edges, repeats, area masks, invalid input, and grid
  coverage for a cell size that does not divide the bounds. The byte-identity
  test fails if either vertex sorting or triangle rotation is removed.

Phase 3 is complete:

- `Space.NavBuilder` pins `DotRecast.Recast` 2026.3.1 (zlib), which brings
  only `DotRecast.Core`. Detour and Crowd are not referenced. The Debug editor
  client references `Space.NavBuilder`; the exported runtime client, server,
  and GameCore do not. Exported client and server outputs contain no DotRecast
  assembly.
- `NavBakeSettings` (in GameCore, so Phase 4 can store it in `LevelData`)
  holds voxel size and height, agent radius, height, climb and slope, region
  sizes, edge length and error, detail sampling, and the lookup cell size, all
  in Fixed64. `Default` matches the player capsule (radius 0.5, height 2),
  its 45 degree standable slope, and a 0.4 climb, with 0.25 x 0.1 voxels.
- `NavMeshBaker.Bake(boxes | soup, settings)` converts Fixed64 to float only
  inside the adapter. `Walkable` triangles get Recast's walkable area where
  their slope allows; `ObstacleOnly` triangles are rasterized as solid,
  non-walkable spans, so they block and erode clearance but are never stood
  on. `Excluded` boxes were already dropped by the triangulator.
- It runs one non-tiled watershed build (`RcBuilder.Build` on a heightfield
  rasterized by the adapter, so no input-geometry provider is needed),
  flattens `RcPolyMeshDetail` with each detail triangle inheriting its
  polygon's `RcPolyMesh` area (Recast's walkable area maps to navigation area
  0; any other area throws), converts vertices back to Fixed64 exactly, and
  hands the soup to `NavMeshBuilder`, which snaps, welds and canonicalizes it.
- The bake returns a `NavMeshBakeReport` (source triangles, walkable
  candidates, Recast polygons and detail triangles, and the build report). No
  geometry, no walkable candidate, or no Recast polygon throws.
- Recast places the surface up to two voxel heights (0.2 by default) above the
  collider it came from: spans round up and detail vertices are lifted by one
  voxel. The runtime only uses mesh height to choose between floors, and
  `CharacterMover` owns the real Y, so the lift is documented, not corrected.
- Tests on the exported sample level check that the ground is walkable, the
  test box's footprint and a 0.1 band around it are blocked while 1.0 away is
  open, nothing above ground level is walkable, and a path from one side of
  the box to the other bends around it. Separate scenes show that agent
  radius, height (space under a slab), climb (a 0.3 step), and maximum slope
  (a 30 degree ramp) each change the result. Repeated bakes, including with
  the boxes in reverse order, are byte-identical; output vertices are on the
  snap grid; a bake without walkable geometry fails; and GameCore references
  neither `Space.NavBuilder` nor DotRecast.

Phase 4 is complete:

- `LevelData.Navigation` is an optional `LevelNavigation(NavBakeSettings,
  NavMeshData)`. `NavMeshData` holds an immutable template mesh; worlds never
  use it directly but call `CreateMesh()`, which shares geometry and the grid
  and copies the areas. The offline client runs client and server in one
  process from the same `LevelFile`, so one world blocking a triangle must not
  reach the other.
- Level format version 4 appends a navigation section: a presence flag, the
  bake settings, vertices, triangles (vertex and neighbor indices, area mask,
  cost, blocked flag), bounds, and the grid. Portal orientation and centroids
  are not stored; `NavTriangle.Create` derives them again on load.
  Version 3 files are rejected.
- Decoding checks every count against a limit (1M vertices, 1M triangles, 16M
  grid entries, `NavMesh.MaxGridCells`) and against the bytes actually left
  before allocating. It also checks vertex indices before building triangles,
  coordinates within `NavMesh.MaxCoordinate` (2^6, now shared with the
  builder), area data (non-zero mask, positive cost, a 0/1 blocked byte),
  and bake settings. The resulting `NavMesh` constructor validates adjacency
  reciprocity, grid ranges, grid coverage, repeated vertices, and (new) zero XZ
  area. Its `ArgumentException`s become `InvalidDataException`s, and all of
  this happens in `LevelFile.Read`, before any world exists.
- `NavMeshBaker.BakeLevel(level, settings, out report)` attaches navigation to
  a `LevelData`; levels without a `Walkable` box get none.
  `LevelExporter.Export` calls it between `Extract` and serialization, so the
  existing decode check and atomic replacement cover the navmesh too, and the
  summary line reports the triangle count, Recast polygons, and non-manifold
  edges. `Client/Space.csproj` references `Space.NavBuilder` only in the
  `Debug` configuration, which is also the only one defining `TOOLS`; the bake
  call sits under `#if TOOLS`. The editor build output contains DotRecast; the
  `ExportRelease` output does not, and the server output has neither.
- `Client/maps/level_pipeline_test.level.bytes` was regenerated through the
  headless exporter with Godot 4.7.2 (45 triangles from 23 Recast polygons,
  12628 bytes). A second export produced the same SHA-256.
- `GameWorldSetup.CreateAndInitialize` installs `NavigationRes` with
  `Systems.SetResource` (the non-snapshotted registry, like `CharacterRes`)
  before `W.Initialize` and `Systems.Initialize`, on client and server alike.
  It holds the world's own `NavMesh`, `NavMeshQuery`, `NavPathfinder`,
  `NavFunnel`, and `NavConfig`, or no mesh for a level without navigation.
- The whole-file SHA-256 connection key already covers the section; no
  separate fingerprint was added.
- Tests cover byte-identical round trips with and without navigation; that
  the committed sample carries exactly what a fresh bake of its colliders
  produces (so a stale export fails the build); that a navmesh-only difference
  changes the connection key; that corrupting any byte of the navigation
  section (with the payload hash recomputed) either decodes to a level that
  round-trips or throws `InvalidDataException`, never another exception; that
  oversized counts fail as truncated before allocation; and that two worlds
  loaded from one level get equal but independent meshes, with the level data
  untouched. The server was started once against the regenerated file and
  initialized.

Phase 5 is complete:

- `PlayerMoverSystem` is split. `PlayerIntentSystem` turns input into a
  `CharacterMoveIntent` (horizontal velocity in world XZ, and a jump flag set
  only on a fresh press); `CharacterMoverSystem` executes the intent of every
  entity with `Transform`, `Mover`, and `CharacterMoveIntent`, with unchanged
  grounding, jump, collision, and push behavior. The per-player query filter
  group moved from `PlayerInfo` into `Mover.FilterGroup`.
- `NavAgent` is an unmanaged component: speed, arrival radius, destination snap
  distance, repath interval and area mask; the requested destination; status
  (`Idle`, `Moving`, `Arrived`, `Failed`) and the last `NavPathStatus`; the
  destination the corridor was planned for; the path target; the current
  triangle; the next repath tick; and corridor progress plus a 64-entry
  `[InlineArray]` corridor (384 bytes per agent). Nothing about a path
  lives in `NavigationRes`, which still only holds the mesh and per-call scratch.
- `NavAgentSystem` locates the agent's feet on the mesh (height-aware, or the
  nearest mesh point when it stands off the mesh), updates its corridor index,
  and plans again when the destination changed bit-for-bit, when the agent left
  its corridor, when a truncated corridor ran out, or every
  `RepathIntervalTicks` (30). Planning snaps an off-mesh destination onto
  passable ground within the snap distance, runs A*, and aims at the
  snapped destination, or at the last triangle's centroid when truncated.
  Steering heads straight for the first `FindCorners` corner, slowing only so
  that one tick never carries it past that corner. The agent stops within its
  arrival radius of the target. A failed plan stands still and retries on the
  interval or when the destination changes. Planning hands the triangles it
  resolved to a new `NavPathfinder.FindPath` overload, so A* does not look
  them up again.
- `NavMeshQuery.ClosestPoint` no longer returns the input point when a
  neighboring cell's triangle accepts it within the point-in-triangle
  tolerance; it returns that triangle's nearest edge point. Such a point lay in
  a cell that did not list the triangle, or past the mesh bounds, so
  `FindTriangle` missed it again. An agent that drifted a hair past the z=-3.25
  mesh edge in front of the sample box (also a grid line) then failed every
  re-plan with `StartOffMesh` and froze. Every `ClosestPoint` and
  `ClosestPassablePoint` result is now found by the matching lookup; the baked
  data is unchanged.
- `NavCharacter` (entity type 5, view `NavCharacter` rendered with the dummy
  scene) has the player's capsule and no Body or Shape, so projectiles and
  characters pass through it, as they do through players.
  `SpawnNavCharacterSystem` spawns one at (0, 1.5, -11), behind the sample box,
  on levels that ship a navmesh. `NavChaseSystem` points it at the nearest
  player's feet (ties to the lower input channel) and only moves the
  destination once the player is 1 unit away from it. Its snap distance is 4
  (the lookup cell size, which the nearest-point search is guaranteed to
  reach), so a player standing on top of the sample box is approached from the
  nearest ground instead of leaving the character frozen. A player deeper
  inside a larger obstacle still leaves it standing still. Tuning is in
  `NavCharacterRes`.
- System order: intent writers (`PlayerIntentSystem`, `NavChaseSystem`,
  `NavAgentSystem`) run after `DeathSystem` and before `CharacterMoverSystem`;
  every later system moved down by three.
- Tests on the committed sample level check that the character walks around
  the test box to the far side and arrives grounded, without its capsule
  entering the box; that a wall missing from the navmesh still blocks it; that
  it chases a player around the box and stops within the arrival radius; that
  it approaches a player standing in the middle of the box top and follows
  again after the player jumps off; that it can plan from just outside the
  mesh edge on a grid line; that nearest-point results just inside and outside
  every edge of the sample mesh are found by the triangle lookup; that
  it stands still without players; that a destination change re-plans on the
  next tick and an interval re-plan happens on schedule; that a destination off
  the ground fails in place with `EndOffMesh`; that two
  worlds stepped alternately in one process keep byte-identical `NavAgent`
  and `Transform` state; that `NavAgent` round-trips through a world snapshot;
  and that a level without navigation spawns no character. The full GameCore
  suite passes in Debug and Release, the client and server build, and the
  server started against the sample level.
- Known mover behavior, not caused by navigation: a capsule pressed straight
  into the unbaked test wall stops, then after about a second slides sideways
  along it at about 2 units per second against its intent. The player does
  the same with plain forward input, so it belongs to `CharacterMover` and is
  left for a physics follow-up.

Phase 6 is complete:

- `NavRollbackTests` (GameCore.Tests) runs two players and the chasing
  character on the sample level. The remote player walks toward the character
  and away again, so which player it chases, and therefore its destination and
  corridor, depends on that input.
- Snapshot restore: every `NavAgent` field, set to distinct non-zero values
  including all 64 corridor slots, round-trips byte-exactly.
- Rollback: an uninterrupted `ForwardOnly` run is compared with an
  `AutomaticRollbacks` run whose remote input arrives 12 ticks late. The late
  run re-simulates more ticks than it plays, its predictions planned
  destinations that the rollback replaced, and its final world-snapshot hash
  matches the uninterrupted run on all 240 ticks.
- Full sync: a world is synchronized mid-path into another world type the way
  `Client.HandleFullSync` does it (`HardReset` to the server tick, then
  `ReadFullSync`); both then advance with identical input and keep equal
  snapshot hashes for 240 ticks.
- Both tests fail when navigation keeps state outside the snapshot (checked by
  temporarily forcing re-plans from a static call counter).
- Connection key: a loopback LiteNetLib handshake between the real
  `LiteNetLibRemoteClientListener` and `LiteNetLibServerConnection` succeeds
  with equal level keys and is refused when the client's level differs only in
  its navmesh.
- Snapshot cost: a navigation character adds 533 bytes to the world snapshot
  (`NavAgent` is 384 of them). The sample world is 13,285 bytes, so the
  640,000-byte rollback frame holds about 1,175 characters; the test requires
  room for at least 256.
- Corridor capacity: the longest corridor between any two triangles of the
  sample mesh is 23 of its 45 triangles, against a 64-entry buffer. Longer
  routes are truncated and re-planned from their end (Phase 5), so 64 stays;
  larger levels should re-run the measurement.
- Allocations: with the character planning every tick for 480 ticks, the run
  allocates exactly as much as the same run without a navmesh, so navigation
  adds zero bytes. The test fails on a single 40-byte allocation per plan.
- Benchmark: `GameCore.Benchmarks` also runs `BenchNavigationBudgets`: 16
  characters that re-plan every tick while both players move, timed with and
  without the navmesh. Rollback bursts are forced by withholding the remote
  player's input and delivering it late; the benchmark checks that the whole
  window was re-simulated. Release results on the development machine, as the
  navigation share over the same session without navigation:

  | Case | Navigation share | Budget |
  | --- | --- | --- |
  | Regular tick | 0.60 ms | 1.0 ms |
  | 30-tick rollback burst | 4.2 ms | 8.0 ms |
  | 115-tick rollback burst | 5.6 ms | 33.3 ms |

  This is the worst case; the default interval re-plans every 30 ticks.
  `--enforce` fails the benchmark run when a budget is exceeded.
- Found, not fixed:
  - StaticEcs writes world resources in the enumeration order of a
    `Dictionary<Guid, …>` that outlives `World.Destroy`. After a world type is
    destroyed and created again in one process, its resources are written in
    another order, so equal states produce different snapshot bytes. Loading
    is by GUID and unaffected, but byte hashes across worlds with different
    histories disagree. The tests give every hash-compared world its own type.
    The fix belongs in the `static-ecs` fork: write resources sorted by GUID
    without allocating.
  - The session tick loop allocates about 150 bytes per tick, and about 300
    with inputs set every tick, with or without navigation. The physics
    harness, which calls `Systems.Update` directly, allocates nothing.

Phase 7 is complete:

- `Core<TWorld>.NavDebugDraw` (GameCore, next to `PhysicsDebugDraw`) walks the
  world's `NavigationRes.Mesh`, the level's `NavigationSources`, and every
  `NavAgent`, and reports them to an `INavDebugDraw` (triangles, segments,
  points, and one per-agent status callback) with semantic `NavDebugColor`s.
  `NavDebugDrawFlags` selects triangles, edges, sources, and agents; an agent
  index selects one agent.
- It is read-only. It reads ECS state with `Read<T>` only, never writes the mesh
  or its areas, and plans nothing: it owns a `NavMeshQuery`, a `NavFunnel`, and
  a waypoint buffer bound to the world's mesh, so the world's per-call scratch
  is never touched either. `NavAgentSystem.Feet` and `Locate` are shared as
  internal helpers, so the overlay locates agents exactly like the system.
- Mesh: every triangle filled by area (walkable, other area, cost multiplier
  not one, blocked); interior edges once, and boundary edges, where no corridor
  can cross, highlighted.
- Why the sample obstacle is blocked: each collider is outlined by its
  `NavContribution` (walkable, obstacle-only, excluded). An obstacle-only box
  also gets its footprint grown by the baked agent radius, drawn flat at its
  lowest corner: the ground Recast eroded (corners square where the real
  erosion is rounded). On the sample, the mesh boundary sits just outside that
  ring, and the label states the bake settings and that obstacle-only colliders
  are solid and never walkable.
- Per agent: the corridor ahead of it, the triangle it was located in, the
  string-pulled path from where it stands to its path target with funnel
  corners, the steering segment to the corner `NavAgentSystem` steers at (the
  same `FindCorners` call), the requested destination and the path target, or
  a line to the destination of a failed plan. The label lists status, path
  status, position, destination, target, triangle, corridor progress, and
  ticks until the next re-plan.
- `Client/setup/NavDebugView.cs` renders it for the predicted client world:
  F5 toggles, F6 cycles layers (everything, agents only, mesh only), F7 cycles
  the shown agent (all, then each). Fills are translucent and depth-tested,
  lines are drawn over everything, and the label sits top right, clear of the
  F3 physics overlay. The server does not reference it.
- Tests on the sample level check that every shipped triangle is drawn as the
  one walkable area, the box is outlined as obstacle-only and the ground as
  walkable, the clearance ring is the footprint grown by 0.5 on the ground, no
  walkable triangle lies inside it, and boundary edges ring it; that a moving
  agent's drawn corridor, current triangle, destination, target, corners, and
  steering match its state and the drawn route ends at the target without
  crossing the box; that a failed plan draws a line to its unreachable
  destination; that a level without navigation draws only its colliders; and
  that a world drawn every tick for 240 ticks of a chase keeps the same
  world-snapshot hash as an undrawn one on every tick, with runtime and
  shipped areas byte-identical. The last test fails at tick 0 if the overlay
  writes one `NavAgent` field. The GameCore suite passes in Debug and Release;
  client and server build. The overlay was not yet checked in a running client.

Phase 8, steps 1 and 2 (switchable zones), are complete:

- Authoring: `Client/level_authoring/NavZone.cs` is a box marker with a
  `ZoneId` (the node name when empty) and a `Size`; it draws blue lines in the
  editor. `LevelExporter` collects it into `LevelData.NavZones`
  (`NavZoneVolume`: id, fixed-point transform, half-extents). The sample map
  has one zone, `gate`, covering x in [2, 6], z in [-9, -3] beside the test
  box, so routes past the box's right side run through it.
- Bake: each zone marks the eroded walkable surface inside its volume with its
  own Recast area (zone `i` is area `1 + i`, at most 62 zones) through
  `RcBuilder`'s convex-volume hook, handed over by a geometry-provider adapter
  that serves only the volumes. The volume is the XZ convex hull of the box's
  corners from its lowest to its highest corner, exact for boxes rotated only
  about Y. Recast never merges regions of different areas, so no polygon and
  therefore no navmesh triangle straddles a zone's boundary. The boundary
  follows the volume within Recast's contour error (cell-center marking plus
  `EdgeMaxError` voxels, about 0.6 units by default). `NavMeshBuilder` carries
  a zone index per triangle through snapping, T-junction splits and
  canonicalization (packed with the area into one sort key) and reports it;
  zone-free inputs produce the same bytes as before. A zone that covers no
  walkable triangle, a duplicate id, or more than 62 zones fail the bake.
  Overlapping zones are not detected; the later id owns the overlap.
- Format version 5 stores the zone volumes after the static boxes (ordinal by
  id; version 6 folds the static boxes into the placements) and appends the zone table (`NavZoneData`: id, strictly ascending
  triangles) to the navigation section. Decoding checks counts against the
  bytes left, and `LevelNavigation` rejects out-of-range, shared, or unordered
  entries. A baked navmesh must carry exactly the level's zone ids in order,
  on write and on read. The sample level was re-exported with Godot 4.7.2: 58
  triangles from 29 Recast polygons, 13,463 bytes.
- Runtime state lives in the ECS: `LevelLoader` spawns one entity per zone
  with `NavZoneState { Zone, Blocked, CostMultiplier }`, open at cost one.
  Gameplay changes the component; `NavigationRes.FindZone(id)` gives the
  index. Nothing in the game toggles a zone yet.
- `NavZoneApplySystem` (order 4, after the intent writers and before
  `NavAgentSystem`) rebuilds every zone triangle's area from the baked value
  and the zone states on every tick, never only on change, so rollback and
  full sync cannot leave stale flags. Several states for one zone combine
  order-independently (any blocked blocks, the highest cost wins, zero or
  below counts as one); a zone without an entity is open. It also computes
  `NavigationRes.ZoneSignature`, a digest of every zone's state; later systems
  moved down by one.
- Re-planning: `NavAgent.PlannedZoneSignature` (8 bytes; `NavAgent` is now
  392 bytes, a character 541 snapshot bytes, room for 1,158 per frame) holds
  the signature the agent planned or failed under, and any difference re-plans
  on the same tick. Every agent re-plans on any zone change, not only those
  whose corridor crosses it, because an opened zone can shorten any route; zone
  changes are rare. A failed agent retries as soon as a zone changes.
- `NavPathfinder` now exempts the start triangle from being blocked, with the
  same escape rule as the area mask: the search may cross blocked ground only
  while it has not left the blocked region it started in. An agent standing in
  a zone that closes walks out instead of failing with `EndpointBlocked`,
  which remains the result for a blocked destination.
- The F5 overlay outlines each zone volume, blue when open and red when
  blocked; blocked triangles were already filled red.
- Tests: one open state per zone; closing writes exactly the zone's
  triangles, a cost multiplies only them, and reopening restores the baked
  area bytes and the signature; states combine regardless of order; closing
  re-plans a corridor through the zone around it on the same tick and
  reopening restores a route through it; an agent reaches its destination with
  the zone closed without ever standing in it; an agent inside a zone that
  closes walks out; a rollback run with late remote input and a test system
  that closes the zone from inside the tick re-simulates both toggles and
  matches the uninterrupted run's world hash, mesh areas, and corridor on all
  240 ticks (and fails if agents ignore the signature); a full sync with a
  closed zone into a fresh world re-derives identical areas on the next tick;
  bakes split the sample mesh along the volume within the contour tolerance
  and are byte-identical whatever the zone order; invalid zones fail the bake;
  zones survive T-junction splits; the codec rejects mismatched, duplicate,
  shared, or unordered zones; zone volumes round-trip without navigation; and
  the overlay outlines the zone by state. Zero steady-state allocation still
  holds with the apply system running every tick. The GameCore suite (239
  tests) passes in Debug and Release; client, server, and benchmarks build,
  and the server started against the re-exported level. Neither the overlay
  nor the `NavZone` node was checked in a running editor or client.
- Found and fixed upstream: `FP.Three` (`Fixed32` and `Fixed64`) returned 2
  (nilpunch/fixed-point#3). With the submodule updated, players spawn at
  x = channel * 3 as intended; the suite passes unchanged.

## Implementation Steps

### Phase 0: Authoring and Source Geometry

Goal: give the offline builder authoritative geometry that exactly matches
the simulation's static collision.

Steps:

1. Add `Walkable`, `ObstacleOnly`, and `Excluded` navigation contribution to
   `LevelCollider` and `StaticBox`.
2. Preserve it in the versioned level format and whole-level hash.
3. Create a Godot-independent `Space.NavBuilder` module.
4. Sort and validate `StaticBox` input, then triangulate each included oriented
   box from its fixed-point transform and half-extents.
5. Test winding, rotation, ordering, exclusion, invalid geometry, codec
   round trips, and headless sample export.

Acceptance criteria:

- Navigation source geometry uses the same quantized transform and dimensions
  as the collider created by `LevelLoader`.
- Repeated input produces identical triangle ordering and values.
- No Godot physics node or visual `MeshInstance3D` is used as bake input.
- The sample level records a walkable ground and obstacle-only test box.

Status: implemented.

### Phase 1: Fixed-Point Runtime Core

Goal: deterministic queries, A*, and funneling over handcrafted navmeshes
before introducing an offline baker.

Steps:

1. Add immutable mesh and triangle types under `GameCore/Navigation`.
2. Port the required Klotho query operations: triangle lookup, height sampling,
   nearest-point search, and endpoint ownership.
3. Port the deterministic binary heap and A* corridor search with explicit
   triangle-index tie-breaking.
4. Port funnel waypoint generation, replacing Klotho vector operations with
   Space Fixed64 operations and exact equality where topology requires it.
5. Replace `FPNavTuning` with a small Space-owned configuration containing
   only the limits used by query, pathfinding, and funneling.
6. Port the corresponding Klotho tests using handcrafted meshes.

Acceptance criteria:

- Query, A*, and funnel tests pass without creating an ECS world.
- Runtime navigation performs no float math and retains no mutable static
  scratch state.
- Equal-cost paths resolve identically across repeated Debug and Release runs.

Status: implemented.

### Phase 2: Fixed-Point Mesh Build Pipeline

Goal: turn canonical fixed-point triangles into the immutable runtime mesh.

Steps:

1. Extract Klotho's full-build path without rebake pools or incremental paths.
2. Port predicate-grid quantization, vertex welding, degenerate removal, and
   T-junction splitting for Space's 31 fractional bits.
3. Build triangle precomputation, adjacency, portal orientation, bounds, and
   the spatial lookup grid.
4. Use exact-sized arrays and ordinary build-time collections instead of
   Klotho's runtime rebake pools.
5. Port snap, adjacency, portal, and T-junction tests.

Acceptance criteria:

- Shared edges produce reciprocal adjacency and correctly oriented portals.
- Degenerate and collapsed triangles cannot reach the runtime mesh.
- Canonically equivalent triangle inputs produce byte-identical mesh data.

Status: implemented.

### Phase 3: DotRecast Offline Bake

Goal: derive an agent-clear walkable surface from the fixed-point collider
triangle soup.

Steps:

1. Pin `DotRecast.Recast` in `Space.NavBuilder`; do not add Detour or Crowd.
2. Convert fixed world vertices to float only at the Recast adapter seam.
3. Map `Walkable` triangles to slope-filtered candidates and
   `ObstacleOnly` triangles to non-walkable rasterized geometry.
4. Run a single non-tiled `RcBuilder` bake with explicit cell size, cell
   height, radius, height, climb, slope, region, contour, and detail settings.
5. Extract `RcPolyMeshDetail`, inherit area information from `RcPolyMesh`,
   convert vertices back to Fixed64, and canonicalize the triangles.
6. Feed the canonical result into the Phase 2 fixed-point mesh builder.

Acceptance criteria:

- The sample ground is walkable, the test box footprint is blocked, and its
  top is not walkable.
- Agent radius, height, climb, and maximum slope have tested effects.
- Repeated bakes with the pinned toolchain produce byte-identical output.
- DotRecast is absent from client and server runtime outputs.

Status: implemented.

### Phase 4: Level Export and Runtime Loading

Goal: ship the complete navmesh in the same level file used by physics.

Steps:

1. Add immutable `NavMeshData` and explicit bake settings to `LevelData`.
2. Add bounded, validated navmesh encoding to `LevelDataCodec` and bump the
   format version again.
3. Invoke `Space.NavBuilder` after `LevelExporter.Extract` and before atomic
   serialization and file replacement.
4. Regenerate committed level files through the headless exporter.
5. Install the decoded mesh as a non-snapshotted world resource before systems
   initialize on client and server.
6. Keep using the whole-level SHA-256 connection key; a separate navmesh
   fingerprint is redundant while both peers load identical level bytes.

Acceptance criteria:

- The navmesh survives serialize, deserialize, and reserialize byte-identically.
- Malformed counts, indices, portals, and grid ranges fail before allocation or
  world mutation.
- Changing any navmesh byte changes the existing level connection key.
- Client and server load identical immutable navmesh data without DotRecast.

Status: implemented.

### Phase 5: Navigation Agent and Movement

Goal: prove the complete path with one rollback-safe AI character.

Steps:

1. Split player intent generation from the shared `CharacterMover` execution
   so player and navigation can write the same `CharacterMoveIntent`.
2. Add an unmanaged `NavAgent` component containing destination, status,
   current triangle, repath tick, corridor progress, and a bounded corridor.
3. Add a dedicated AI character using `Transform`, `Mover`, `NavAgent`, and
   `CharacterMoveIntent`; do not convert the current kinematic dummy yet.
4. Snap start and destination to the mesh, find a corridor, generate corners,
   and steer toward the next corner.
5. Repath on destination changes, invalid paths, and deterministic simulation
   tick intervals.

Acceptance criteria:

- The AI reaches a destination around the sample obstacle.
- Movement uses `CharacterMover`, cannot cut through physics walls, and keeps
  existing grounding and collision behavior.
- No persistent corridor or progress state lives in unsnapshotted resources.

Status: implemented.

### Phase 6: Rollback and Multiplayer Verification

Goal: prove navigation remains deterministic through prediction, rollback,
full synchronization, and resimulation bursts.

Steps:

1. Verify snapshot restore preserves every `NavAgent` field exactly.
2. Roll back across path requests and destination changes, then compare
   canonical per-tick state hashes.
3. Full-sync the world into another world type and advance both identically.
4. Measure snapshot size for the selected corridor capacity and realistic
   agent counts.
5. Verify zero steady-state allocations and benchmark pathfinding during
   multi-tick resimulation bursts.

Acceptance criteria:

- Restored and uninterrupted simulations produce identical paths and state
  hashes on every tick.
- The client and server reject different navmesh data through the existing
  level connection key.
- Corridor capacity and snapshot costs have measured limits rather than
  inherited Klotho defaults.

Status: implemented.

### Phase 7: Debug Visualization

Goal: make bake and runtime failures inspectable in the client.

Steps:

1. Draw shipped navmesh triangles, boundaries, and area classifications.
2. Draw each selected agent's corridor, funnel corners, destination, current
   triangle, and path status.
3. Keep visualization client-only and read-only; it must never build or mutate
   navigation data.

Acceptance criteria:

- The overlay explains why the sample obstacle is blocked and which route an
  agent selected.
- Enabling the overlay does not change simulation state or hashes.

Status: implemented.

### Phase 8: Dynamic Navigation

Goal: add dynamic behavior only after static navigation is complete.

Steps:

1. Add snapshotted switchable zones for doors, bridges, and known topology
   changes; derive blocked/cost flags from ECS state before pathfinding.
   (Implemented.)
2. Repath agents whose corridor crosses a changed zone. (Implemented: every
   agent re-plans on any zone change.)
3. Port Klotho obstacle-boundary extraction and ORCA local avoidance for
   agents and moving obstacles.
4. Continue to use `CharacterMover` collision as the final authority.

Acceptance criteria:

- Rolling back across a zone toggle restores identical blocked flags and paths.
- Two agents can pass head-on without changing the navmesh topology.
- DotRecast and runtime rebaking remain outside the simulation.

Status: steps 1 and 2 implemented; the rollback criterion is met. Steps 3 and
4 and the head-on criterion are open.
