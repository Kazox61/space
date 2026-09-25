# Navigation Assessment

> Question: how should Space handle navmesh-based navigation, and can we reuse
> code from `Klotho/`, `box3d/`, or [DotRecast](https://github.com/ikpil/DotRecast)?
> Scope: deterministic runtime pathfinding for rollback. All claims verified
> against local source unless marked otherwise.

## Summary

Port the core of Klotho's deterministic navigation runtime into
`GameCore/Navigation`. Build the navmesh with our own offline builder:
DotRecast bakes the walkable surface from the map's static colliders, and a
port of Klotho's build pipeline turns it into a fixed-point navmesh stored in
the level file (see `level-pipeline.md`, Phase 5). Godot's
`NavigationRegion3D` bake is not used. Do not run DotRecast in the simulation.
box3d has no navigation.

## Candidate Sources

| Source | License | Math | Usable for |
| --- | --- | --- | --- |
| Klotho `Runtime/Deterministic/Navigation/` | Apache-2.0 | `FP64` (Q32.32) | Runtime query, A*, funnel; build pipeline (triangles → navmesh) |
| DotRecast | MIT (Recast/Detour: zlib) | `float` | Offline baking only (chosen baker) |
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

`Klotho/com.xpturn.klotho/Runtime/Deterministic/Navigation/` holds 68 files
(~23.4k lines): a complete fixed-point navmesh runtime, ORCA avoidance, and a
large runtime-rebake subsystem. Documentation: `Klotho/Docs/Navigation.md`,
`Klotho/Docs/Navigation.Rebake.md`, `Klotho/Docs/NavMeshVisualizer.Godot.md`.
Tests: `Klotho/Samples/Klotho.Runtime.Tests/Deterministic/Navigation/`.

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
  level sizes. `Transform.Position` is already `Fixed64.FVector3`, so no
  conversion is needed at the gameplay boundary.
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

## Suggested Order

1. Port mesh, triangle, query, pathfinder, heap, funnel, tuning, and a Space
   serializer into `GameCore/Navigation`. Port the matching Klotho tests into
   `tests/GameCore.Tests` to validate the fixed-point conversion.
2. Build the `NavBuilder` (DotRecast bake → Klotho build pipeline), run it
   from the level exporter (`level-pipeline.md`, Phase 5), and load the
   navmesh from the level file on server and client.
3. Add a `NavAgent` component and a system that requests paths and drives
   `CharacterMover`.
4. Add a navmesh fingerprint to the connect handshake (Klotho:
   `INavFingerprintSource.cs`), alongside the build fingerprint proposed in
   `klotho-workflow-comparison.md` #9.
5. Dynamic navigation: ORCA avoidance (A), then nav zones (B).
6. Navmesh debug overlay (`Klotho/Docs/NavMeshVisualizer.Godot.md`).
