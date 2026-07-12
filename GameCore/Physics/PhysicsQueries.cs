using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Fixed32;
using Fixed;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>World-space ray cast result: mirrors box3d's b3RayResult.</summary>
	public struct RayCastResult {
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
			// The broad-phase trees themselves are Fixed32 (see Shape.ComputeFatAABB/BroadPhase),
			// so tree pruning is inherently bounded to that precision; only the per-candidate local
			// transform below (InvTransformPoint) needs to stay full-precision.
			var treeOrigin = new FVector3(origin.X.To32(), origin.Y.To32(), origin.Z.To32());

			broadPhase.CastRay(treeOrigin, translation, FP.One, (shapeGid, _, _, maxFraction) => {
				if (!shapeGid.TryUnpack<TWorld>(out var shapeEntity)) {
					return -FP.One;
				}

				ref readonly var shape = ref shapeEntity.Read<Shape>()!; // Broad-phase leaves are always shape entities.
				if (shape.IsSensor || !Filter.ShouldCollide(filter, shape.Filter)) {
					return -FP.One;
				}

				if (!TryGetBodyTransform(shapeEntity, out var bodyXf)) {
					return -FP.One;
				}

				var localOrigin = FWorldTransform.InvTransformPoint(bodyXf, origin);
				var localTranslation = FQuaternion.Inverse(bodyXf.Rotation) * translation;

				var output = shape.RayCast(new RayCastInput { Origin = localOrigin, Translation = localTranslation, MaxFraction = maxFraction });
				if (!output.Hit) {
					return -FP.One;
				}

				var worldPoint = FWorldTransform.TransformPoint(bodyXf, output.Point);
				var worldNormal = bodyXf.Rotation * output.Normal;
				return callback(shapeGid, worldPoint, worldNormal, output.Fraction);
			});
		}

		/// <summary>Canned <see cref="CastRay"/> callback that keeps only the closest hit -- box3d's b3World_CastRayClosest.</summary>
		public static bool CastRayClosest(BroadPhase broadPhase, FPos origin, FVector3 translation, Filter filter, out RayCastResult result) {
			var found = false;
			var closest = default(RayCastResult);

			CastRay(broadPhase, origin, translation, filter, (EntityGID shapeGid, in FPos point, in FVector3 normal, FP fraction) => {
				found = true;
				closest = new RayCastResult { Shape = shapeGid, Point = point, Normal = normal, Fraction = fraction };
				return fraction;
			});

			result = closest;
			return found;
		}

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
