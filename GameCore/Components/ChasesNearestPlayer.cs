using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>Points a <see cref="NavAgent"/> at the nearest player; see <see cref="Core{TWorld}.NavChaseSystem"/>.</summary>
public struct ChasesNearestPlayer : ITag { }
