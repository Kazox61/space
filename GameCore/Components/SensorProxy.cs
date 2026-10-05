using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>
/// A kinematic body with a sensor capsule that follows a character, so characters (which have no
/// <see cref="Body"/>) take part in sensor contacts; see <see cref="Core{TWorld}.SensorProxySystem"/>.
/// The capsule is a sensor, so the character mover, the solver and queries all ignore it.
/// </summary>
public struct SensorProxy : IComponent {
	/// <summary>The character the proxy follows; the proxy is destroyed once it is gone.</summary>
	public EntityGID Owner;
}

/// <summary>Marks a character whose <see cref="SensorProxy"/> has been created.</summary>
public struct HasSensorProxy : ITag { }
