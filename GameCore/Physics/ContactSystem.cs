using System;
using FFS.Libraries.StaticEcs;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Spawns a contact entity for every new broad-phase pair, then re-evaluates every existing
	/// contact's manifold each tick: flips <see cref="Contact.Touching"/> and sends
	/// begin/end-touch events on transition, and destroys the contact once its shapes' fat AABBs no
	/// longer overlap (mirrors box3d destroying contacts on AABB separation). Contacts with no awake
	/// body are skipped entirely: sleeping bodies do not move, so their manifolds, touch state, and
	/// warm-start impulses stay valid until the island wakes (box3d's sleeping contact sets).
	/// </summary>
	public struct ContactSystem : ISystem {
		public void Update() {
			var runtime = PhysicsRuntime.Get();
			var broadPhase = W.GetResource<BroadPhase>();
			var timestamp = PhysicsRuntime.Timestamp();
			runtime.Pending.MovedProxies = broadPhase.MovedProxyCount;
			broadPhase.UpdatePairs(TryCreateContact);
			runtime.AddTime(PhysicsPhase.PairUpdate, timestamp);
			timestamp = PhysicsRuntime.Timestamp();

			// Self-heal: TryCreateContact sets Contact + both links atomically, so this should never
			// match anything -- but a Contact entity missing a link has been observed in practice
			// across a client/server rollback boundary. Destroy rather than let the main loop below
			// crash reading a link that isn't there.
			W.Query<All<Contact>, Or<None<W.Link<ShapeA>>, None<W.Link<ShapeB>>>>().For(ref broadPhase,
				static (ref BroadPhase bp, W.Entity contactEntity) => ContactLifecycle.DestroyContact(contactEntity, bp));

#pragma warning disable FFSECS0050 // Link<ShapeA> and Link<ShapeB> are distinct relation types; the analyzer's duplicate check compares by open-generic definition and can't tell them apart.
			W.Query<All<W.Link<ShapeA>, W.Link<ShapeB>>>().For(ref runtime, static (ref PhysicsRuntime rt, W.Entity contactEntity, ref Contact contact) => {
#pragma warning restore FFSECS0050
				ref readonly var shapeALink = ref contactEntity.Read<W.Link<ShapeA>>();
				ref readonly var shapeBLink = ref contactEntity.Read<W.Link<ShapeB>>();

				if (!shapeALink.Value.TryUnpack<TWorld>(out var entityA) || !shapeBLink.Value.TryUnpack<TWorld>(out var entityB)) {
					ContactLifecycle.DestroyContact(contactEntity, W.GetResource<BroadPhase>());
					return;
				}

				if (!TryGetBody(entityA, out var bodyA) || !TryGetBody(entityB, out var bodyB)) {
					return;
				}
				ref readonly var bodyDataA = ref bodyA.Read<Body>()!; // TryGetBody only resolves entities with Body.
				ref readonly var bodyDataB = ref bodyB.Read<Body>()!;
				// Static/static pairs only exist for events and are still evaluated; a pair is frozen
				// only when a sleeping body is involved and nothing awake could have moved it.
				if (!PhysicsSleep.IsSimulated(bodyDataA) && !PhysicsSleep.IsSimulated(bodyDataB)
					&& (PhysicsSleep.IsSleeping(bodyDataA) || PhysicsSleep.IsSleeping(bodyDataB))) {
					rt.Pending.ContactsSleeping++;
					return;
				}

				ref readonly var shapeDataA = ref entityA.Read<Shape>()!; // Link<ShapeA>/<ShapeB> always resolve to shape entities.
				ref readonly var shapeDataB = ref entityB.Read<Shape>()!;

				if (!FAABB.Overlaps(shapeDataA.FatAabb, shapeDataB.FatAabb)
					|| !Filter.ShouldCollide(shapeDataA.Filter, shapeDataB.Filter)) {
					ContactLifecycle.DestroyContact(contactEntity, W.GetResource<BroadPhase>());
					return;
				}

				var xfA = bodyDataA.Transform;
				var xfB = bodyDataB.Transform;
				rt.Pending.ContactsUpdated++;

				var oldManifold = contact.Manifold;
				var manifold = Manifold.Collide(shapeDataA, xfA, shapeDataB, xfB);
				MatchManifoldPoints(oldManifold, ref manifold);
				var wasTouching = contact.Touching;
				var isTouching = manifold.PointCount > 0 && manifold.MinSeparation() <= FP.Zero;
				contact.Manifold = manifold;
				contact.Touching = isTouching;
				contact.EnableContactEvents = !contact.IsSensorContact && (shapeDataA.EnableContactEvents || shapeDataB.EnableContactEvents);
				contact.EnableSensorEvents = contact.IsSensorContact && shapeDataA.EnableSensorEvents && shapeDataB.EnableSensorEvents;

				if (isTouching) {
					rt.Pending.TouchingContacts++;
				}

				if (isTouching && !wasTouching) {
					SendBeginEvent(contact, entityA.GID, entityB.GID);
				} else if (!isTouching && wasTouching) {
					SendEndEvent(contact, entityA.GID, entityB.GID);
				}
			});
			runtime.AddTime(PhysicsPhase.Narrowphase, timestamp);
		}

		private static void MatchManifoldPoints(in Manifold oldManifold, ref Manifold manifold) {
			Span<bool> claimed = stackalloc bool[Manifold.MaxPoints];
			claimed.Clear();
			for (var i = 0; i < manifold.PointCount; i++) {
				var point = manifold.GetPoint(i);
				point.NormalImpulse = FP.Zero;
				point.Persisted = false;
				for (var j = 0; j < oldManifold.PointCount; j++) {
					if (claimed[j]) {
						continue;
					}
					var oldPoint = oldManifold.GetPoint(j);
					if (!oldPoint.HasFeatureId || !point.HasFeatureId || oldPoint.FeatureId != point.FeatureId) {
						continue;
					}
					point.NormalImpulse = oldPoint.NormalImpulse;
					point.Persisted = true;
					claimed[j] = true;
					break;
				}
				manifold.SetPoint(i, point);
			}
		}

		internal static void SendBeginEvent(in Contact contact, EntityGID shapeA, EntityGID shapeB) {
			if (contact.IsSensorContact) {
				if (contact.EnableSensorEvents) {
					if (contact.ShapeAIsSensor) {
						W.SendEvent(new SensorBeginTouchEvent { SensorShape = shapeA, VisitorShape = shapeB });
					}
					if (contact.ShapeBIsSensor) {
						W.SendEvent(new SensorBeginTouchEvent { SensorShape = shapeB, VisitorShape = shapeA });
					}
				}
			} else if (contact.EnableContactEvents) {
				W.SendEvent(new ContactBeginTouchEvent { ShapeA = shapeA, ShapeB = shapeB });
			}
		}

		internal static void SendEndEvent(in Contact contact, EntityGID shapeA, EntityGID shapeB) {
			if (contact.IsSensorContact) {
				if (contact.EnableSensorEvents) {
					if (contact.ShapeAIsSensor) {
						W.SendEvent(new SensorEndTouchEvent { SensorShape = shapeA, VisitorShape = shapeB });
					}
					if (contact.ShapeBIsSensor) {
						W.SendEvent(new SensorEndTouchEvent { SensorShape = shapeB, VisitorShape = shapeA });
					}
				}
			} else if (contact.EnableContactEvents) {
				W.SendEvent(new ContactEndTouchEvent { ShapeA = shapeA, ShapeB = shapeB });
			}
		}

		private static bool TryGetBody(W.Entity shapeEntity, out W.Entity bodyEntity) {
			bodyEntity = default;
			return shapeEntity.Has<W.Link<BodyOwner>>()
				&& shapeEntity.Read<W.Link<BodyOwner>>().Value.TryUnpack<TWorld>(out bodyEntity)
				&& bodyEntity.Has<Body>();
		}

		internal static bool TryCreateContact(EntityGID a, EntityGID b) {
			if (!a.TryUnpack<TWorld>(out var entityA) || !b.TryUnpack<TWorld>(out var entityB)) {
				return false;
			}
			if (!entityA.Has<Shape>() || !entityB.Has<Shape>()
				|| !entityA.Has<W.Link<BodyOwner>>() || !entityB.Has<W.Link<BodyOwner>>()) {
				return false;
			}

			ref readonly var shapeDataA = ref entityA.Read<Shape>();
			ref readonly var shapeDataB = ref entityB.Read<Shape>()!;

			if (!Manifold.Supports(shapeDataA.Type, shapeDataB.Type)
				|| !Filter.ShouldCollide(shapeDataA.Filter, shapeDataB.Filter)) {
				return false;
			}

			ref readonly var ownerA = ref entityA.Read<W.Link<BodyOwner>>();
			ref readonly var ownerB = ref entityB.Read<W.Link<BodyOwner>>();
			// Box3D rejects pairs with no dynamic body. Keep event-enabled pairs because the game
			// intentionally uses kinematic projectiles against kinematic dummies for hit detection.
			var isSensorContact = shapeDataA.IsSensor || shapeDataB.IsSensor;
			var needsContactEvents = !isSensorContact && (shapeDataA.EnableContactEvents || shapeDataB.EnableContactEvents);
			var needsSensorEvents = isSensorContact && shapeDataA.EnableSensorEvents && shapeDataB.EnableSensorEvents;
			if (ownerA.Value == ownerB.Value
				|| !ownerA.Value.TryUnpack<TWorld>(out var bodyA) || !bodyA.Has<Body>()
				|| !ownerB.Value.TryUnpack<TWorld>(out var bodyB) || !bodyB.Has<Body>()
				|| !BodyOperations.IsEnabled(bodyA.Read<Body>()) || !BodyOperations.IsEnabled(bodyB.Read<Body>())
				|| (!needsContactEvents && !needsSensorEvents && bodyA.Read<Body>().Type != BodyType.Dynamic && bodyB.Read<Body>().Type != BodyType.Dynamic)) {
				return false;
			}
			var isEventOnly = bodyA.Read<Body>().Type != BodyType.Dynamic && bodyB.Read<Body>().Type != BodyType.Dynamic;

			PhysicsRuntime.Get().Pending.PairsCreated++;
			W.NewEntity<Default>().Set(
				new W.Link<ShapeA>(entityA),
				new W.Link<ShapeB>(entityB),
				new Contact {
					ShapeA = a,
					ShapeB = b,
					EnableContactEvents = needsContactEvents,
					EnableSensorEvents = needsSensorEvents,
					IsSensorContact = isSensorContact,
					ShapeAIsSensor = shapeDataA.IsSensor,
					ShapeBIsSensor = shapeDataB.IsSensor,
					IsEventOnly = isEventOnly,
				}
			);
			return true;
		}
	}
}
