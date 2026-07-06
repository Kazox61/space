using FFS.Libraries.StaticEcs;
using Fixed32;
using Fixed;

namespace Space.GameCore;

public struct Body : IComponent, IComponentConfig<Body>, ITrackableChanged {
	public ushort Generation { get; internal set; }
	public BodyType Type { get; set; }

	/// <summary>World transform of the body origin (not the center of mass).</summary>
	public FWorldTransform Transform;

	/// <summary>Center of mass position in world space.</summary>
	public FPos Center;

	/// <summary>Location of the center of mass relative to the body origin, in local space.</summary>
	public FVector3 LocalCenter;

	public FVector3 LinearVelocity;
	public FVector3 AngularVelocity;

	public FP Mass { get; internal set; }
	public FP InvMass { get; internal set; }

	/// <summary>Rotational inertia about the center of mass, in local space.</summary>
	public FMatrix3 Inertia { get; internal set; }

	public FMatrix3 InvInertiaLocal { get; internal set; }
	public FMatrix3 InvInertiaWorld { get; internal set; }

	public FP LinearDamping;
	public FP AngularDamping;
	public FP GravityScale;

	public MotionLocks MotionLocks;

	public bool EnableSleep;
	public bool IsAwake;
	public bool IsEnabled;
	public bool IsBullet;
	public bool AllowFastRotation;
	public bool EnableContactRecycling;

	public FP SleepThreshold;

	public string? Name;
	public object? UserData;

	/// <summary>
	/// Solver working state: translation accumulated since the start of the current step, in world
	/// orientation. Reset to zero by <see cref="ContactSolver"/> at the start of a step and applied
	/// to <see cref="Transform"/> at the end. Kept on Body directly (rather than a separate
	/// BodyState) since this port has no BodySim/BodyState split, but the delta-position/rotation
	/// pattern itself is load-bearing for the sub-stepping math, not just a SoA artifact - see
	/// contact_solver.c's separation formula.
	/// </summary>
	internal FVector3 DeltaPosition;

	/// <summary>Rotation accumulated since the start of the current step.</summary>
	internal FQuaternion DeltaRotation;

	/// <summary>Whether all rotational axes are locked, meaning the body has effectively fixed rotation.</summary>
	public readonly bool HasFixedRotation => MotionLocks is { AngularX: true, AngularY: true, AngularZ: true };

	public ComponentTypeConfig<Body> Config() => new(
		defaultValue: new Body {
			GravityScale = FP.One,
			EnableSleep = true,
			IsAwake = true,
			IsEnabled = true,
			EnableContactRecycling = true,
			DeltaRotation = FQuaternion.Identity
		}
	);
}
