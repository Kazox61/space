using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>Mandatory teardown boundary for entities carrying a physics body.</summary>
	public static class PhysicsBodyLifecycle {
		public static void DestroyBody(W.Entity bodyEntity) {
			var broadPhase = W.GetResource<BroadPhase>();
			var shapes = new List<W.Entity>();
			var shapeGids = new HashSet<EntityGID>();

			if (bodyEntity.Has<W.Links<Shapes>>()) {
				ref readonly var links = ref bodyEntity.Read<W.Links<Shapes>>();
				for (var i = 0; i < links.Length; i++) {
					if (links[i].Value.TryUnpack<TWorld>(out var shapeEntity) && shapeEntity.Has<Shape>()) {
						shapes.Add(shapeEntity);
						shapeGids.Add(shapeEntity.GID);
					}
				}
			}

			var contacts = new List<W.Entity>();
			foreach (var contactEntity in W.Query<All<Contact>>().Entities()) {
				ref readonly var contact = ref contactEntity.Read<Contact>();
				if (shapeGids.Contains(contact.ShapeA) || shapeGids.Contains(contact.ShapeB)) {
					contacts.Add(contactEntity);
				}
			}

			foreach (var contactEntity in contacts) {
				ContactLifecycle.DestroyContact(contactEntity, broadPhase);
			}

			foreach (var shapeEntity in shapes) {
				ShapeFactory.DestroyShape(shapeEntity, broadPhase);
			}

			bodyEntity.Destroy();
		}
	}
}
