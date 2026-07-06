using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Fixed32;
using Fixed;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Recomputes a body's mass, center of mass, and inertia from its shapes. Ported from box3d's
	/// b3UpdateBodyMassData (body.c). Skips the minExtent/maxExtent bookkeeping box3d also does
	/// here — that only feeds sleeping/CCD, both out of scope for this pass.
	/// </summary>
	public static class BodyMassUpdate {
		public static void Update(W.Entity bodyEntity) {
			ref var body = ref bodyEntity.Ref<Body>();

			body.Mass = FP.Zero;
			body.Inertia = FMatrix3.Zero;
			body.InvMass = FP.Zero;
			body.InvInertiaLocal = FMatrix3.Zero;
			body.InvInertiaWorld = FMatrix3.Zero;
			body.LocalCenter = FVector3.Zero;

			// Static and kinematic bodies have zero mass; only their center tracks the transform.
			if (body.Type != BodyType.Dynamic || !bodyEntity.Has<W.Links<Shapes>>()) {
				body.Center = body.Transform.Position;
				return;
			}

			ref var shapeLinks = ref bodyEntity.Ref<W.Links<Shapes>>();
			var count = shapeLinks.Length;
			var masses = new MassData[count];

			// Pass 1: accumulate mass and the (not yet centered) mass-weighted center.
			var localCenter = FVector3.Zero;
			for (var i = 0; i < count; i++) {
				if (!shapeLinks[i].Value.TryUnpack<TWorld>(out var shapeEntity)) {
					continue;
				}

				ref readonly var shape = ref shapeEntity.Read<Shape>();
				if (shape.Density == FP.Zero) {
					continue;
				}

				var massData = shape.ComputeMass();
				masses[i] = massData;
				body.Mass += massData.Mass;
				localCenter += massData.Mass * massData.Center;
			}

			if (body.Mass > FP.Zero) {
				body.InvMass = FP.One / body.Mass;
				localCenter *= body.InvMass;
			}

			// Pass 2: accumulate rotational inertia about the shared center of mass (parallel axis theorem).
			for (var i = 0; i < count; i++) {
				var massData = masses[i];
				if (massData.Mass == FP.Zero) {
					continue;
				}

				var offset = localCenter - massData.Center;
				body.Inertia += massData.Inertia + FMatrix3.Steiner(massData.Mass, offset);
			}

			if (FMatrix3.Determinant(body.Inertia) > FP.Zero) {
				body.InvInertiaLocal = FMatrix3.InvertTranspose(body.Inertia);
				var rotation = FMatrix3.FromQuaternion(body.Transform.Rotation);
				body.InvInertiaWorld = rotation * body.InvInertiaLocal * FMatrix3.Transpose(rotation);
			}

			var oldCenter = body.Center;
			body.LocalCenter = localCenter;
			body.Center = FWorldTransform.TransformPoint(body.Transform, localCenter);

			// Center of mass moved — keep linear velocity consistent for the point that used to be the center.
			body.LinearVelocity += FVector3.Cross(body.AngularVelocity, body.Center - oldCenter);

			if (body.HasFixedRotation) {
				body.Inertia = FMatrix3.Zero;
				body.InvInertiaLocal = FMatrix3.Zero;
				body.InvInertiaWorld = FMatrix3.Zero;
			}
		}
	}
}
