using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>
/// Which position in the patrol row a <see cref="Dummy"/> occupies -- lets
/// <see cref="Core{TWorld}.DummyRespawnSystem"/> recreate an identical replacement at the same rail
/// slot after it dies, via <c>Core&lt;TWorld&gt;.SpawnDummySystem.SpawnAt</c>.
/// </summary>
public struct RailSlot : IComponent {
	public int Index;
}
