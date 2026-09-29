using FFS.Libraries.StaticEcs;
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
			}

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
