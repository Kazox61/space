using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

public struct ViewId : IComponent, ITrackableAdded, ITrackableDeleted {
	public ViewAsset Value;
}

public enum ViewAsset {
	Player = 0,
	Projectile = 1,
	Sphere = 2,
	Platform = 3,
	Box = 4,
	Dummy = 5,
	Crate = 6,
}
