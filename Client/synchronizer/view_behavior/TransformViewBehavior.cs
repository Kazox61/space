using FFS.Libraries.StaticEcs;
using Fixed64;
using Godot;
using Space.GameCore;

namespace Space.Client;

/// <summary>
/// Places the view at its entity's <see cref="Transform"/>, blended between the previous and the
/// current simulated tick (see <see cref="RenderInterpolation"/>), so movement is smooth at any
/// display rate instead of stepping at the tick rate.
/// </summary>
[GlobalClass]
public partial class TransformViewBehavior : EntityBehavior {
	[Export] private Node3D _targetNodePosition;
	[Export] private Node3D _targetNodeRotation;
	/// <summary>Off for views whose entity never moves, or whose every move is a jump.</summary>
	[Export] private bool _interpolate = true;

	public override void OnEntityAssigned(EntityGID entityGid) {
		OnEntityUpdate(entityGid);
	}

	public override void OnEntityRemoved(EntityGID entityGid) {
	}

	public override void OnEntityUpdate(EntityGID entityGid) {
		if (!TrySamplePose(entityGid, _interpolate, out var pose)) {
			return;
		}

		_targetNodePosition.GlobalPosition = pose.Origin;
		_targetNodeRotation.Quaternion = pose.Basis.GetRotationQuaternion();
	}

	/// <summary>
	/// The pose to draw the entity at this frame. With <paramref name="interpolate"/>, blends from
	/// the entity's pose in the previous-tick world (<see cref="WP"/>) toward the current one by
	/// <see cref="RenderInterpolation.Alpha"/>. Falls back to the current pose when the entity did
	/// not exist a tick ago, or teleported in between (<see cref="Transform.TeleportTick"/> differs).
	/// </summary>
	public static bool TrySamplePose(EntityGID entityGid, bool interpolate, out Transform3D pose) {
		pose = default;
		if (!entityGid.TryUnpack<ClientWorld>(out var entity) || !entity.Has<Transform>()) {
			return false;
		}

		ref readonly var current = ref entity.Read<Transform>();
		pose = ToGodot(current);

		// WP is a snapshot of this same world one tick earlier, so the entity has the same GID there;
		// the version check in TryUnpack rejects a slot reused by a different entity.
		if (interpolate && entityGid.TryUnpack<GameWorldPrev>(out var previousEntity) && previousEntity.Has<Transform>()) {
			ref readonly var previous = ref previousEntity.Read<Transform>();
			if (previous.TeleportTick == current.TeleportTick) {
				pose = ToGodot(previous).InterpolateWith(pose, RenderInterpolation.Alpha);
			}
		}

		return true;
	}

	public static Transform3D ToGodot(in Transform transform) => new(
		new Basis(new Quaternion(
			transform.Rotation.X.ToFloat(),
			transform.Rotation.Y.ToFloat(),
			transform.Rotation.Z.ToFloat(),
			transform.Rotation.W.ToFloat()
		).Normalized()),
		new Vector3(
			transform.Position.X.ToFloat(),
			transform.Position.Y.ToFloat(),
			transform.Position.Z.ToFloat()
		)
	);
}
