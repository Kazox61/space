using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Fixed;
using Fixed32;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Spawns a row of one-hit-kill targets, each sliding back and forth along its own
	/// <see cref="PatrolRail"/> -- an arcade shooting-gallery setup for trying out the shooting
	/// mechanic. Neighbors start at opposite ends of the rail moving toward each other (alternating
	/// initial direction) so they don't all move in lockstep.
	/// </summary>
	public struct SpawnDummySystem : ISystem {
		public void Init() {
			var res = Systems.GetResource<DummyRes>();

			for (var i = 0; i < res.Count; i++) {
				SpawnAt(i);
			}
		}

		/// <summary>
		/// Creates one Dummy at the given row slot, using the same position/direction convention as
		/// <see cref="Init"/>'s row layout. Also called by <see cref="Core{TWorld}.DummyRespawnSystem"/>
		/// to recreate a killed target at the same slot after its respawn delay.
		/// </summary>
		public static void SpawnAt(int index) {
			var res = Systems.GetResource<DummyRes>();

			var z = res.RowZ - Fixed64.FP.FromRatio(index, 1) * res.RowSpacing;
			var x = res.Count > 1
				? Fixed64.FP.Lerp(res.RailMin, res.RailMax, Fixed64.FP.FromRatio(index, res.Count - 1))
				: res.RailMin;
			var direction = index % 2 == 0 ? Fixed64.FP.One : -Fixed64.FP.One;

			// Player-sized standing capsule, same dimensions as the player's own Mover capsule (see Player.cs).
			var position = new Fixed64.FVector3(x, Fixed64.FP.One + Fixed64.FP.Half, z);
			var worldTransform = new FWorldTransform(new FPos(position.X, position.Y, position.Z), FQuaternion.Identity);

			var transform = new Transform();
			transform.SetFromWorldTransform(worldTransform);

			var dummy = W.NewEntity<Dummy>();
			dummy.Set(transform);
			dummy.Set(new PatrolRail { Min = res.RailMin, Max = res.RailMax });
			dummy.Set(new RailSlot { Index = index });

			ref var body = ref dummy.Ref<Body>()!; // Dummy.OnCreate always sets Body.
			body.Transform = worldTransform;
			body.LinearVelocity = new FVector3((direction * res.Speed).To32(), FP.Zero, FP.Zero);

			var shape = Shape.MakeCapsule(
				new FVector3(FP.Zero, -FP.Half, FP.Zero),
				new FVector3(FP.Zero, FP.Half, FP.Zero),
				FP.Half);
			shape.EnableContactEvents = true;
			ShapeFactory.CreateShape(dummy, shape);
		}
	}
}
