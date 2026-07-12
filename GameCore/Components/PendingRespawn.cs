using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

/// <summary>
/// Marks a standalone timer entity (no <see cref="Body"/>/<see cref="Shape"/> of its own) counting
/// down to respawning a killed <see cref="Dummy"/> at <see cref="SlotIndex"/>. See
/// <see cref="Core{TWorld}.DummyRespawnSystem"/>.
/// </summary>
public struct PendingRespawn : IComponent {
	public int SlotIndex;
	public FP TimeRemaining;
}
