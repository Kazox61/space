# Navigation Assessment

> Question: how should Space handle navmesh-based navigation, and can we reuse
> code from `Klotho/`, `box3d/`, or [DotRecast](https://github.com/ikpil/DotRecast)?
> Scope: deterministic runtime pathfinding for rollback. All claims verified
> against local source unless marked otherwise.

## Summary

Port the core of Klotho's deterministic navigation runtime into
`GameCore/Navigation`, and bake the navmesh with Godot's own
`NavigationRegion3D` (Recast under the hood) plus Klotho's Godot exporter.
Do not run DotRecast in the simulation. box3d has no navigation.

## Candidate Sources

| Source | License | Math | Usable for |
| --- | --- | --- | --- |
| Klotho `Runtime/Deterministic/Navigation/` | Apache-2.0 | `FP64` (Q32.32) | Runtime query, A*, funnel, serialization, Godot bake export |
| DotRecast | MIT (Recast/Detour: zlib) | `float` | Offline baking only |
| Godot `NavigationRegion3D` | MIT | `float` | Offline baking (already Recast-based) |
| box3d | MIT | `float` | Nothing; physics only |

### DotRecast

DotRecast is a C# port of Recast (voxel-based mesh building) and Detour
(runtime queries, crowd). All of it is `float`. Space is rollback-networked:
every peer must produce bit-identical paths, and float pathfinding, funnel
smoothing, and crowd steering do not guarantee that across platforms and JIT
modes. Converting Detour to fixed point is a large port that Klotho has
already done.

Baking determinism does not matter, because the baked result is shipped as
data. DotRecast would work as a baker, but Godot's baker already uses Recast
and is integrated with the editor, so it adds nothing today. Revisit DotRecast
only if a headless/server-side bake from level geometry is needed.

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

Plus the Godot editor exporter,
`com.xpturn.klotho/Godot~/Adapters/Editor/GodotFPNavMeshExporter.cs`, which
converts a baked `NavigationRegion3D` into the binary format.

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

### Skip for now

- ORCA avoidance (`FPNavAvoidance.cs`, 1012 lines, plus obstacle extraction):
  add when agent crowds jam.
- Runtime rebake, placement, constrained Delaunay, abstract graph: the
  majority of the 23k lines, needed only for RTS-style building placement.

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
handedness. Use only the Godot exporter so the baked data matches Space's
coordinate system.

### Rollback

- The navmesh is immutable at runtime: store it as a non-snapshotted resource
  loaded identically on server and client.
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
   `Test/` to validate the fixed-point conversion.
2. Port the Godot exporter. Bake one level, load the `.bytes` on server and
   client.
3. Add a `NavAgent` component and a system that requests paths and drives
   `CharacterMover`.
4. Add a navmesh fingerprint to the connect handshake (Klotho:
   `INavFingerprintSource.cs`), alongside the build fingerprint proposed in
   `klotho-workflow-comparison.md` #9.
5. Later: ORCA avoidance, and a navmesh debug overlay
   (`Klotho/Docs/NavMeshVisualizer.Godot.md`).
