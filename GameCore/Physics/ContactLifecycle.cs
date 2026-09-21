using System;
using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public static class ContactLifecycle {
		public static void DestroyContactsForShape(EntityGID shape, BroadPhase broadPhase) {
			var contacts = new List<W.Entity>();
			foreach (var contactEntity in W.Query<All<Contact>>().Entities()) {
				ref readonly var contact = ref contactEntity.Read<Contact>();
				if (contact.ShapeA == shape || contact.ShapeB == shape) {
					contacts.Add(contactEntity);
				}
			}

			foreach (var contactEntity in contacts) {
				DestroyContact(contactEntity, broadPhase);
			}
		}

		public static void DestroyContactsForBody(W.Entity bodyEntity, BroadPhase broadPhase) {
			if (!bodyEntity.Has<W.Links<Shapes>>()) {
				return;
			}

			var shapes = new HashSet<EntityGID>();
			ref readonly var links = ref bodyEntity.Read<W.Links<Shapes>>();
			for (var i = 0; i < links.Length; i++) {
				shapes.Add(links[i].Value);
			}

			var contacts = new List<W.Entity>();
			foreach (var contactEntity in W.Query<All<Contact>>().Entities()) {
				ref readonly var contact = ref contactEntity.Read<Contact>();
				if (shapes.Contains(contact.ShapeA) || shapes.Contains(contact.ShapeB)) {
					contacts.Add(contactEntity);
				}
			}

			foreach (var contactEntity in contacts) {
				DestroyContact(contactEntity, broadPhase);
			}
		}

		public static void DestroyContact(W.Entity contactEntity, BroadPhase broadPhase) {
			if (!contactEntity.Has<Contact>()) {
				throw new InvalidOperationException("ContactLifecycle.DestroyContact requires a contact entity.");
			}

			ref readonly var contact = ref contactEntity.Read<Contact>();
			broadPhase.ForgetPair(contact.ShapeA, contact.ShapeB);

			if (contact.Touching) {
				W.SendEvent(new ContactEndTouchEvent { ShapeA = contact.ShapeA, ShapeB = contact.ShapeB });
			}

			contactEntity.Destroy();
		}
	}
}
