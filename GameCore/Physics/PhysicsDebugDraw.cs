using System;
using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

/// <summary>Semantic colors for <see cref="IPhysicsDebugDraw"/>; the renderer picks the actual palette.</summary>
public enum PhysicsDebugColor : byte {
	StaticShape,
	KinematicShape,
	AwakeShape,
	SleepingShape,
	DisabledShape,
	SensorShape,
	Aabb,
	Proxy,
	ContactTouching,
	ContactSpeculative,
	ContactNormal,
}

/// <summary>What <c>PhysicsDebugDraw.Draw</c> emits.</summary>
[Flags]
public enum PhysicsDebugDrawFlags {
	None = 0,
	/// <summary>Shape wireframes, colored by body type and awake/sleeping/disabled state.</summary>
	Shapes = 1 << 0,
	/// <summary>Tight shape AABBs, as last refreshed by the proxy system.</summary>
	Aabbs = 1 << 1,
	/// <summary>Broad-phase proxies (the enlarged AABBs stored in the dynamic trees).</summary>
	Proxies = 1 << 2,
	/// <summary>Manifold points, colored touching versus speculative.</summary>
	Contacts = 1 << 3,
	/// <summary>Manifold normals, from shape A toward shape B.</summary>
	ContactNormals = 1 << 4,
	Default = Shapes | Contacts | ContactNormals,
	All = Shapes | Aabbs | Proxies | Contacts | ContactNormals,
}

/// <summary>
/// Renderer callbacks for physics debug drawing (box3d's b3DebugDraw). Everything is in world space;
/// AABBs are in the broad phase's absolute Fixed32 frame. Implementations convert to engine types.
/// </summary>
public interface IPhysicsDebugDraw {
	void DrawSphere(FPos center, FP radius, PhysicsDebugColor color);
	void DrawCapsule(FPos center1, FPos center2, FP radius, PhysicsDebugColor color);
	void DrawBox(FWorldTransform transform, FVector3 halfExtents, PhysicsDebugColor color);
	void DrawAabb(FAABB aabb, PhysicsDebugColor color);
	void DrawSegment(FPos start, FPos end, PhysicsDebugColor color);
	void DrawPoint(FPos point, PhysicsDebugColor color);
}

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Walks the physics world and reports shapes, AABBs, broad-phase proxies, contacts, and sleep
	/// state to an <see cref="IPhysicsDebugDraw"/>. Read-only: safe to call between ticks from a
	/// renderer, and never part of the simulation.
	/// </summary>
	public static class PhysicsDebugDraw {
		public static void Draw(IPhysicsDebugDraw draw, PhysicsDebugDrawFlags flags = PhysicsDebugDrawFlags.Default) {
			if ((flags & (PhysicsDebugDrawFlags.Shapes | PhysicsDebugDrawFlags.Aabbs | PhysicsDebugDrawFlags.Proxies)) != 0) {
				DrawShapes(draw, flags);
			}
			if ((flags & (PhysicsDebugDrawFlags.Contacts | PhysicsDebugDrawFlags.ContactNormals)) != 0) {
				DrawContacts(draw, flags);
			}
		}

		private static void DrawShapes(IPhysicsDebugDraw draw, PhysicsDebugDrawFlags flags) {
			foreach (var shapeEntity in W.Query<All<Shape, W.Link<BodyOwner>>>().Entities()) {
				ref readonly var shape = ref shapeEntity.Read<Shape>();
				if (!shapeEntity.Read<W.Link<BodyOwner>>().Value.TryUnpack<TWorld>(out var bodyEntity) || !bodyEntity.Has<Body>()) {
					continue;
				}
				ref readonly var body = ref bodyEntity.Read<Body>();

				if ((flags & PhysicsDebugDrawFlags.Shapes) != 0) {
					DrawShape(draw, shape, body.Transform, ShapeColor(shape, body));
				}
				if ((flags & PhysicsDebugDrawFlags.Aabbs) != 0 && shape.ProxyKey != Shape.NullProxyKey) {
					draw.DrawAabb(shape.Aabb, PhysicsDebugColor.Aabb);
				}
				if ((flags & PhysicsDebugDrawFlags.Proxies) != 0 && shape.ProxyKey != Shape.NullProxyKey) {
					draw.DrawAabb(shape.FatAabb, PhysicsDebugColor.Proxy);
				}
			}
		}

		private static PhysicsDebugColor ShapeColor(in Shape shape, in Body body) {
			if (!BodyOperations.IsEnabled(body)) {
				return PhysicsDebugColor.DisabledShape;
			}
			if (shape.IsSensor) {
				return PhysicsDebugColor.SensorShape;
			}
			if (body.Type == BodyType.Static) {
				return PhysicsDebugColor.StaticShape;
			}
			if (!PhysicsSleep.IsAwake(body)) {
				return PhysicsDebugColor.SleepingShape;
			}
			return body.Type == BodyType.Kinematic ? PhysicsDebugColor.KinematicShape : PhysicsDebugColor.AwakeShape;
		}

		private static void DrawShape(IPhysicsDebugDraw draw, in Shape shape, FWorldTransform bodyTransform, PhysicsDebugColor color) {
			switch (shape.Type) {
				case ShapeType.Sphere:
					draw.DrawSphere(FWorldTransform.TransformPoint(bodyTransform, shape.SphereShape.Center), shape.SphereShape.Radius, color);
					break;
				case ShapeType.Capsule:
					draw.DrawCapsule(
						FWorldTransform.TransformPoint(bodyTransform, shape.CapsuleShape.Center1),
						FWorldTransform.TransformPoint(bodyTransform, shape.CapsuleShape.Center2),
						shape.CapsuleShape.Radius, color);
					break;
				case ShapeType.Hull:
					draw.DrawBox(
						new FWorldTransform(
							FWorldTransform.TransformPoint(bodyTransform, shape.HullShape.Center),
							FQuaternion.Normalize(bodyTransform.Rotation * shape.HullShape.Rotation)),
						shape.HullShape.HalfExtents, color);
					break;
			}
		}

		private static void DrawContacts(IPhysicsDebugDraw draw, PhysicsDebugDrawFlags flags) {
			var normalLength = FP.Half * B3Config.GetLengthUnitsPerMeter();
			foreach (var contactEntity in W.Query<All<Contact>>().Entities()) {
				ref readonly var contact = ref contactEntity.Read<Contact>();
				ref readonly var manifold = ref contact.Manifold;
				if (manifold.PointCount == 0 || !PhysicsSleep.TryGetBodyOfShape(contact.ShapeA, out var bodyA)) {
					continue;
				}
				var transformA = bodyA.Read<Body>()!.Transform; // TryGetBodyOfShape only resolves entities with Body.
				var normal = transformA.Rotation * manifold.Normal;
				for (var i = 0; i < manifold.PointCount; i++) {
					var point = manifold.GetPoint(i);
					var worldPoint = FWorldTransform.TransformPoint(transformA, point.Point);
					if ((flags & PhysicsDebugDrawFlags.Contacts) != 0) {
						draw.DrawPoint(worldPoint, point.Separation <= FP.Zero ? PhysicsDebugColor.ContactTouching : PhysicsDebugColor.ContactSpeculative);
					}
					if ((flags & PhysicsDebugDrawFlags.ContactNormals) != 0) {
						draw.DrawSegment(worldPoint, worldPoint + normalLength * normal, PhysicsDebugColor.ContactNormal);
					}
				}
			}
		}
	}
}
