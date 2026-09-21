using System;
using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public static class ContactLifecycle {
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
