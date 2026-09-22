using FFS.Libraries.StaticEcs;
using Fixed32;
using Fixed64;
using Godot;
using Shenanicode.Rollback;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

[GlobalClass]
public partial class PlayerPresentationBehavior : EntityBehavior {
	private static readonly StringName s_idleState = "idle";
	private static readonly StringName s_runState = "run";
	private static readonly StringName s_jumpState = "jump";
	private static readonly StringName s_fallState = "fall";

	[Export] private Node3D _visualRoot;
	[Export] private GodotPlushSkin _skin;
	[Export] private AudioStreamPlayer3D _footstepAudio;
	[Export] private AudioStreamPlayer3D _impactAudio;
	[Export] private PackedScene _jumpParticlesScene;
	[Export] private PackedScene _landParticlesScene;

	private bool _hasPreviousState;
	private bool _isAssigned;
	private bool _wasGrounded;
	private float _previousVerticalVelocity;
	private float _targetYaw;
	private float _tilt;
	private StringName _currentState;

	public override void _Ready() {
		_skin.Footstep += OnFootstep;
	}

	public override void OnEntityAssigned(EntityGID entityGid) {
		ResetPresentation();
		_isAssigned = true;
		UpdateFromEntity(entityGid, false);
	}

	public override void OnEntityRemoved(EntityGID entityGid) {
		_isAssigned = false;
		ResetPresentation();
	}

	public override void OnEntityUpdate(EntityGID entityGid) {
		UpdateFromEntity(entityGid, true);
	}

	private void UpdateFromEntity(EntityGID entityGid, bool playTransitions) {
		if (!entityGid.TryUnpack<ClientWorld>(out var entity) || !entity.Has<Mover>() || !entity.Has<PlayerInfo>()) {
			return;
		}

		ref readonly var mover = ref entity.Read<Mover>();
		ref readonly var playerInfo = ref entity.Read<PlayerInfo>();
		var input = S.GetInput<PlayerInput>(channel: playerInfo.InputChannel).LastFresh();
		var velocity = new Vector3(
			mover.Velocity.X.ToFloat(),
			mover.Velocity.Y.ToFloat(),
			mover.Velocity.Z.ToFloat()
		);
		var movementInput = new Vector2(input.MoveX.ToFloat(), input.MoveY.ToFloat());
		var isMoving = movementInput.LengthSquared() > 0.01f;

		if (isMoving) {
			_targetYaw = -movementInput.Orthogonal().Angle();
		}

		var delta = (float)GetProcessDeltaTime();
		var rotation = _visualRoot.Rotation;
		rotation.Y = Mathf.LerpAngle(rotation.Y, _targetYaw, Mathf.Clamp(6.0f * delta, 0.0f, 1.0f));
		_visualRoot.Rotation = rotation;

		var angleDifference = Mathf.AngleDifference(rotation.Y, _targetYaw);
		_tilt = Mathf.MoveToward(_tilt, angleDifference, 2.0f * delta);
		_skin.SetTilt(_tilt);

		var state = mover.Grounded
			? (isMoving ? s_runState : s_idleState)
			: (velocity.Y > 0.05f ? s_jumpState : s_fallState);
		if (_currentState != state) {
			_currentState = state;
			_skin.SetState(state);
		}

		if (playTransitions && _hasPreviousState) {
			if (_wasGrounded && !mover.Grounded && velocity.Y > 0.05f) {
				SpawnParticles(_jumpParticlesScene);
				_skin.PlaySquash(1.2f, 0.1f);
			} else if (!_wasGrounded && mover.Grounded && _previousVerticalVelocity < -0.05f) {
				PlayLandingFeedback(_previousVerticalVelocity);
			}
		}

		_wasGrounded = mover.Grounded;
		_previousVerticalVelocity = velocity.Y;
		_hasPreviousState = true;
	}

	private void PlayLandingFeedback(float verticalVelocity) {
		var intensity = Mathf.Remap(Mathf.Clamp(Mathf.Abs(verticalVelocity), 0.0f, 12.0f), 0.0f, 12.0f, 0.5f, 1.0f);
		_impactAudio.VolumeDb = Mathf.LinearToDb(intensity);
		_impactAudio.Play();
		SpawnParticles(_landParticlesScene);
		_skin.PlaySquash(0.7f, 0.08f);
	}

	private void SpawnParticles(PackedScene scene) {
		if (scene?.Instantiate() is not GpuParticles3D particles) {
			return;
		}

		var world = _visualRoot.GetParent()?.GetParent();
		if (world is null) {
			particles.QueueFree();
			return;
		}

		world.AddChild(particles);
		particles.GlobalTransform = _visualRoot.GlobalTransform;
	}

	private void OnFootstep(float intensity) {
		if (!_isAssigned) {
			return;
		}

		_footstepAudio.VolumeDb = Mathf.LinearToDb(Mathf.Clamp(intensity, 0.01f, 1.0f));
		_footstepAudio.Play();
	}

	private void ResetPresentation() {
		_hasPreviousState = false;
		_wasGrounded = false;
		_previousVerticalVelocity = 0.0f;
		_targetYaw = 0.0f;
		_tilt = 0.0f;
		_currentState = default;
		_visualRoot.Rotation = Vector3.Zero;
		_footstepAudio.Stop();
		_impactAudio.Stop();
		_skin.ResetPresentation();
	}
}
