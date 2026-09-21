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

			if (bodyEntity.Has<W.Links<Shapes>>()) {
				ref readonly var links = ref bodyEntity.Read<W.Links<Shapes>>();
				for (var i = 0; i < links.Length; i++) {
					if (links[i].Value.TryUnpack<TWorld>(out var shapeEntity) && shapeEntity.Has<Shape>()) {
						shapes.Add(shapeEntity);
					}
				}
			}

			ContactLifecycle.DestroyContactsForBody(bodyEntity, broadPhase);

			foreach (var shapeEntity in shapes) {
				ShapeFactory.DestroyShape(shapeEntity, broadPhase);
			}

			bodyEntity.Destroy();
		}
	}
}
