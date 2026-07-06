using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>
/// Link from a contact entity to the first shape entity in the pair. No hooks — unidirectional.
/// Top-level (not nested in <c>Core&lt;TWorld&gt;</c>) — see <see cref="Shape"/>'s remarks.
/// </summary>
public struct ShapeA : ILinkType { }

/// <summary>Link from a contact entity to the second shape entity in the pair. No hooks — unidirectional.</summary>
public struct ShapeB : ILinkType { }
