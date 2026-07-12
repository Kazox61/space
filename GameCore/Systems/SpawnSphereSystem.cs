using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Fixed;
using Fixed32;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public struct SpawnSphereSystem : ISystem {
		public void Init() {
			// Every Body-owning entity also carries a Transform, kept in sync by
			// BodyTransformSyncSystem -- seed it here too so rendering (TransformViewBehavior)
			// doesn't pop on the first tick before that system runs.
			var groundWorldTransform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity);
			var groundTransform = new Transform();
			groundTransform.SetFromWorldTransform(groundWorldTransform);

			var groundBody = W.NewEntity<Default>();
			groundBody.Set(new ViewId { Value = ViewAsset.Platform });
			groundBody.Set(groundTransform);
			groundBody.Set(new Body {
				Type = BodyType.Static,
				Transform = groundWorldTransform,
			});
			ShapeFactory.CreateShape(groundBody, Shape.MakeBox(FVector3.Zero, new FVector3(40.ToFP(), FP.Half, 40.ToFP())));

			var position = FVector3.Up * 50 + FVector3.Forward * 2;
			var sphereWorldTransform = new FWorldTransform(FPos.FromLocal(position), FQuaternion.Identity);
			var sphereTransform = new Transform();
			sphereTransform.SetFromWorldTransform(sphereWorldTransform);

			var body = W.NewEntity<Default>();
			body.Set(new ViewId { Value = ViewAsset.Sphere });
			body.Set(sphereTransform);
			body.Set(new Body {
				Type = BodyType.Dynamic,
				GravityScale = FP.One,
				Transform = sphereWorldTransform,
			});

			// See the console tests' remarks: box3d's default density (1000, water) overflows Fixed32's
			// inertia math for a shape this size, corrupting rotation response. Keep it sane.
			var shape = Shape.MakeSphere(FVector3.Zero, FP.Half);
			shape.Density = FP.One;
			// Without this a pushed sphere converts sliding into rolling almost immediately (normal
			// Coulomb friction only kills *sliding*, not rolling) and then rolls forever -- see
			// ContactSolverSystem's rolling-resistance block.
			shape.Material.RollingResistance = FP.FromRatio(1, 4);
			ShapeFactory.CreateShape(body, shape);

			// A 4x1x4 test obstacle, behind spawn (away from the sphere's push path) -- for trying
			// out walking into a wall-height box and jumping on top of it.
			var boxPosition = new FPos(Fixed64.FP.Zero, Fixed64.FP.One, Fixed64.FP.FromRatio(-6, 1));
			var boxWorldTransform = new FWorldTransform(boxPosition, FQuaternion.Identity);
			var boxTransform = new Transform();
			boxTransform.SetFromWorldTransform(boxWorldTransform);

			var boxBody = W.NewEntity<Default>();
			boxBody.Set(new ViewId { Value = ViewAsset.Box });
			boxBody.Set(boxTransform);
			boxBody.Set(new Body {
				Type = BodyType.Static,
				Transform = boxWorldTransform,
			});
			ShapeFactory.CreateShape(boxBody, Shape.MakeBox(FVector3.Zero, new FVector3(2.ToFP(), FP.Half, 2.ToFP())));
		}
	}
}
