using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Gives every character (anything with a <see cref="Mover"/>) a <see cref="SensorProxy"/>: a kinematic
	/// body whose sensor capsule matches the mover's. Characters have no <see cref="Body"/>, so without it
	/// they could not touch sensors such as pressure plates. Runs after <see cref="CharacterMoverSystem"/>
	/// and steers each proxy by velocity onto its character's new position, so the solver moves it this
	/// tick and its contacts persist; a proxy is only teleported after a jump (a respawn), which ends and
	/// restarts its contacts as it should. Proxies of destroyed characters are destroyed.
	/// </summary>
	public struct SensorProxySystem : ISystem {
		/// <summary>Beyond this per-axis distance the character jumped rather than moved.</summary>
		private static readonly FP TeleportDistance = FP.FromRatio(2, 1);

		public void Update() {
			foreach (var character in W.Query<All<Mover, Transform>, None<HasSensorProxy>>().Entities()) {
				ref readonly var mover = ref character.Read<Mover>();
				var proxy = W.NewEntity<Default>();
				proxy.Set(new SensorProxy { Owner = character.GID });
				BodyOperations.CreateBody(proxy, BodyType.Kinematic, new FWorldTransform(character.Read<Transform>().ToWorldTransform().Position, FQuaternion.Identity));
				var shape = Shape.MakeCapsule(mover.CapsuleCenter1, mover.CapsuleCenter2, mover.CapsuleRadius);
				shape.IsSensor = true;
				shape.EnableSensorEvents = true;
				shape.Filter = Filter.SensorProxy;
				ShapeFactory.CreateShape(proxy, shape);
				character.Set<HasSensorProxy>();
			}

			var invDt = Const.InvDeltaTime.To32();
			// Destroying the current proxy (and its shape and contacts, which are outside this query) is allowed in Strict mode.
			foreach (var proxy in W.Query<All<SensorProxy, Body>>().Entities()) {
				if (!proxy.Read<SensorProxy>().Owner.TryUnpack<TWorld>(out var owner) || !owner.Has<Transform>()) {
					BodyOperations.DestroyBody(proxy);
					continue;
				}

				var target = owner.Read<Transform>().ToWorldTransform().Position;
				var delta = target - proxy.Read<Body>().Transform.Position;
				if (FP.Abs(delta.X) > TeleportDistance || FP.Abs(delta.Y) > TeleportDistance || FP.Abs(delta.Z) > TeleportDistance) {
					BodyOperations.SetTransform(proxy, new FWorldTransform(target, FQuaternion.Identity));
					BodyOperations.SetLinearVelocity(proxy, FVector3.Zero);
				} else {
					BodyOperations.SetLinearVelocity(proxy, delta * invDt);
				}
			}
		}
	}
}
