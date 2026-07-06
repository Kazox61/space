using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

public struct PlayerInfo : IComponent {
	public Guid PlayerGuid;
	public ushort InputChannel;
}
