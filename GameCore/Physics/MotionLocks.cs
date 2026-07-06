namespace Space.GameCore;

/// <summary>
/// Motion locks to restrict body movement.
/// </summary>
public struct MotionLocks {
	/// <summary>Prevent translation along the x-axis.</summary>
	public bool LinearX;

	/// <summary>Prevent translation along the y-axis.</summary>
	public bool LinearY;

	/// <summary>Prevent translation along the z-axis.</summary>
	public bool LinearZ;

	/// <summary>Prevent rotation around the x-axis.</summary>
	public bool AngularX;

	/// <summary>Prevent rotation around the y-axis.</summary>
	public bool AngularY;

	/// <summary>Prevent rotation around the z-axis.</summary>
	public bool AngularZ;
}
