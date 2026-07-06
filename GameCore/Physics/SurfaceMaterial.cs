using Fixed32;

namespace Space.GameCore;

/// <summary>
/// Material properties supported per triangle on meshes and height fields.
/// </summary>
public struct SurfaceMaterial {
	/// <summary>The Coulomb (dry) friction coefficient, usually in the range [0,1].</summary>
	public FP Friction;

	/// <summary>
	/// The coefficient of restitution (bounce) usually in the range [0,1].
	/// https://en.wikipedia.org/wiki/Coefficient_of_restitution
	/// </summary>
	public FP Restitution;

	/// <summary>The rolling resistance usually in the range [0,1]. Only used for spheres and capsules.</summary>
	public FP RollingResistance;

	/// <summary>
	/// The tangent velocity for conveyor belts. Local to the shape and projected onto the contact surface.
	/// </summary>
	public FVector3 TangentVelocity;

	/// <summary>
	/// User material identifier, passed with query results and to friction/restitution combining
	/// functions. Not used internally.
	/// </summary>
	public ulong UserMaterialId;

	/// <summary>Custom debug draw color. Ignored if 0.</summary>
	public uint CustomColor;

	public static SurfaceMaterial Default => new() {
		Friction = FP.FromRatio(6, 10),
		Restitution = FP.Zero,
		RollingResistance = FP.Zero,
		TangentVelocity = FVector3.Zero,
		UserMaterialId = 0,
		CustomColor = 0,
	};
}
