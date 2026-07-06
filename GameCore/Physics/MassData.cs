using Fixed32;

namespace Space.GameCore;

/// <summary>
/// Mass properties computed for a shape.
/// </summary>
public struct MassData {
	/// <summary>The shape mass.</summary>
	public FP Mass;

	/// <summary>The local center of mass position.</summary>
	public FVector3 Center;

	/// <summary>The inertia tensor about the shape center of mass.</summary>
	public FMatrix3 Inertia;
}
