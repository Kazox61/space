using FFS.Libraries.StaticEcs;
using Fixed32;
using Fixed;

namespace Space.GameCore;

/// <summary>
/// A shape entity's geometry/material/filter data. Top-level rather than nested in
/// <c>Core&lt;TWorld&gt;</c>: types nested inside an open generic report
/// <c>IsGenericTypeDefinition == true</c> under plain reflection, which
/// <c>AutoRegistration.RegisterAll</c> explicitly skips — so a nested <c>IComponent</c> silently
/// never registers. Broad-phase operations that need a world-bound <c>W.Entity</c>/<c>BroadPhase</c>
/// live in <see cref="Core{TWorld}.ShapeBroadPhaseOps"/> instead of as methods here.
/// </summary>
public struct Shape : IComponent, IComponentConfig<Shape>, ITrackableAdded {
	public const int NullProxyKey = -1;
	public ShapeType Type { get; internal set; }
	/// <summary>Valid when <see cref="Type"/> is <see cref="ShapeType.Sphere"/>.</summary>
	public Sphere SphereShape;

	/// <summary>Valid when <see cref="Type"/> is <see cref="ShapeType.Capsule"/>.</summary>
	public Capsule CapsuleShape;

	/// <summary>The density, usually in kg/m^3. Defaults to the density of water.</summary>
	public FP Density;

	/// <summary>Explosion scale for explosions, non-dimensional.</summary>
	public FP ExplosionScale;

	/// <summary>The base surface material. Ignored for compound shapes.</summary>
	public SurfaceMaterial Material;

	/// <summary>Contact filtering data.</summary>
	public Filter Filter;

	/// <summary>Use this to store application specific shape data.</summary>
	public object? UserData;

	public string? Name;

	/// <summary>
	/// A sensor shape generates overlap events but never generates a collision response. Sensors do
	/// not have continuous collision; use a ray or shape cast instead. Sensors still contribute to
	/// body mass if they have non-zero density.
	/// </summary>
	public bool IsSensor;

	/// <summary>Enable sensor events for this shape. Applies to sensors and non-sensors.</summary>
	public bool EnableSensorEvents;

	/// <summary>Enable contact events for this shape. Only applies to kinematic and dynamic bodies. Ignored for sensors.</summary>
	public bool EnableContactEvents;

	/// <summary>Enable custom filtering. Only one of the two shapes needs to enable custom filtering.</summary>
	public bool EnableCustomFiltering;

	/// <summary>Enable hit events for this shape. Only applies to kinematic and dynamic bodies. Ignored for sensors.</summary>
	public bool EnableHitEvents;

	/// <summary>
	/// Enable pre-solve contact events for this shape. Only applies to dynamic bodies. Expensive and
	/// must be carefully handled due to multithreading. Ignored for sensors.
	/// </summary>
	public bool EnablePreSolveEvents;

	/// <summary>Local centroid, computed at creation time.</summary>
	public FVector3 LocalCentroid { get; internal set; }

	/// <summary>Per-shape AABB margin (a fraction of shape extent, capped by <see cref="B3Config.MaxAabbMargin"/>).</summary>
	public FP AabbMargin { get; internal set; }

	/// <summary>The tight AABB with a small speculative margin. Updated whenever the body moves.</summary>
	public FAABB Aabb;

	/// <summary>The fattened AABB used as the broad-phase proxy bounds.</summary>
	public FAABB FatAabb;

	/// <summary>The broad-phase proxy key, or <see cref="NullProxyKey"/> if this shape has no proxy.</summary>
	public int ProxyKey { get; internal set; }

	/// <summary>Compute mass properties for this shape's geometry at its own density.</summary>
	public MassData ComputeMass() {
		return Type switch {
			ShapeType.Sphere => Sphere.ComputeMass(SphereShape, Density),
			ShapeType.Capsule => Capsule.ComputeMass(CapsuleShape, Density),
			_ => default,
		};
	}

	/// <summary>Compute the local-space centroid of this shape's geometry.</summary>
	public FVector3 ComputeCentroid() {
		return Type switch {
			ShapeType.Sphere => SphereShape.Center,
			ShapeType.Capsule => FP.Half * (CapsuleShape.Center1 + CapsuleShape.Center2),
			_ => FVector3.Zero,
		};
	}

	/// <summary>
	/// Per-shape AABB margin: a fraction of the shape's own extent (capped), matching box3d's
	/// b3ComputeShapeMargin. Small shapes get small margins; large shapes are capped.
	/// </summary>
	public FP ComputeMargin() {
		FP margin;
		switch (Type) {
			case ShapeType.Sphere:
				margin = SphereShape.Radius;
				break;

			case ShapeType.Capsule:
				margin = FP.Half * FVector3.Distance(CapsuleShape.Center2, CapsuleShape.Center1) + CapsuleShape.Radius;
				break;

			default:
				return B3Config.MaxAabbMargin;
		}

		return FP.Min(B3Config.MaxAabbMargin, B3Config.AabbMarginFraction * margin);
	}

	/// <summary>Compute the (tight) AABB of this shape under a local transform.</summary>
	public FAABB ComputeAABB(FTransform transform) {
		return Type switch {
			ShapeType.Sphere => Sphere.ComputeAABB(SphereShape, transform),
			ShapeType.Capsule => Capsule.ComputeAABB(CapsuleShape, transform),
			_ => new FAABB(transform.Position, transform.Position),
		};
	}

	/// <summary>
	/// Conservative world AABB for this shape inflated by extra margin, built from the body's world
	/// transform (rotation + <see cref="FPos"/> translation).
	/// </summary>
	public FAABB ComputeFatAABB(FWorldTransform transform, FP extra) {
		var r = new FVector3(extra, extra, extra);

		var rotationOnly = new FTransform(FVector3.Zero, transform.Rotation);
		var localBox = ComputeAABB(rotationOnly);
		localBox = new FAABB(localBox.LowerBound - r, localBox.UpperBound + r);
		return FWorldTransform.OffsetAABB(localBox, transform.Position);
	}

	/// <summary>Build a generic point-cloud proxy for this shape, for GJK-based queries.</summary>
	public ShapeProxy MakeProxy() {
		return Type switch {
			ShapeType.Sphere => new ShapeProxy { Points = new[] { SphereShape.Center }, Radius = SphereShape.Radius },
			ShapeType.Capsule => new ShapeProxy { Points = new[] { CapsuleShape.Center1, CapsuleShape.Center2 }, Radius = CapsuleShape.Radius },
			_ => new ShapeProxy { Points = Array.Empty<FVector3>(), Radius = FP.Zero },
		};
	}

	/// <summary>
	/// Surface materials used on mesh shapes per triangle. Ignored for convex shapes and compound shapes.
	/// </summary>
	public SurfaceMaterial[]? Materials;

	/// <summary>
	/// When shapes are created they scan the environment for collision the next time step. This can
	/// significantly slow down static body creation when there are many static shapes. Ignored for
	/// dynamic and kinematic shapes, which always invoke contact creation.
	/// </summary>
	public bool InvokeContactCreation;

	/// <summary>Should the body update its mass properties when this shape is created.</summary>
	public bool UpdateBodyMass;

	private static FP Cube(FP value) => value * value * value;

	private static Shape DefaultTemplate => new() {
		Material = SurfaceMaterial.Default,
		Density = FP.FromRatio(1000, 1) / Cube(B3Config.GetLengthUnitsPerMeter()),
		ExplosionScale = FP.One,
		Filter = Filter.Default,
		InvokeContactCreation = true,
		UpdateBodyMass = true,
		ProxyKey = NullProxyKey
	};

	public ComponentTypeConfig<Shape> Config() => new(defaultValue: DefaultTemplate);

	/// <summary>Builds a sphere shape with default material/filter/density, ready for <c>ShapeFactory.CreateShape</c>.</summary>
	public static Shape MakeSphere(FVector3 center, FP radius) {
		var shape = DefaultTemplate;
		shape.Type = ShapeType.Sphere;
		shape.SphereShape = new Sphere(center, radius);
		return shape;
	}

	/// <summary>Builds a capsule shape with default material/filter/density, ready for <c>ShapeFactory.CreateShape</c>.</summary>
	public static Shape MakeCapsule(FVector3 center1, FVector3 center2, FP radius) {
		var shape = DefaultTemplate;
		shape.Type = ShapeType.Capsule;
		shape.CapsuleShape = new Capsule(center1, center2, radius);
		return shape;
	}
}
