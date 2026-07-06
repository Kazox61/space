using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

public struct ViewId : IComponent, ITrackableAdded, ITrackableDeleted {
	public ViewAsset Value;
}

public enum ViewAsset {
	Player,
	Projectile,
}
