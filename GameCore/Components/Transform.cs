using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

public struct Transform : IComponent, IComponentConfig<Transform>, ITrackableChanged {
	public FVector3 Position;
	public FQuaternion Rotation;

	public ComponentTypeConfig<Transform> Config() => new(
		defaultValue: new Transform { Rotation = FQuaternion.Identity }
	);
}
