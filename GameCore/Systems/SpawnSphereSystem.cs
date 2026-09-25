using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public struct SpawnSphereSystem : ISystem {
		public void Init() {
			// Ground and walls come from the level file (LevelLoader). Every Body-owning entity also
			// carries a Transform, kept in sync by BodyTransformSyncSystem -- seed it here too so
			// rendering (TransformViewBehavior) doesn't pop on the first tick before that system runs.
			var position = FVector3.Up * 50 + FVector3.Forward * 2;
			var sphereWorldTransform = new FWorldTransform(FPos.FromLocal(position), FQuaternion.Identity);
			var sphereTransform = new Transform();
			sphereTransform.SetFromWorldTransform(sphereWorldTransform);

			var body = W.NewEntity<Default>();
			body.Set(new ViewId { Value = ViewAsset.Sphere });
			body.Set(sphereTransform);
			BodyOperations.CreateBody(body, BodyType.Dynamic, sphereWorldTransform);

			// See the console tests' remarks: box3d's default density (1000, water) overflows Fixed32's
			// inertia math for a shape this size, corrupting rotation response. Keep it sane.
			var shape = Shape.MakeSphere(FVector3.Zero, FP.Half);
			shape.Density = FP.One;
			// Without this a pushed sphere converts sliding into rolling almost immediately (normal
			// Coulomb friction only kills *sliding*, not rolling) and then rolls forever -- see
			// ContactSolverSystem's rolling-resistance block.
			shape.Material.RollingResistance = FP.FromRatio(1, 4);
			ShapeFactory.CreateShape(body, shape);
		}
	}
}
