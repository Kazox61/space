using System;
using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>World-space ray cast result: mirrors box3d's b3RayResult.</summary>
	public struct RayCastResult {
		public EntityGID Shape;
		public FPos Point;
		public FVector3 Normal;
		public FP Fraction;
	}

	/// <summary>World-space convex shape-cast result.</summary>
	public struct ShapeCastResult {
		public EntityGID Shape;
		public FPos Point;
		public FVector3 Normal;
		public FP Fraction;
	}

	/// <summary>
	/// World-level ray query entry points, dispatching across <see cref="BroadPhase"/>'s three trees
	/// down to each candidate shape's own precise <see cref="Shape.RayCast"/> -- the piece
	/// <c>BroadPhase.CastRay</c> deliberately leaves out (it stays entity-resolution-free, matching
	/// <see cref="BroadPhase.Query"/>). Mirrors box3d's b3World_CastRay/b3World_CastRayClosest.
	/// </summary>
	public static class PhysicsQueries {
		/// <summary>Same 5-way contract as <see cref="BroadPhase.RayCastCallback"/>, but with the precise world-space hit already computed.</summary>
		public delegate FP WorldRayCastCallback(EntityGID shapeGid, in FPos point, in FVector3 normal, FP fraction);
		public delegate FP WorldShapeCastCallback(EntityGID shapeGid, in FPos point, in FVector3 normal, FP fraction);
		public delegate bool WorldOverlapCallback(EntityGID shapeGid);

		/// <summary>
		/// Casts a ray from <paramref name="origin"/> (a full-precision world position, so this stays
		/// correct arbitrarily far from the broad phase's own Fixed32 frame -- see
		/// <see cref="FWorldTransform.InvTransformPoint"/>) by <paramref name="translation"/>, invoking
		/// <paramref name="callback"/> once per shape whose precise geometry the ray actually hits.
		/// Sensor shapes are skipped (no continuous collision for sensors, matching <see cref="Shape.IsSensor"/>'s
		/// doc comment and <see cref="CharacterMover"/>'s existing convention); <paramref name="filter"/>
		/// is checked the same way two shapes filter each other (<see cref="Filter.ShouldCollide"/>).
		/// </summary>
		public static void CastRay(BroadPhase broadPhase, FPos origin, FVector3 translation, Filter filter, WorldRayCastCallback callback) {
			CastRay(broadPhase, origin, translation, filter, QuerySensorMode.Exclude, callback);
		}

		public static void CastRay(BroadPhase broadPhase, FPos origin, FVector3 translation, Filter filter, QuerySensorMode sensors, WorldRayCastCallback callback) {
			PhysicsValidation.ValidateRayQuery(origin, translation);
			var treeOrigin = new FVector3(origin.X.To32Checked(), origin.Y.To32Checked(), origin.Z.To32Checked());
			var treeEnd = treeOrigin + translation;
			var rayAabb = new FAABB(
				FVector3.MinComponents(treeOrigin, treeEnd),
				FVector3.MaxComponents(treeOrigin, treeEnd));
			var candidates = CollectCandidates(broadPhase, rayAabb, filter);
			var maxFraction = FP.One;

			for (var i = 0; i < candidates.Count; i++) {
				var shapeGid = candidates[i];
				if (!shapeGid.TryUnpack<TWorld>(out var shapeEntity)) {
					continue;
				}

				ref readonly var shape = ref shapeEntity.Read<Shape>()!; // Broad-phase leaves are always shape entities.
				if ((shape.IsSensor && sensors == QuerySensorMode.Exclude) || !Filter.ShouldCollide(filter, shape.Filter)) {
					continue;
				}

				if (!TryGetBodyTransform(shapeEntity, out var bodyXf)) {
					continue;
				}

				var localOrigin = FWorldTransform.InvTransformPoint(bodyXf, origin);
				var localTranslation = FQuaternion.Inverse(bodyXf.Rotation) * translation;

				var output = shape.RayCast(new RayCastInput { Origin = localOrigin, Translation = localTranslation, MaxFraction = maxFraction });
				if (!output.Hit) {
					continue;
				}

				var worldPoint = FWorldTransform.TransformPoint(bodyXf, output.Point);
				var worldNormal = bodyXf.Rotation * output.Normal;
				var value = callback(shapeGid, worldPoint, worldNormal, output.Fraction);
				if (value == FP.Zero) {
					break;
				}
				if (value > FP.Zero && value <= maxFraction) {
					maxFraction = value;
				}
			}
		}

		/// <summary>Canned <see cref="CastRay"/> callback that keeps only the closest hit -- box3d's b3World_CastRayClosest.</summary>
		public static bool CastRayClosest(BroadPhase broadPhase, FPos origin, FVector3 translation, Filter filter, out RayCastResult result) {
			return CastRayClosest(broadPhase, origin, translation, filter, QuerySensorMode.Exclude, out result);
		}

		public static bool CastRayClosest(BroadPhase broadPhase, FPos origin, FVector3 translation, Filter filter, QuerySensorMode sensors, out RayCastResult result) {
			var found = false;
			var closest = default(RayCastResult);

			CastRay(broadPhase, origin, translation, filter, sensors, (EntityGID shapeGid, in FPos point, in FVector3 normal, FP fraction) => {
				found = true;
				closest = new RayCastResult { Shape = shapeGid, Point = point, Normal = normal, Fraction = fraction };
				return fraction;
			});

			result = closest;
			return found;
		}

		/// <summary>Reports shapes whose precise convex geometry overlaps the query proxy.</summary>
		public static void OverlapShape(BroadPhase broadPhase, FWorldTransform transform, ShapeProxy proxy, Filter filter, QuerySensorMode sensors, WorldOverlapCallback callback) {
			PhysicsValidation.ValidateQuery(transform, proxy, FVector3.Zero);
			var queryAabb = ComputeProxyAabb(transform, proxy);
			var candidates = CollectCandidates(broadPhase, queryAabb, filter);

			for (var i = 0; i < candidates.Count; i++) {
				var shapeGid = candidates[i];
				if (!TryGetShapeAndBodyTransform(shapeGid, filter, sensors, out var shape, out var bodyXf)) {
					continue;
				}

				var input = new DistanceInput {
					ProxyA = shape.MakeProxy(),
					ProxyB = proxy,
					Transform = FWorldTransform.InvMul(bodyXf, transform),
					UseRadii = true,
				};
				var cache = SimplexCache.Empty;
				if (Distance.ShapeDistance(input, ref cache).Distance < B3Config.OverlapSlop && !callback(shapeGid)) {
					break;
				}
			}
		}

		/// <summary>
		/// Linearly casts a convex query proxy through the world. Initial overlap is reported at
		/// fraction zero; callback return values follow <see cref="BroadPhase.RayCastCallback"/>.
		/// </summary>
		public static void CastShape(BroadPhase broadPhase, FWorldTransform transform, ShapeProxy proxy, FVector3 translation, Filter filter, QuerySensorMode sensors, WorldShapeCastCallback callback) {
			PhysicsValidation.ValidateQuery(transform, proxy, translation);
			var startAabb = ComputeProxyAabb(transform, proxy);
			var endTransform = transform;
			endTransform.Position += translation;
			var endAabb = ComputeProxyAabb(endTransform, proxy);
			var sweptAabb = new FAABB(
				FVector3.MinComponents(startAabb.LowerBound, endAabb.LowerBound),
				FVector3.MaxComponents(startAabb.UpperBound, endAabb.UpperBound));
			var candidates = CollectCandidates(broadPhase, sweptAabb, filter);

			var maxFraction = FP.One;
			for (var i = 0; i < candidates.Count; i++) {
				var shapeGid = candidates[i];
				if (!TryGetShapeAndBodyTransform(shapeGid, filter, sensors, out var shape, out var bodyXf)) {
					continue;
				}

				var output = Distance.ShapeCast(new ShapeCastPairInput {
					ProxyA = shape.MakeProxy(),
					ProxyB = proxy,
					Transform = FWorldTransform.InvMul(bodyXf, transform),
					TranslationB = FQuaternion.Inverse(bodyXf.Rotation) * translation,
					MaxFraction = maxFraction,
					CanEncroach = true,
				});
				if (!output.Hit) {
					continue;
				}

				var worldPoint = FWorldTransform.TransformPoint(bodyXf, output.Point);
				var worldNormal = bodyXf.Rotation * output.Normal;
				var value = callback(shapeGid, worldPoint, worldNormal, output.Fraction);
				if (value == FP.Zero) {
					break;
				}
				if (value > FP.Zero && value <= maxFraction) {
					maxFraction = value;
				}
			}
		}

		public static bool CastShapeClosest(BroadPhase broadPhase, FWorldTransform transform, ShapeProxy proxy, FVector3 translation, Filter filter, QuerySensorMode sensors, out ShapeCastResult result) {
			var found = false;
			var closest = default(ShapeCastResult);
			CastShape(broadPhase, transform, proxy, translation, filter, sensors, (EntityGID shapeGid, in FPos point, in FVector3 normal, FP fraction) => {
				found = true;
				closest = new ShapeCastResult { Shape = shapeGid, Point = point, Normal = normal, Fraction = fraction };
				return fraction;
			});
			result = closest;
			return found;
		}

		private static List<EntityGID> CollectCandidates(BroadPhase broadPhase, FAABB aabb, Filter filter) {
			var candidates = new List<EntityGID>();
			broadPhase.Query(aabb, QueryMask(filter), candidates);
			candidates.Sort(static (a, b) => a.Raw.CompareTo(b.Raw));
			return candidates;
		}

		private static FAABB ComputeProxyAabb(FWorldTransform transform, ShapeProxy proxy) {
			var points = proxy.Points!;
			var first = transform.Rotation * points[0];
			var lower = first;
			var upper = first;
			for (var i = 1; i < points.Length; i++) {
				var point = transform.Rotation * points[i];
				lower = FVector3.MinComponents(lower, point);
				upper = FVector3.MaxComponents(upper, point);
			}
			var radius = new FVector3(proxy.Radius, proxy.Radius, proxy.Radius);
			return FWorldTransform.OffsetAABB(new FAABB(lower - radius, upper + radius), transform.Position);
		}

		private static bool TryGetShapeAndBodyTransform(EntityGID shapeGid, Filter filter, QuerySensorMode sensors, out Shape shape, out FWorldTransform transform) {
			shape = default;
			transform = default;
			if (!shapeGid.TryUnpack<TWorld>(out var shapeEntity) || !shapeEntity.Has<Shape>()) {
				return false;
			}
			shape = shapeEntity.Read<Shape>();
			return !(shape.IsSensor && sensors == QuerySensorMode.Exclude)
				&& Filter.ShouldCollide(filter, shape.Filter)
				&& TryGetBodyTransform(shapeEntity, out transform);
		}

		private static ulong QueryMask(Filter filter) => filter.GroupIndex > 0 ? ulong.MaxValue : filter.MaskBits;

		private static bool TryGetBodyTransform(W.Entity shapeEntity, out FWorldTransform transform) {
			if (shapeEntity.Has<W.Link<BodyOwner>>()) {
				ref readonly var owner = ref shapeEntity.Read<W.Link<BodyOwner>>();
				if (owner.Value.TryUnpack<TWorld>(out var bodyEntity)) {
					transform = bodyEntity.Read<Body>()!.Transform; // BodyOwner always links to an entity with Body.
					return true;
				}
			}

			transform = default;
			return false;
		}
	}
}
