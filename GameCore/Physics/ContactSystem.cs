using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Fixed32;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Spawns a contact entity for every new broad-phase pair, then re-evaluates every existing
	/// contact's manifold each tick: flips <see cref="Contact.Touching"/> and sends
	/// begin/end-touch events on transition, and destroys the contact once its shapes' fat AABBs no
	/// longer overlap (mirrors box3d destroying contacts on AABB separation).
	/// </summary>
	public struct ContactSystem : ISystem {
		public void Update() {
			var broadPhase = Systems.GetResource<BroadPhase>();
			broadPhase.UpdatePairs(TryCreateContact);

			W.Query<All<Contact>>().For(static (W.Entity contactEntity, ref Contact contact) => {
				ref readonly var shapeALink = ref contactEntity.Read<W.Link<ShapeA>>();
				ref readonly var shapeBLink = ref contactEntity.Read<W.Link<ShapeB>>();

				if (!shapeALink.Value.TryUnpack<TWorld>(out var entityA) || !shapeBLink.Value.TryUnpack<TWorld>(out var entityB)) {
					contactEntity.Destroy();
					return;
				}

				ref readonly var shapeDataA = ref entityA.Read<Shape>();
				ref readonly var shapeDataB = ref entityB.Read<Shape>();

				if (!FAABB.Overlaps(shapeDataA.FatAabb, shapeDataB.FatAabb)) {
					if (contact.Touching) {
						W.SendEvent(new ContactEndTouchEvent { ShapeA = entityA.GID, ShapeB = entityB.GID });
					}

					Systems.GetResource<BroadPhase>().ForgetPair(entityA.GID, entityB.GID);
					contactEntity.Destroy();
					return;
				}

				if (!TryGetBodyTransform(entityA, out var xfA) || !TryGetBodyTransform(entityB, out var xfB)) {
					return;
				}

				var manifold = Manifold.Collide(shapeDataA, xfA, shapeDataB, xfB);
				var wasTouching = contact.Touching;
				var isTouching = manifold.PointCount > 0 && manifold.Point0.Separation <= B3Config.SpeculativeDistance;
				contact.Manifold = manifold;
				contact.Touching = isTouching;

				if (isTouching && !wasTouching) {
					W.SendEvent(new ContactBeginTouchEvent { ShapeA = entityA.GID, ShapeB = entityB.GID });
				} else if (!isTouching && wasTouching) {
					W.SendEvent(new ContactEndTouchEvent { ShapeA = entityA.GID, ShapeB = entityB.GID });
				}
			});
		}

		private static bool TryGetBodyTransform(W.Entity shapeEntity, out Fixed.FWorldTransform transform) {
			if (shapeEntity.Has<W.Link<BodyOwner>>()) {
				ref readonly var owner = ref shapeEntity.Read<W.Link<BodyOwner>>();
				if (owner.Value.TryUnpack<TWorld>(out var bodyEntity)) {
					transform = bodyEntity.Read<Body>().Transform;
					return true;
				}
			}

			transform = default;
			return false;
		}

		private static void TryCreateContact(EntityGID a, EntityGID b) {
			if (!a.TryUnpack<TWorld>(out var entityA) || !b.TryUnpack<TWorld>(out var entityB)) {
				return;
			}

			ref readonly var shapeDataA = ref entityA.Read<Shape>();
			ref readonly var shapeDataB = ref entityB.Read<Shape>();

			if (!Filter.ShouldCollide(shapeDataA.Filter, shapeDataB.Filter)) {
				return;
			}

			W.NewEntity<Default>().Set(new W.Link<ShapeA>(entityA), new W.Link<ShapeB>(entityB), new Contact());
		}
	}
}
