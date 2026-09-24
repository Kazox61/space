using System;
using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public static class ContactLifecycle {
		public static void DestroyContactsForShape(EntityGID shape, BroadPhase broadPhase) {
			var runtime = PhysicsRuntime.Get();
			var contacts = runtime.RentEntityList();
			foreach (var contactEntity in W.Query<All<Contact>>().Entities()) {
				ref readonly var contact = ref contactEntity.Read<Contact>();
				if (contact.ShapeA == shape || contact.ShapeB == shape) {
					contacts.Add(contactEntity);
				}
			}

			DestroyAll(runtime, contacts, broadPhase);
		}

		private static void DestroyAll(PhysicsRuntime runtime, List<W.Entity> contacts, BroadPhase broadPhase) {
			foreach (var contactEntity in contacts) {
				DestroyContact(contactEntity, broadPhase);
			}
			runtime.Return(contacts);
		}

		public static void ReevaluateContactsForShape(W.Entity shapeEntity, BroadPhase broadPhase) {
			if (!shapeEntity.Has<Shape>()) {
				throw new InvalidOperationException("Contact reevaluation requires a shape entity.");
			}
			ref readonly var shape = ref shapeEntity.Read<Shape>();
			var runtime = PhysicsRuntime.Get();
			var contacts = runtime.RentEntityList();
			foreach (var contactEntity in W.Query<All<Contact>>().Entities()) {
				ref readonly var contact = ref contactEntity.Read<Contact>();
				var otherGid = contact.ShapeA == shapeEntity.GID ? contact.ShapeB
					: contact.ShapeB == shapeEntity.GID ? contact.ShapeA : default;
				if (otherGid == default || !otherGid.TryUnpack<TWorld>(out var otherEntity)
					|| !otherEntity.Has<Shape>() || !Filter.ShouldCollide(shape.Filter, otherEntity.Read<Shape>().Filter)) {
					if (otherGid != default) {
						contacts.Add(contactEntity);
					}
				}
			}

			DestroyAll(runtime, contacts, broadPhase);
		}

		public static void ReevaluateEventFlagsForShape(W.Entity shapeEntity, BroadPhase broadPhase) {
			var runtime = PhysicsRuntime.Get();
			var contactsToDestroy = runtime.RentEntityList();
			foreach (var contactEntity in W.Query<All<Contact>>().Entities()) {
				ref var contact = ref contactEntity.Ref<Contact>();
				if (contact.ShapeA != shapeEntity.GID && contact.ShapeB != shapeEntity.GID) {
					continue;
				}
				if (!contact.ShapeA.TryUnpack<TWorld>(out var shapeA) || !shapeA.Has<Shape>()
					|| !contact.ShapeB.TryUnpack<TWorld>(out var shapeB) || !shapeB.Has<Shape>()) {
					contactsToDestroy.Add(contactEntity);
					continue;
				}
				ref readonly var dataA = ref shapeA.Read<Shape>();
				ref readonly var dataB = ref shapeB.Read<Shape>();
				var oldContact = contact;
				contact.EnableContactEvents = !contact.IsSensorContact && (dataA.EnableContactEvents || dataB.EnableContactEvents);
				contact.EnableSensorEvents = contact.IsSensorContact && dataA.EnableSensorEvents && dataB.EnableSensorEvents;
				if (contact.Touching) {
					var oldEnabled = oldContact.IsSensorContact ? oldContact.EnableSensorEvents : oldContact.EnableContactEvents;
					var newEnabled = contact.IsSensorContact ? contact.EnableSensorEvents : contact.EnableContactEvents;
					if (oldEnabled && !newEnabled) {
						ContactSystem.SendEndEvent(oldContact, contact.ShapeA, contact.ShapeB);
					} else if (!oldEnabled && newEnabled) {
						ContactSystem.SendBeginEvent(contact, contact.ShapeA, contact.ShapeB);
					}
				}

				if (contact.IsEventOnly && !contact.EnableContactEvents && !contact.EnableSensorEvents) {
					contactsToDestroy.Add(contactEntity);
				}
			}
			DestroyAll(runtime, contactsToDestroy, broadPhase);
		}

		public static void DestroyContactsForBody(W.Entity bodyEntity, BroadPhase broadPhase) {
			if (!bodyEntity.Has<W.Links<Shapes>>()) {
				return;
			}

			var runtime = PhysicsRuntime.Get();
			var contacts = runtime.RentEntityList();
			foreach (var contactEntity in W.Query<All<Contact>>().Entities()) {
				ref readonly var contact = ref contactEntity.Read<Contact>();
				ref readonly var links = ref bodyEntity.Read<W.Links<Shapes>>();
				for (var i = 0; i < links.Length; i++) {
					var shape = links[i].Value;
					if (contact.ShapeA == shape || contact.ShapeB == shape) {
						contacts.Add(contactEntity);
						break;
					}
				}
			}

			DestroyAll(runtime, contacts, broadPhase);
		}

		public static void DestroyContact(W.Entity contactEntity, BroadPhase broadPhase) {
			if (!contactEntity.Has<Contact>()) {
				throw new InvalidOperationException("ContactLifecycle.DestroyContact requires a contact entity.");
			}

			ref readonly var contact = ref contactEntity.Read<Contact>();
			broadPhase.ForgetPair(contact.ShapeA, contact.ShapeB);

			if (contact.Touching) {
				ContactSystem.SendEndEvent(contact, contact.ShapeA, contact.ShapeB);
			}

			// Removing support from a sleeping island must wake it (box3d's b3DestroyContact with
			// wakeBodies): otherwise a stack resting on a destroyed or moved body would hang in the air.
			if (PhysicsSleep.LinksIsland(contact)) {
				if (PhysicsSleep.TryGetBodyOfShape(contact.ShapeA, out var bodyA)) {
					PhysicsSleep.WakeBody(bodyA);
				}
				if (PhysicsSleep.TryGetBodyOfShape(contact.ShapeB, out var bodyB)) {
					PhysicsSleep.WakeBody(bodyB);
				}
			}

			contactEntity.Destroy();
		}
	}
}
