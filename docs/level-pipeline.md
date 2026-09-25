# Level Pipeline: From Authored Maps to ECS Entities

## Purpose

This document is the roadmap for turning visually authored maps into
deterministic ECS entities with colliders, entity placements, and a navmesh
that the server and the client both load. It records the decisions made so far
and splits the work into phases with acceptance criteria.

Related documents:

- `physics-box3d-migration-assessment.md`: physics gaps (static meshes,
  snapshot cost)
- `physics-limits.md`: fixed-point limits the exporter must enforce
- `navigation-assessment.md`: navmesh runtime (Klotho port)
- `klotho-workflow-comparison.md`: build fingerprint handshake (#9)

## Summary

Today the only map is hand-written C#. `GameCore/Systems/SpawnSphereSystem.cs`
creates a static ground box, a test box and a sphere at hardcoded positions,
and `GameCore/Systems/SpawnDummySystem.cs` places dummies with a formula from
`DummyRes`. That is fine for a test arena but does not scale to real maps.

Decision: **maps are built in Godot.** Blockouts use Godot primitives
(`MeshInstance3D` with `BoxMesh` etc., or CSG) and Godot collision nodes.
Blender is needed only later, for final art assets. Godot collision shapes
(`BoxShape3D`, `SphereShape3D`, `CapsuleShape3D`) map one-to-one onto the
physics shapes GameCore supports, so no shape fitting is needed.

Target pipeline:

```text
Godot map scene (<map>.tscn)
  ├─ visuals                     MeshInstance3D / CSG now, Blender .glb pieces later
  ├─ StaticBody3D / Area3D       + CollisionShape3D (Box / Sphere / Capsule)
  └─ EntitySpawn markers         entity type + per-instance overrides
        │
        ▼  exporter: Godot editor tool, or headless Godot in CI
        │  (float → fixed happens only here)
        │
        ├─► NavBuilder (plain C#, no Godot): static colliders
        │     → DotRecast bake → Klotho build pipeline → Fixed64 navmesh
        ▼
<map>.level.bytes
  ├─ static colliders
  ├─ entity placements
  ├─ navmesh
  └─ version + content hash
        │
        ▼
LevelLoader (GameCore) on server and client ──► ECS entities

Visuals: the client instances <map>.tscn directly. Only entities (things
that change during play) go through ViewId and the view catalog.
```

The server never reads Godot files. It only loads `<map>.level.bytes`.

## Responsibilities

| Layer | Owns | Example for a crate |
| --- | --- | --- |
| Godot map scene | Where things are and which kind they are | `EntitySpawn` marker: type `Crate`, health override 200 |
| C# (GameCore) | What a kind is: components, collider, behaviour | `Crate : IEntityType` sets `Health`, `ViewId`; spawn fn adds body and box shape |
| Godot view | How it looks: model, animations, VFX | `Client/entity/crate.tscn`, entry in `Client/config/entity_view_catalog.tres` |
| Blender (later) | Final asset geometry | `crate.glb`, `building_a.glb` replacing primitive placeholders |

Rules for marker fields:

- Allowed: the entity type and small per-instance overrides (`health = 200`,
  `loot = ammo`, `patrol_group = 2`).
- Not allowed: type definitions or tuning ("every crate has 50 health, mass
  3"). That lives in C# so it is typed, reviewed and changed in one place.
- Not possible: animations or behaviour. Those belong to the view and to
  systems.

Reusable pieces (a building, a wall segment) are their own `.tscn` with their
collision nodes inside. Every instance in every map gets the correct collider.

## Conventions

| Convention | Rule |
| --- | --- |
| Solid static geometry | `StaticBody3D` with `CollisionShape3D` children |
| Trigger volumes | `Area3D` with `CollisionShape3D` children |
| Supported shapes | `BoxShape3D`, `SphereShape3D`, `CapsuleShape3D`; anything else is an export error until Phase 6 |
| Scale | Allowed on boxes; uniform only on spheres; no mirrored (negative) scale |
| Collision layer / material | Exported from the body's collision layer and a small metadata or script field (`friction`) |
| Navmesh | Built by the exporter from the static colliders; no Godot navigation nodes. Colliders that should not produce walkable surface (e.g. roofs) get `nav = blocked` metadata |
| Nav zones | `NavZone` box volume with an id (`door_1`); triangles inside are tagged at export and toggled at runtime (Phase 5, B) |
| Entities | `[Tool]` `EntitySpawn : Node3D` with a typed entity field and override fields; shows a preview of the entity's view in the editor |
| Blender assets (later) | Export `.glb`, wrap in a `.tscn` that adds Godot collision nodes. Do not use Godot's `-col` / `-colonly` import suffixes. `import/blender/enabled` (`Client/project.godot:40`) may be enabled to import `.blend` directly |

## Phases

### Phase 0: Blockout Prototype

Goal: confirm the Godot workflow with the least possible code before
committing to formats.

Steps:

1. Build a blockout scene in Godot with primitives: ground, two box
   buildings, a ramp, three crates, two player spawns.
2. Add a `[Tool]` `EntitySpawn` marker node with a typed entity field.
3. A throwaway editor tool that walks the scene and prints the colliders
   (type, size, world transform) and entity placements it found.
4. Time the loop "move a building 2 m, then play it" and note friction points.

Acceptance criteria:

- The printed colliders match the scene, including rotated and scaled boxes.
- The loop is fast enough to iterate on layout. If it is not, record why
  here before continuing.
- Prototype code is deleted or clearly marked as throwaway.

### Phase 1: Level File Format and Shared Types

Goal: one engine-independent file that the exporter and the runtime agree on.

Steps:

1. Define `LevelData` in GameCore:
   - static colliders: shape type, dimensions, position, rotation, filter
     layer, material, trigger flag
   - entity placements: type name, position, rotation, overrides
   - navmesh section (empty until Phase 5)
   - format version and content hash
2. Binary serializer with a magic header and version.
3. Round-trip tests in `tests/GameCore.Tests`.

Acceptance criteria:

- Serialize → deserialize is byte-identical and value-identical.
- The content hash is stable across runs and platforms.
- An unknown version fails loudly.

### Phase 2: Exporter

Goal: convert a Godot map scene into `<map>.level.bytes`.

Reference: Klotho's `GodotFPStaticColliderExporter.cs` (140 lines) and
`GodotFPStaticColliderConverter.cs` (131 lines) in
`Klotho/com.xpturn.klotho/Godot~/Adapters/Editor/`. Apache-2.0; keep
attribution if adapted.

Steps:

1. Editor tool (menu entry) plus a headless entry point
   (`godot --headless --script ...`) for CI and `deploy.sh`.
2. Walk the scene: every `CollisionShape3D` under a `StaticBody3D` (solid) or
   `Area3D` (trigger), using its global transform. Convert Box, Sphere and
   Capsule directly; bake scale into the dimensions.
3. Collect `EntitySpawn` markers into placements. Fail on unknown type names
   and invalid overrides.
4. Enforce `physics-limits.md`: static half-extent ≤ 40, all coordinates
   within ±8192. Fail with the node path.
5. Convert float to fixed point only here, using the `FixedPoint` types.

Acceptance criteria:

- The Phase 0 blockout exports without warnings.
- Deliberately broken inputs (unsupported shape, oversize wall, unknown
  entity type, mirrored transform, non-uniform sphere scale) each fail with a
  message naming the node.
- Exporting the same scene twice gives byte-identical output.

### Phase 3: Runtime Loading

Goal: the simulation builds its world from the level file instead of from
hardcoded spawns.

Steps:

1. `LevelLoader` in GameCore reads `LevelData` and creates static bodies with
   `BodyOperations.CreateBody` and `ShapeFactory.CreateShape`, matching what
   `SpawnSphereSystem` does today.
2. Entity registry mapping type names to spawn functions: `crate`, `dummy`,
   `player_spawn`. Spawn functions follow the pattern in `SpawnDummySystem`
   (the `IEntityType.OnCreate` sets components; the spawn fn adds body and
   shapes).
3. Replace `SpawnSphereSystem` (registered in `GameCore/SimulationSetup.cs:22`)
   with a level-loading system. Player spawns come from `player_spawn`
   placements.
4. Server and client load the same file. The client also instances the map
   scene for visuals, with its collision and marker nodes inactive at runtime.
5. Add the level content hash to the connect handshake, alongside the build
   fingerprint from `klotho-workflow-comparison.md` #9. Refuse to connect on
   mismatch.

Startup integration (done for entity placements):

```text
server: --level <file>, default levels/level_pipeline_test.level.bytes next to the binary
client: --level <name>, default level_pipeline_test → res://maps/<name>.level.bytes
  → LevelFile.Read / ReadFromDisk      verifies magic, version and payload hash
  → GameWorldSetup.CreateAndInitialize(level.Data)
       W.Initialize → Systems.Initialize → LevelLoader.Load
  → connect: LiteNetLib connection key = "space-level:" + SHA-256 of the file
       server rejects a mismatched key (not logged yet: the client simply
       never connects; both sides print their level hash at startup)
```

- `Server/Server.csproj` copies `Client/maps/*.level.bytes` into `levels/`
  on build and publish, so the Docker image ships the same files.
- `Client/export_presets.cfg` includes `*.level.bytes` in exported builds.
- The offline (in-process) server uses the level the client already read.
- The `.level.bytes` files are committed until CI can run the headless
  exporter. Re-export after changing a map; the scene is not read at runtime,
  so an edit without re-export has no effect.
- Static box colliders are in the format (version 2) and load as static ECS
  bodies (`LevelLoader`). `SpawnSphereSystem` no longer builds the ground; the
  old test arena lives in `maps/level_pipeline_test.tscn`.
- The client instances `res://maps/<name>.tscn` for visuals.
- Static colliders are authored like entities: a `LevelCollider` marker with a
  `ColliderRecipe` and overrides (`BoxColliderComponent` for the size). This
  replaces the `StaticBody3D` / `CollisionShape3D` convention below: the
  exporter refuses Godot physics nodes, and markers draw their box as lines in
  the editor only.
- Cost of static colliders as ECS bodies, measured on a 212-box test map:
  about 1.4 KB of rollback snapshot per box (314 KB vs 15 KB), 14 us to write
  and 59 us to load a snapshot, simulation 0.30 ms per tick (0.23 ms before).
  Moving static geometry out of the snapshot (Phase 6 step 2) is the fix if
  maps grow large.
- Still open: `player_spawn` placements, triggers, and adding the build
  fingerprint to the connection key.

Acceptance criteria:

- The game runs on the exported blockout with the same behaviour as the
  current test arena.
- Rollback state-hash tests still pass.
- A client with a different level file cannot connect and logs why.

### Phase 4: First Map-Placed Entity: Crate

Goal: prove the full path for an entity with gameplay, visuals and
animations.

Steps:

1. `Crate : IEntityType` in `GameCore/Entities/`, modelled on `Dummy.cs`:
   `Health` and `ViewId { Value = ViewAsset.Crate }`.
2. Add `Crate` to `ViewAsset` in `GameCore/Components/ViewId.cs`.
3. Spawn function adds a body and box shape; supports a `health` override.
4. `Client/entity/crate.tscn` with a `BoxMesh` placeholder, hit and break
   animations, and debris VFX; entry in
   `Client/config/entity_view_catalog.tres` with a prewarm count.

Acceptance criteria:

- Crates placed in the map appear, take damage through `DamageSystem`, and
  are removed through `DeathSystem`.
- Hit and break animations play on the client.
- A crate with a `health` override behaves differently from default crates.

### Phase 5: Navmesh

Goal: our own navmesh builder, independent of Godot, that turns the map's
static colliders into a fixed-point navmesh in the same level file.

Approach: DotRecast bakes the walkable surface, and Klotho's build pipeline
turns those triangles into the fixed-point navmesh the runtime uses. Klotho's
own Godot exporter is not used; it only reads Godot's `NavigationRegion3D`
bake.

Steps:

1. `NavBuilder`: a plain .NET library (no Godot references) called by the
   exporter and usable from a CLI or tests.
2. Input: the exported static colliders (Phase 2), triangulated into a
   triangle soup. Boxes become 12 triangles; spheres and capsules a coarse
   tessellation. Colliders with `nav = blocked` are obstacles only.
   Building from the collision geometry means the navmesh always matches what
   the simulation collides with.
3. Bake with DotRecast's Recast part (`RcBuilder`: voxelise, walkable
   filtering with agent radius, height, climb and max slope, regions,
   contours, poly mesh, detail mesh). Floats are acceptable because baking is
   offline and only the result ships. Detour (DotRecast's runtime) is not
   used.
4. Triangulate the detail mesh, weld vertices, convert to `Fixed64`.
5. Feed the triangles into a port of Klotho's
   `FPNavMeshBuildPipeline.Build(vertices, indices, areas, cellSize, ...)`
   (`Klotho/com.xpturn.klotho/Runtime/Deterministic/Navigation/`), which
   removes degenerate triangles, fixes T-junctions, builds adjacency and
   portals, and builds the spatial grid. It takes plain triangles, so it is
   independent of where they came from.
6. Store agent parameters (radius, height, climb, max slope) alongside the
   navmesh, as Klotho's `FPNavMesh` does.
7. Runtime query, A* and funnel per `navigation-assessment.md`.
8. Debug view: draw the navmesh from the level file in the client (Klotho's
   `NavMeshVisualizer.Godot.md` as reference).

#### Dynamic navigation

The baked navmesh never changes shape at runtime. Changes during play are
handled in two ways, both modelled on Klotho. Runtime rebaking (Klotho's
`FPNavMeshRebaker`) is out of scope; DotRecast is never used at runtime
because its float math would desync peers.

**A. Moving and temporary obstacles: avoidance, no navmesh change.**
Crates, players, dummies and other entities are not baked. Agents path over
them as if they were not there, and two things keep them out:

- `CharacterMover` collision (already in GameCore).
- ORCA local avoidance, ported from Klotho's `FPNavAvoidance.cs`:
  - static walls: `FPNavMeshObstacleExtractor.Extract` turns navmesh boundary
    edges into obstacle rings, loaded once with `LoadObstacles`. No ECS
    coupling; port as-is.
  - agent vs agent: `ComputeNewVelocity` reads Klotho's `Frame`/`EntityRef`;
    rewrite that part against a StaticEcs query over `NavAgent` + `Transform`.
  - Non-agent obstacles (a crate) are fed to avoidance as agents with zero
    velocity.

**B. Known changes: zones switched on and off.**
Doors, gates, bridges, and walls that can be destroyed at known places.

- Authoring: a `NavZone` node in the map scene (a box volume with an id such
  as `door_1`). The exporter tags every navmesh triangle inside it with the
  zone id and stores the zone → triangles table in the navmesh section.
- Runtime state lives in the ECS, never in the navmesh: a `NavZoneState`
  component (or resource) with a blocked flag per zone, set by gameplay (a
  door system, a destruction system). It is snapshotted, so rollback restores
  it.
- Before pathfinding each tick, a `NavZoneApplySystem` writes the flags into
  the mesh triangles' `isBlocked` (and optionally `costMultiplier`, e.g. for
  a slow zone). Klotho's pathfinder already honours both
  (`FPNavMeshPathfinder.cs` rejects blocked triangles and multiplies step
  cost). This follows Klotho's rule for runtime mesh changes: *the installed
  mesh is derived state; re-derive it from the frame every tick*
  (`Klotho/Docs/Navigation.Rebake.md`). Writing flags only on change is an
  optimisation that must still be correct after a rollback.
- Agents whose corridor crosses a zone that just closed must repath: compare a
  per-zone version counter stored in the agent's component.
- The static collider of a door or destructible wall is an ECS body (it
  changes during play), not a baked static collider; `nav` metadata on it
  only controls the zone, not the bake.

Acceptance criteria:

- The navmesh covers the walkable area of the blockout, respects obstacles,
  and keeps the agent radius away from walls.
- A closed `NavZone` makes agents repath around it; reopening restores the
  short path.
- Rolling back across a door toggle restores the same `isBlocked` flags and
  the same paths on every peer (state-hash test).
- Two agents walking head-on pass each other without getting stuck.
- Building the same level twice gives a byte-identical navmesh.
- The level hash changes when the navmesh changes.
- A test agent reaches a target around a building on both server and client
  with identical paths.

### Phase 6: Scale and Richer Geometry (When Needed)

Goal: support large maps, art assets, and non-box geometry. Start each item
only when its trigger is hit.

| Item | Trigger |
| --- | --- |
| Blender art assets replacing primitive placeholders | Layout is stable and final art starts |
| Static colliders outside the snapshot | Snapshot or full-sync size grows noticeably with map size |
| Static triangle-mesh shape | Geometry cannot be represented with boxes, spheres and capsules |
| Convex hulls, height fields | Content requires them |

Steps:

1. Art assets: model in Blender, export `.glb`, wrap each in a `.tscn` that
   adds Godot collision nodes. Maps swap placeholders for these scenes; the
   exporter does not change.
2. Move static colliders out of the ECS snapshot into a level resource with
   its own immutable static tree. Remove the static tree from
   `BroadPhase.Write` (`GameCore/Physics/BroadPhase.cs:325`). Queries and the
   character mover check both.
3. Static triangle-mesh shape: mesh vs sphere, capsule and box, using box3d
   `src/mesh.c` and `src/mesh_contact.c` as reference. The exporter accepts
   `ConcavePolygonShape3D` and splits large triangles to stay within Q16.16
   limits.
4. Convex hulls (`src/hull.c`, from `ConvexPolygonShape3D`) and height fields
   (`src/height_field.c`, from `HeightMapShape3D`) as content requires.

Acceptance criteria:

- Snapshot size is independent of the number of static colliders.
- A map with triangle-mesh terrain runs with rollback hashes intact.

## Open Questions

- **Planet or terrain-heavy maps.** `PlanetRes` suggests a planet. If maps are
  mostly sculpted terrain, the terrain mesh would come from Blender and Phase
  6 mesh or height-field support moves earlier.
- **Multiple maps.** How a map is selected, and whether the server rotates
  maps.
- **Override set.** Which per-instance overrides each entity type accepts,
  and how the exporter validates them.
- **Destructible static pieces.** Whether a destructible building is a baked
  static collider plus a small ECS entity (health, enabled flag), or a full
  ECS body.
