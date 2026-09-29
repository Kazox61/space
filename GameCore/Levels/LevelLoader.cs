using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public static class LevelLoader {
		public static void Load(LevelData level) {
			// Validate every physics object before mutating the world so a bad level cannot load partially.
			foreach (var placement in level.Entities) {
				LevelDataValidation.Validate(placement);
				var components = placement.Components;
				PhysicsValidation.ValidateShape(CreateBoxShape(components.BoxShape!.Value), components.Body!.Value, placement.Transform, placement.SourcePath);
				if (components.DoorMotion is { } motion) {
					// A door must be valid at both ends of its travel.
					PhysicsValidation.ValidateShape(CreateBoxShape(components.BoxShape.Value), components.Body.Value, OpenTransform(placement.Transform, motion), placement.SourcePath);
				}
			}
			LevelDataValidation.ValidateZoneLinks(level.Entities, level.NavZones);
			var zoneIds = level.NavZones.Select(static zone => zone.Id).Order(StringComparer.Ordinal).ToArray();

			// Static geometry first, then zones, then the rest, so entity ids stay in this order.
			foreach (var placement in level.Entities) {
				if (placement.Type == LevelEntityType.StaticGeometry) {
					SpawnStaticGeometry(placement);
				}
			}

			SpawnNavZones(level.Navigation);

			foreach (var placement in level.Entities) {
				switch (placement.Type) {
					case LevelEntityType.StaticGeometry:
						break;
					case LevelEntityType.Crate:
						SpawnCrate(placement);
						break;
					case LevelEntityType.Door:
						SpawnDoor(placement, ZoneIndex(zoneIds, placement));
						break;
					case LevelEntityType.PressurePlate:
						SpawnPressurePlate(placement, ZoneIndex(zoneIds, placement));
						break;
					default:
						throw new InvalidDataException($"{placement.SourcePath}: unsupported entity type {placement.Type}.");
				}
			}
		}

		private static void SpawnCrate(in EntityPlacement placement) {
			var components = placement.Components;
			var crate = W.NewEntity(new Crate {
				InitialHealth = components.Health!.Value,
				Loot = components.Loot!.Value,
				View = components.View!.Value,
			});

			var transform = new Transform();
			transform.SetFromWorldTransform(placement.Transform);
			crate.Set(transform);
			BodyOperations.CreateBody(crate, components.Body!.Value, placement.Transform);

			ShapeFactory.CreateShape(crate, CreateBoxShape(components.BoxShape!.Value));
		}

		private static void SpawnDoor(in EntityPlacement placement, int zone) {
			var components = placement.Components;
			var motion = components.DoorMotion!.Value;
			var openTransform = OpenTransform(placement.Transform, motion);
			var door = W.NewEntity(new Door {
				State = new DoorState {
					Zone = zone,
					ClosedPosition = placement.Transform.Position,
					OpenOffset = openTransform.Position - placement.Transform.Position,
					Speed = motion.Speed,
					Open = motion.StartsOpen,
					FullyOpen = motion.StartsOpen,
				},
			});

			var start = motion.StartsOpen ? openTransform : placement.Transform;
			var transform = new Transform();
			transform.SetFromWorldTransform(start);
			door.Set(transform);
			BodyOperations.CreateBody(door, components.Body!.Value, start);
			ShapeFactory.CreateShape(door, CreateBoxShape(components.BoxShape!.Value));
		}

		private static void SpawnPressurePlate(in EntityPlacement placement, int zone) {
			// No ViewId: like static geometry, the map scene draws the plate.
			var plate = W.NewEntity(new PressurePlate { Zone = zone });
			var transform = new Transform();
			transform.SetFromWorldTransform(placement.Transform);
			plate.Set(transform);
			BodyOperations.CreateBody(plate, placement.Components.Body!.Value, placement.Transform);
			var shape = CreateBoxShape(placement.Components.BoxShape!.Value);
			shape.IsSensor = true;
			shape.EnableSensorEvents = true;
			shape.Filter = Filter.Trigger;
			ShapeFactory.CreateShape(plate, shape);
		}

		/// <summary>The door's open pose: its placed pose moved by the open offset, turned into world space.</summary>
		private static FWorldTransform OpenTransform(in FWorldTransform closed, in DoorMotionData motion) {
			var offset = FQuaternion.Normalize(closed.Rotation) * motion.OpenOffset;
			return new FWorldTransform(closed.Position + offset, closed.Rotation);
		}

		/// <summary>The linked zone's index among the level's zone ids in ordinal order; see <see cref="DoorState.Zone"/>.</summary>
		private static int ZoneIndex(string[] zoneIds, in EntityPlacement placement) {
			return Array.IndexOf(zoneIds, placement.Components.ZoneLink!);
		}

		/// <summary>One open <see cref="NavZoneState"/> per baked zone, for gameplay to switch.</summary>
		private static void SpawnNavZones(LevelNavigation? navigation) {
			if (navigation is null) {
				return;
			}
			for (var zone = 0; zone < navigation.Zones.Count; zone++) {
				W.NewEntity<Default>().Set(NavZoneState.Open(zone));
			}
		}

		private static void SpawnStaticGeometry(in EntityPlacement placement) {
			// No ViewId: the client draws static geometry from the map scene itself.
			var entity = W.NewEntity<Default>();
			var transform = new Transform();
			transform.SetFromWorldTransform(placement.Transform);
			entity.Set(transform);
			BodyOperations.CreateBody(entity, placement.Components.Body!.Value, placement.Transform);
			ShapeFactory.CreateShape(entity, CreateBoxShape(placement.Components.BoxShape!.Value));
		}

		private static Shape CreateBoxShape(in BoxShapeData box) {
			var shape = Shape.MakeBox(FVector3.Zero, box.HalfExtents);
			shape.Density = box.Density;
			return shape;
		}
	}
}
