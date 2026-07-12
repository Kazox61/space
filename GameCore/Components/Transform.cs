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

	/// <summary>
	/// Mirrors a physics-authoritative <see cref="Body"/>'s <see cref="Body.Transform"/> into this
	/// gameplay-facing transform. Called only by <c>BodyTransformSyncSystem</c> — entities with a
	/// <see cref="Body"/> must not have their <see cref="Transform"/> written anywhere else.
	/// </summary>
	public void SetFromWorldTransform(FWorldTransform t) {
		Position = new FVector3(t.Position.X, t.Position.Y, t.Position.Z);
		Rotation = new FQuaternion(t.Rotation.X.To64(), t.Rotation.Y.To64(), t.Rotation.Z.To64(), t.Rotation.W.To64());
	}
}
