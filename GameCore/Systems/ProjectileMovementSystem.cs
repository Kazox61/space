using FFS.Libraries.StaticEcs;
using Fixed64;
using Shenanicode.Rollback;

namespace Space.GameCore;


public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public struct ProjectileMovementSystem : ISystem {
		public void Update() {
			W.Query().For(static (W.Entity entity, ref Transform transform, ref Velocity velocity) => {
				var radius = Systems.GetResource<PlanetRes>().Radius;

				var newPos = transform.Position + velocity.Value * Const.DeltaTime;

				var height = FVector3.Length(transform.Position) - radius;
				newPos = FVector3.Normalize(newPos) * (radius + height);
				transform.Position = newPos;

				var up = FVector3.Normalize(newPos);
				velocity.Value = FVector3.NormalizeSafe(
					velocity.Value - up * FVector3.Dot(velocity.Value, up),
					FVector3.Orthonormal(up)) * FVector3.Length(velocity.Value);
			});
		}
	}
}
