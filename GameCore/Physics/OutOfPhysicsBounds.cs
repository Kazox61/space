using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>
/// Set by <see cref="Core{TWorld}.ContactSolverSystem"/> on a body whose origin crossed
/// <see cref="Core{TWorld}.PhysicsValidation.EscapeCoordinate"/>. The body is disabled in the same step;
/// gameplay decides whether to destroy, respawn, or move it back and re-enable it (which clears the tag).
/// </summary>
public struct OutOfPhysicsBounds : ITag { }
