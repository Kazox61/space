using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;

namespace Space.GameCore;

public struct Body : IComponent, IComponentConfig<Body> {
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
	public FVector3 Force;
	public FVector3 Torque;

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
	internal bool EnableStateInitialized;
	public bool IsBullet;
	public bool AllowFastRotation;
	public bool EnableContactRecycling;

	/// <summary>
	/// Surface speed below which the body counts as resting, in length units per second (box3d's
	/// sleepThreshold, default 0.05 m/s). Angular velocity is converted with <see cref="MaxExtent"/>.
	/// </summary>
	public FP SleepThreshold;

	/// <summary>
	/// Seconds the body has continuously rested below <see cref="SleepThreshold"/>. An island falls
	/// asleep once every body in it has rested for <see cref="B3Config.TimeToSleep"/>.
	/// </summary>
	public FP SleepTime { get; internal set; }

	/// <summary>Largest distance from the center of mass to the surface of any owned shape.</summary>
	public FP MaxExtent { get; internal set; }

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

	/// <summary>Box3D's default sleep threshold (0.05 m/s), scaled by the configured length units.</summary>
	public static FP DefaultSleepThreshold => FP.FromRatio(5, 100) * B3Config.GetLengthUnitsPerMeter();

	/// <summary>Whether all rotational axes are locked, meaning the body has effectively fixed rotation.</summary>
	public readonly bool HasFixedRotation => MotionLocks is { AngularX: true, AngularY: true, AngularZ: true };

	public ComponentTypeConfig<Body> Config() => new(
		defaultValue: new Body {
			GravityScale = FP.One,
			EnableSleep = true,
			IsAwake = true,
			IsEnabled = true,
			EnableStateInitialized = true,
			EnableContactRecycling = true,
			SleepThreshold = DefaultSleepThreshold,
			DeltaRotation = FQuaternion.Identity
		}
	);
}
