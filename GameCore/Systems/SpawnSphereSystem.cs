using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Fixed;
using Fixed32;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public struct SpawnSphereSystem : ISystem {
		private int _counter;

		public bool UpdateIsActive() => ++_counter % 60 == 0;

		public void Init() {
			Console.WriteLine("INITTTTTT");
			var groundBody = W.NewEntity<Default>();
			groundBody.Set(new Body {
				Type = BodyType.Static,
				Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.Zero, Fixed64.FP.Zero), FQuaternion.Identity),
			});
			ShapeFactory.CreateShape(groundBody, Shape.MakeSphere(FVector3.Zero, 40.ToFP()));
		}

		public void Update() {
			var position = FVector3.Up * 50;
			var body = W.NewEntity<Default>();
			body.Set(new ViewId { Value = ViewAsset.Sphere });
			body.Set(new Body {
				Type = BodyType.Dynamic,
				GravityScale = FP.One,
				Transform = new FWorldTransform(FPos.FromLocal(position), FQuaternion.Identity),
			});

			// See the console tests' remarks: box3d's default density (1000, water) overflows Fixed32's
			// inertia math for a shape this size, corrupting rotation response. Keep it sane.
			var shape = Shape.MakeSphere(FVector3.Zero, FP.One);
			shape.Density = FP.One;
			ShapeFactory.CreateShape(body, shape);
		}
	}
}
