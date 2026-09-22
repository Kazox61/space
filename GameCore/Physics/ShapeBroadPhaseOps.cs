using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Broad-phase proxy operations for a <see cref="Shape"/>. Kept off <see cref="Shape"/> itself
	/// (which is top-level, see its remarks) because these need <see cref="W"/>.Entity and
	/// <see cref="BroadPhase"/>, both world-bound.
	/// </summary>
	public static class ShapeBroadPhaseOps {
		/// <summary>Create this shape's broad-phase proxy. <paramref name="self"/> is this shape's own entity.</summary>
		public static void CreateProxy(ref Shape shape, W.Entity self, BroadPhase broadPhase, BodyType proxyType, FWorldTransform bodyTransform, bool forcePairCreation) {
			shape.FatAabb = shape.ComputeFatAABB(bodyTransform, B3Config.SpeculativeDistance);
			shape.Aabb = shape.FatAabb;
			shape.ProxyKey = broadPhase.CreateProxy(proxyType, shape.FatAabb, shape.Filter.CategoryBits, self.GID, forcePairCreation);
		}

		/// <summary>Destroy this shape's broad-phase proxy, if it has one.</summary>
		public static void DestroyProxy(ref Shape shape, BroadPhase broadPhase) {
			if (shape.ProxyKey != Shape.NullProxyKey) {
				broadPhase.DestroyProxy(shape.ProxyKey);
				shape.ProxyKey = Shape.NullProxyKey;
			}
		}

		public static void ResetProxy(ref Shape shape, W.Entity self, BroadPhase broadPhase, BodyType proxyType, FWorldTransform bodyTransform, bool forcePairCreation = true) {
			DestroyProxy(ref shape, broadPhase);
			CreateProxy(ref shape, self, broadPhase, proxyType, bodyTransform, forcePairCreation);
		}

		/// <summary>
		/// Recompute this shape's AABB from the body's current world transform and, if it grew outside
		/// the existing fat AABB, re-fatten it and move the broad-phase proxy.
		/// </summary>
		public static void UpdateAABBs(ref Shape shape, FWorldTransform bodyTransform, BroadPhase broadPhase, FVector3 predictedTranslation = default) {
			var aabb = shape.ComputeFatAABB(bodyTransform, B3Config.SpeculativeDistance);
			if (predictedTranslation != FVector3.Zero) {
				var endTransform = bodyTransform;
				endTransform.Position += predictedTranslation;
				var endAabb = shape.ComputeFatAABB(endTransform, B3Config.SpeculativeDistance);
				aabb = new FAABB(
					FVector3.MinComponents(aabb.LowerBound, endAabb.LowerBound),
					FVector3.MaxComponents(aabb.UpperBound, endAabb.UpperBound));
			}
			SetAabb(ref shape, broadPhase, aabb);
		}

		/// <summary>Uses a rotation-independent bound around the center-of-mass path.</summary>
		public static void UpdateSweptAABBs(ref Shape shape, in Body body, FVector3 predictedTranslation, BroadPhase broadPhase) {
			var sweepRadius = shape.ComputeSweepRadius(body.LocalCenter) + B3Config.SpeculativeDistance;
			var radius = new FVector3(sweepRadius, sweepRadius, sweepRadius);
			var start = new FVector3(body.Center.X.To32(), body.Center.Y.To32(), body.Center.Z.To32());
			var end = start + predictedTranslation;
			SetAabb(ref shape, broadPhase, new FAABB(
				FVector3.MinComponents(start, end) - radius,
				FVector3.MaxComponents(start, end) + radius));
		}

		private static void SetAabb(ref Shape shape, BroadPhase broadPhase, FAABB aabb) {
			shape.Aabb = aabb;
			if (!shape.FatAabb.Contains(aabb)) {
				var margin = new FVector3(shape.AabbMargin, shape.AabbMargin, shape.AabbMargin);
				shape.FatAabb = new FAABB(aabb.LowerBound - margin, aabb.UpperBound + margin);

				if (shape.ProxyKey != Shape.NullProxyKey) {
					broadPhase.MoveProxy(shape.ProxyKey, shape.FatAabb);
				}
			}
		}
	}
}
