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
				if (placement.Type == LevelEntityType.Crate) {
					var shape = CreateCrateShape(placement.Crate);
					PhysicsValidation.ValidateShape(shape, placement.Crate.BodyType, placement.Transform, placement.SourcePath);
				}
			}
			foreach (var box in level.StaticBoxes) {
				LevelDataValidation.Validate(box);
				PhysicsValidation.ValidateShape(CreateStaticBoxShape(box), BodyType.Static, box.Transform, box.SourcePath);
			}

			foreach (var box in level.StaticBoxes) {
				SpawnStaticBox(box);
			}

			foreach (var placement in level.Entities) {
				switch (placement.Type) {
					case LevelEntityType.Crate:
						SpawnCrate(placement);
						break;
					default:
						throw new InvalidDataException($"{placement.SourcePath}: unsupported entity type {placement.Type}.");
				}
			}
		}

		private static void SpawnCrate(in EntityPlacement placement) {
			LevelDataValidation.Validate(placement);
			var crate = W.NewEntity(new Crate {
				InitialHealth = placement.Crate.Health,
				Loot = placement.Crate.Loot,
				View = placement.Crate.View,
			});

			var transform = new Transform();
			transform.SetFromWorldTransform(placement.Transform);
			crate.Set(transform);
			BodyOperations.CreateBody(crate, placement.Crate.BodyType, placement.Transform);

			ShapeFactory.CreateShape(crate, CreateCrateShape(placement.Crate));
		}

		private static void SpawnStaticBox(in StaticBox box) {
			// No ViewId: the client draws static geometry from the map scene itself.
			var entity = W.NewEntity<Default>();
			var transform = new Transform();
			transform.SetFromWorldTransform(box.Transform);
			entity.Set(transform);
			BodyOperations.CreateBody(entity, BodyType.Static, box.Transform);
			ShapeFactory.CreateShape(entity, CreateStaticBoxShape(box));
		}

		private static Shape CreateStaticBoxShape(in StaticBox box) => Shape.MakeBox(FVector3.Zero, box.HalfExtents);

		private static Shape CreateCrateShape(in CratePlacementData crate) {
			var shape = Shape.MakeBox(FVector3.Zero, crate.BoxHalfExtents);
			shape.Density = crate.Density;
			return shape;
		}
	}
}
