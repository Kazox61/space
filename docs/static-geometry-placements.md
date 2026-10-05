# Static Geometry as Entity Placements

## Purpose

This is the plan for making `EntitySpawn` the only marker that places
entities. A static wall becomes an entity whose components are a static body,
a box shape, and a navigation setting. The Godot collider stack
(`LevelCollider`, `ColliderRecipe`, `ColliderComponent`,
`BoxColliderComponent`), the `StaticBox` file section, and `SpawnStaticBox`
are deleted. `NavZone` stays as it is.

Related documents:

- `level-pipeline.md`: the level format and authoring pipeline
- `navigation-assessment.md`: the navmesh bake that consumes static geometry

## Decisions

1. **Keep `LevelEntityType` and add `StaticGeometry = 2`.** Crates spawn as
   the StaticEcs entity type `Crate`, which carries an ID used in snapshots.
   Walls spawn as `Default`. The placement therefore still needs a type, and
   components cannot replace it. Each type has a table of required and
   forbidden components.
2. **Navigation is required on `StaticGeometry` and forbidden on everything
   else.** Today `LevelCollider.Navigation` quietly defaults to `Walkable`.
   Making it required means each static recipe states its navigation role
   explicitly.
3. **View is forbidden on `StaticGeometry` for now.** The client draws static
   geometry from the map scene, so a view component would render it twice.
   This is the same rule as today's "no ViewId".
4. **Spawn order stays the same.** Static geometry spawns first, then nav
   zones, then everything else. Within each group the loader keeps the order
   of `LevelData.Entities`, which is source-path order for any level read from
   a file. Entity IDs come out in the same order as today.
5. **The bake still takes boxes.** `StaticBox` becomes `NavSourceBox`, a value
   derived from placements. It is no longer stored in the file.
6. **Static geometry keeps density 1.** `BoxShapeData.Density` is required
   for every box shape, and density does nothing on a static body. Today
   static shapes get `Shape.MakeBox`'s default of `FP.One`. The static recipe
   uses `BoxShapeComponent`'s default of 1, so the physics state does not
   change.

## Step 1: Data model (`GameCore/Levels/LevelData.cs`)

```csharp
public enum LevelEntityType : byte { Crate = 1, StaticGeometry = 2 }

public readonly record struct BoxShapeData(FVector3 HalfExtents, FP Density);

public readonly record struct PlacementComponents(
	int? Health, LootKind? Loot, BodyType? Body,
	BoxShapeData? BoxShape, ViewAsset? View, NavContribution? Navigation);

public readonly record struct EntityPlacement(
	string SourcePath, LevelEntityType Type, FWorldTransform Transform, PlacementComponents Components);
```

- Delete `CratePlacementData` and `StaticBox`.
- `LevelData` keeps only `Entities`, `NavZones` and `Navigation`, so the
  `staticBoxes` constructor parameter goes. Update `WithNavigation` to match.
- Add `LevelData.NavigationSources`, an `IReadOnlyList<NavSourceBox>` of the
  `StaticGeometry` placements ordered by source path. Build it once in the
  constructor: `BakeLevel` and `NavDebugDraw` both read it.
- Move `NavSourceBox(SourcePath, Transform, HalfExtents, Navigation)` next to
  the navigation types.

## Step 2: Builder (`GameCore/Levels/EntityPlacementBuilder.cs`)

- Add `Navigation` to `LevelEntityComponentKind`, with a matching
  `SetNavigation(NavContribution)`.
- `Build()` no longer requires every component. It fills in whatever was set
  and leaves the rest to validation. Validation reports "missing X" or "Y not
  allowed on StaticGeometry" for each entity type.
- Delete `StaticBoxBuilder.cs`, including `LevelColliderComponentKind`.

## Step 3: Validation (`GameCore/Levels/LevelDataValidation.cs`)

| Type             | Required                           | Forbidden          | Body types allowed |
|------------------|------------------------------------|--------------------|--------------------|
| `Crate`          | Health, Loot, Body, BoxShape, View | Navigation         | Dynamic, Kinematic |
| `StaticGeometry` | Body, BoxShape, Navigation         | Health, Loot, View | Static             |

- Component checks move over from today's two methods:
  - health 1–1000
  - loot, view and navigation enum values
  - half-extent limit of 4 for dynamic bodies and 40 for everything else, so
    static sides stay at most 80 m
  - density in (0, 2]
  - `PhysicsMassValidation.ValidateShape`, which only checks dynamic bodies
  - non-zero rotation
  - origin bounds
- Remove `Validate(in StaticBox)` and the `Type != Crate` check.
- Source paths share one namespace now: a crate and a wall can no longer have
  the same path. Godot node paths are unique, so this changes nothing in
  practice.

## Step 4: Codec (`GameCore/Levels/LevelDataCodec.cs`)

- Bump the version from 5 to 6. Old `.level.bytes` files fail with the
  existing "unsupported version" error.
- Placement layout: source path, type byte, transform, a presence-bitmask
  byte (one bit per `LevelEntityComponentKind`, six in use), then only the
  fields that are present, in enum order.
- Reading rejects unknown mask bits and validates every placement after
  reading it, as today.
- Remove the static-box section, `MaximumStaticBoxCount` and
  `Write/ReadStaticBox`. The file becomes: entities, zones, navigation.

## Step 5: Loader (`GameCore/Levels/LevelLoader.cs`)

- Keep pre-validation as it is, including `PhysicsValidation.ValidateShape`
  with the placement's own body type.
- Spawn all `StaticGeometry` placements, then `SpawnNavZones`, then the other
  types (Decision 4).
- Static geometry spawns as `W.NewEntity<Default>()` with a `Transform`, a
  static body and a box shape. That's today's `SpawnStaticBox`, reading from
  `Components`.
- `SpawnCrate` reads `Components.Health.Value` and the other fields.
- One `CreateBoxShape(in BoxShapeData)` replaces `CreateStaticBoxShape` and
  `CreateCrateShape`.

## Step 6: Navigation consumers

- `NavBuilder/StaticBoxTriangulator.cs`: takes `IEnumerable<NavSourceBox>`.
  Rename it to `NavSourceTriangulator`.
- `NavBuilder/NavMeshBaker.cs`: the `Bake(...)` overloads take
  `NavSourceBox`, and `BakeLevel` uses `level.NavigationSources`. The message
  "no Walkable collider" becomes "no Walkable static geometry".
- `GameCore/Navigation/NavDebugDraw.cs`: `_sources = level.NavigationSources`.
- `Client/setup/ClientGame.cs:36`: the log line prints `staticGeometry=` by
  counting placements. Update the `LevelCollider` mention in the doc comment
  at line 115.

## Step 7: Godot authoring (`Client/level_authoring/`)

- Add `NavigationComponent : EntityComponent`. It exports
  `NavContribution Value` and calls `builder.SetNavigation(Value)`.
- `BodyComponent` needs no change: its enum already includes `Static`.
- `EntitySpawn` needs no functional change. It already draws the box outline
  from `BoxShape` and shows a view preview only when there is a view.
  Optionally, give crates a non-orange outline so walls and crates are easy
  to tell apart. Both markers use `BoxOutline.DefaultColor` today.
- `LevelExporter`:
  - `Collect`: drop the `LevelCollider` case and the `staticBoxes` list, and
    change the `CollisionObject3D` refusal to "use an EntitySpawn with a
    static body".
  - Line 30: the summary becomes `navigation=none (no Walkable static
    geometry)`.
  - Line 52: the summary prints `staticGeometry=` in place of
    `staticBoxes=`.
- Delete `LevelCollider.cs`, `ColliderRecipe.cs`, `ColliderComponent.cs` and
  `BoxColliderComponent.cs` with their `.uid` files. Do this only after Step 8
  has migrated the scene, or the scene fails to load.

## Step 8: Migrating the maps (`Client/maps/`)

1. Before changing anything, export `level_pipeline_test` and note
   `navTriangles`, `recastPolygons` and `sha256` from the summary line.
2. Rewrite `static_box_recipe.tres` as an `EntityRecipe` with
   `EntityType = StaticGeometry`, `Body(Static)`,
   `BoxShape(Size 1,1,1, Density 1)` and `Navigation(Walkable)`. Keep the
   file path and UID so references keep working.
3. Edit `level_pipeline_test.tscn` by hand. `Recipe` and `Overrides` keep
   their names but change type, and a script swap in the editor would leave
   the typed array `Array[ExtResource("6_collider_component")]` wrong. Edit
   the scene as follows:
   - Point `Ground` and `TestBox` at the `EntitySpawn` script (`1_spawn`).
   - Turn the `GroundBox` (80×1×80) and `TestBoxBox` (4×1×4) sub-resources
     into `BoxShapeComponent`s with the same sizes, in an
     `Array[ExtResource("8_entity_component")]`.
   - Give `TestBox` an extra `NavigationComponent(ObstacleOnly)` override and
     remove `Navigation = 1`.
   - Remove the `LevelCollider`, `ColliderComponent` and
     `BoxColliderComponent` ext_resources.
   - Keep node names so the source paths do not change. Keep `Ground`'s
     `Mesh` child and `TestBox`'s transform.
4. Open and save the scene in Godot to normalise it, then check the diff.
5. Re-export and compare. `navTriangles` and `recastPolygons` must match the
   numbers from step 1. The bake input is identical: `BoxShapeComponent`
   converts `Size * 0.5f` the same way `BoxColliderComponent` did, the
   transforms are unchanged, and the triangulator sorts by source path. The
   file hash will change because the format changed.

## Step 9: Tests

- `TestLevels.cs`, `NavigationBenchmarks.cs:171`, `NavTestSession.cs` and
  `NavAgentTests.cs`: build static geometry with a
  `TestLevels.StaticBox(path, transform, halfExtents, nav)` helper that
  returns an `EntityPlacement`. Both `TestLevels.Arena` boxes, including
  `Arena/Box`, are `Walkable` today and must stay that way.
- `LevelDataTests.cs`:
  - Rewrite `CodecRoundTripsStaticBoxes`,
    `CodecRejectsInvalidNavigationContribution`,
    `CodecRejectsOversizedStaticBox` and
    `LoaderCreatesStaticBodiesWithoutViews` against placements.
  - Rewrite `ExportedSampleContainsDifferentCrateHealthOverrides` to find
    `Ground` and `TestBox` among the placements.
  - Keep the connection-key test for navigation contribution.
- `NavMeshBakerTests.cs`, `LevelNavigationTests.cs` and
  `StaticBoxTriangulatorTests.cs`: switch to `NavSourceBox` or placements.
- New tests:
  - StaticGeometry without Navigation is rejected.
  - A Crate with Navigation is rejected.
  - StaticGeometry with Health or View is rejected.
  - StaticGeometry with a dynamic body is rejected.
  - A Crate with a static body is rejected.
  - A presence mask with unknown bits is rejected.
  - The loader spawns static geometry before crates, even when the input
    lists a crate first.
  - `NavigationSources` returns only static geometry, ordered by source path.
- Run `dotnet test`, plus the TestSupport scenario harness for rollback and
  physics.

## Step 10: Docs

- `Client/level_authoring/README.md`: the marker table collapses to a single
  `EntitySpawn` row, with example recipes for a crate and for static
  geometry. The navigation paragraph points at `NavigationComponent`.
- `docs/level-pipeline.md`: document format v6 and the per-type component
  rules, and replace the `LevelCollider` authoring paragraph.
- `docs/navigation-assessment.md`: update its references to `StaticBox`,
  `LevelCollider`, `ColliderRecipe` and `StaticBoxBuilder`.

## Commit order

Each commit builds and passes tests.

1. **Steps 1–9 together**: GameCore model, builder, validation, codec, loader,
   NavBuilder and debug draw; Client authoring; map migration with the
   re-exported `.level.bytes`; all tests. These cannot be split. Once the
   codec reads only v6, the checked-in v5 level file fails to load. That
   breaks the tests that read it (`LevelDataTests`, `NavMeshBakerTests`,
   `NavMeshQueryTests`, `NavTestSession`, `NavigationBenchmarks`) and the
   game's default level.
2. **Docs** (Step 10).

## Risks

- **Scene migration:** a hand-edited `.tscn` can reference a wrong resource
  ID. Opening and saving the scene in Godot in Step 8 catches this, and the
  unchanged `navTriangles` and `recastPolygons` counts confirm the geometry.
- **Loader order:** if static geometry does not spawn first, entity IDs
  shift. That is harmless for correctness, but snapshots would no longer
  match old recordings, if any are kept.

## Out of scope, but made easier

- **Doors:** a `Door` type with a kinematic body, a view and a `NavZone`
  reference fits into the same component table.
- **Other shapes:** a sphere or capsule component would work the same way for
  crates and static geometry.
