using Godot;

namespace Space.Client;

[GlobalClass]
public partial class PlayerSkin : Node3D {
	[Signal]
	public delegate void FootstepEventHandler(float intensity);

	private static readonly StringName s_idleState = "idle";
	private static readonly StringName s_runState = "run";

	[Export] private AnimationPlayer _animationPlayer;
	[Export] private AnimationTree _animationTree;
	[Export] private AnimationLibrary _eventAnimations;
	[Export] private Node3D _squashTarget;
	[Export] private string _attackRequestParameter = "";
	[Export(PropertyHint.Range, "-15,15,0.5")] private float _aimYawOffsetDegrees;

	public float AimYawOffsetRadians => Mathf.DegToRad(_aimYawOffsetDegrees);

	private AnimationNodeStateMachinePlayback _stateMachine;
	private Tween _squashTween;
	private float _tilt;

	public override void _Ready() {
		if (_eventAnimations is not null) {
			_animationPlayer.AddAnimationLibrary("events", _eventAnimations);
		}
		SetLooping(s_idleState);
		SetLooping(s_runState);
		_stateMachine = _animationTree
			.Get("parameters/StateMachine/playback")
			.As<AnimationNodeStateMachinePlayback>();
		ResetPresentation();
	}

	public void SetState(StringName state) {
		_stateMachine?.Travel(state);
	}

	public void SetTilt(float value) {
		_tilt = Mathf.Clamp(value, -1.0f, 1.0f);
		_animationTree?.Set("parameters/AddTilt/add_amount", Mathf.Abs(_tilt));
		_animationTree?.Set("parameters/TiltAmount/blend_position", _tilt);
	}

	public void PlaySquash(float scaleY, float duration) {
		if (_squashTarget is null) {
			return;
		}

		_squashTween?.Kill();
		_squashTween = CreateTween().SetEase(Tween.EaseType.Out);
		_squashTween.TweenMethod(Callable.From<float>(SetSquash), 1.0f, scaleY, duration);
		_squashTween.TweenMethod(Callable.From<float>(SetSquash), scaleY, 1.0f, duration * 1.8f);
	}

	public void PlayAttack() {
		if (_attackRequestParameter.Length > 0) {
			_animationTree?.Set(_attackRequestParameter, (int)AnimationNodeOneShot.OneShotRequest.Fire);
		}
	}

	public void ResetPresentation() {
		_squashTween?.Kill();
		_squashTween = null;
		SetSquash(1.0f);
		SetTilt(0.0f);
		if (_attackRequestParameter.Length > 0) {
			_animationTree?.Set(_attackRequestParameter, (int)AnimationNodeOneShot.OneShotRequest.Abort);
		}
		if (_animationTree is not null) {
			_animationTree.Active = false;
			_animationTree.Active = true;
		}
		_stateMachine?.Start(s_idleState, true);
	}

	// Animation method tracks call this exact GDScript-style method name.
	public void emit_footstep() {
		EmitSignal(SignalName.Footstep, 1.0f);
	}

	private void SetSquash(float scaleY) {
		if (_squashTarget is null) {
			return;
		}

		var scaleXZ = 2.0f - scaleY;
		_squashTarget.Scale = new Vector3(scaleXZ, scaleY, scaleXZ);
	}

	private void SetLooping(StringName animationName) {
		var animation = _animationPlayer?.GetAnimation(animationName);
		if (animation is not null) {
			animation.LoopMode = Animation.LoopModeEnum.Linear;
		}
	}
}
