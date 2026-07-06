using FFS.Libraries.StaticEcs;

namespace Space.Client;

public struct GameWorldPrev : IWorldType { }

/// <summary>
/// Previous GameWorld state.
/// </summary>
public abstract class WP : World<GameWorldPrev> { }
