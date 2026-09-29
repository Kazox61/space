# Level Authoring

Production tracer bullet for typed Godot entity recipes and deterministic GameCore level files.

Author reusable defaults in an `EntityRecipe` resource. Place `EntitySpawn` nodes in a map and add only per-instance values to `Overrides`. The exporter resolves both layers, validates them, converts transforms to fixed point, and writes a versioned `.level.bytes` file with an embedded payload hash.

Export from the editor: open the map scene and press **Export Level** in the 3D viewport toolbar (or Project → Tools → Export Level). It writes `<scene>.level.bytes` next to the scene from the scene as it is in the editor, unsaved changes included, and shows the result as a toast. The `Level Export` addon in `addons/level_export` must be enabled (it is in `project.godot`).

Export a map headlessly (`--output` defaults to `<scene>.level.bytes`):

```sh
/path/to/Godot --headless --path Client level_authoring/level_export_runner.tscn -- \
  --input=res://maps/level_pipeline_test.tscn \
  --output=res://maps/level_pipeline_test.level.bytes
```

Everything is set up the same way: a marker node with a `Recipe` resource for defaults and `Overrides` for per-instance values.

| Marker | Recipe | Components | Exports |
| --- | --- | --- | --- |
| `EntitySpawn` | `EntityRecipe` | Health, Loot, Body, BoxShape, View, Navigation, ZoneLink, DoorMotion, RailMotion | an entity placement |

The recipe's `EntityType` decides which components the placement needs:

| Entity type | Example recipe | Required | Not allowed | Body |
| --- | --- | --- | --- | --- |
| `Crate` | `maps/crate_recipe.tres` | Health, Loot, Body, BoxShape, View | Navigation, RailMotion | Dynamic or Kinematic |
| `StaticGeometry` | `maps/static_box_recipe.tres` | Body, BoxShape, Navigation | Health, Loot, View, RailMotion | Static |
| `Door` | `maps/door_recipe.tres` | Body, BoxShape, View, ZoneLink, DoorMotion | Health, Loot, Navigation, RailMotion | Kinematic |
| `PressurePlate` | `maps/pressure_plate_recipe.tres` | Body, BoxShape, ZoneLink | Health, Loot, View, Navigation, DoorMotion, RailMotion | Static |
| `Platform` | `maps/platform_recipe.tres` | Body, BoxShape, View, Navigation, ZoneLink, RailMotion | Health, Loot, DoorMotion | Kinematic |

A door and a pressure plate are linked through a `ZoneLinkComponent` naming a `NavZone` id. The door's node transform is its closed pose; `DoorMotionComponent` sets how far it slides to open (in its own frame), its speed, and whether it starts open. While a door is not fully open its zone is blocked for navigation. A character stepping onto an empty plate toggles every door of the plate's zone. Plates are drawn by the map scene (give them a mesh child); doors have a view and move. In the sample map, `VaultDoor` fills the only doorway into a walled room (the `vault` zone) and opens by sliding into the wall beside it, so the open door never stands on walkable ground. `VaultPlate`, left of the player spawns, and `VaultPlateInside` both toggle it. Walls that should not be walked on get `NavigationComponent(ObstacleOnly)`.

A platform's node transform is the floor where its linked zone is available. Its `NavigationComponent` contributes that start-pose box to the bake without creating a second static body, so it can be the only floor over a shaft. `RailMotionComponent` sets the local offset to its other stop, speed, and optional start-at-end state. The platform dwells for three seconds at each stop; its zone is blocked whenever it is away from the authored floor. A zone cannot be controlled by both doors and platforms.

Walls and floors are `StaticGeometry` placements: the static recipe sets a static body, a 1 m box and `Walkable` navigation, and each wall overrides `BoxShape` with its size (at most 80 m per side). The export fails on a missing or disallowed component.

A `NavZone` node marks a switchable navigation zone (a door, gate or bridge): set `ZoneId` (the node name when empty) and `Size`, and rotate it only about Y. The bake splits the navmesh along the box, so the simulation can close exactly the ground inside it through the zone's `NavZoneState` entity. The box must enclose the floor it switches, zones must not overlap, and a zone over no walkable ground fails the export. It draws blue lines in the editor and does not collide.

Markers take position and rotation from their node transform and must have unit scale; sizes live in components. Static geometry and platform start poses declare how they contribute to the offline navigation bake with a `NavigationComponent`: `Walkable`, `ObstacleOnly`, or `Excluded`. Every export bakes a navmesh from those sources (`NavBakeSettings.Default`, sized for the player capsule) and stores it with its settings in the level file; a map without a `Walkable` source gets no navmesh. The bake runs only in editor builds: `Space.NavBuilder` and DotRecast are referenced in the `Debug` configuration alone, so exported games never contain them. An `EntitySpawn` draws its box as orange lines in the editor and nothing in the game. Static geometry has no view, so give it a mesh (a child works) for what players see. Godot physics nodes (`StaticBody3D`, `CollisionShape3D`, `Area3D`) are refused by the exporter.

Every Godot export preset must list `*.level.bytes` under Resources → "Filters to export non-resource files", or exported builds stop at startup with "Level file ... is missing". The editor reads presets only at startup, so restart it after the filter changes in `export_presets.cfg`.

The server and client load the exported file at startup (`--level` selects another one); see "Startup integration" in `docs/level-pipeline.md`. Re-export after editing a map and commit the `.level.bytes` file.
