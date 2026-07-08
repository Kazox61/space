using FFS.Libraries.StaticEcs;
using Fixed64;
using Fixed;

namespace Space.GameCore;

public struct Transform : IComponent, IComponentConfig<Transform>, ITrackableChanged {
	public FVector3 Position;
	public FQuaternion Rotation;

	public ComponentTypeConfig<Transform> Config() => new(
		defaultValue: new Transform { Rotation = FQuaternion.Identity }
	);

	/// <summary>Narrows this 64-bit gameplay transform down to the 32-bit-rotation <see cref="FWorldTransform"/> that <see cref="Body"/> uses.</summary>
	public FWorldTransform ToWorldTransform() => new(
		new FPos(Position.X, Position.Y, Position.Z),
		new Fixed32.FQuaternion(Rotation.X.To32(), Rotation.Y.To32(), Rotation.Z.To32(), Rotation.W.To32())
	);
}
