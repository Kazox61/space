using Godot;

namespace Space.Client;

public partial class GodotPlushSkin : Node3D {
	[Signal]
	public delegate void FootstepEventHandler(float intensity);

	private static readonly StringName s_idleState = "idle";

	private AnimationTree _animationTree;
	private MeshInstance3D _mesh;
	private AnimationNodeStateMachinePlayback _stateMachine;
	private Tween _squashTween;
	private float _tilt;

	public override void _Ready() {
		_animationTree = GetNode<AnimationTree>("AnimationTree");
		_mesh = GetNode<MeshInstance3D>("GodotPlushModel/Rig/Skeleton3D/GodotPlushMesh");
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
		if (_mesh is null) {
			return;
		}

		_squashTween?.Kill();
		_squashTween = CreateTween().SetEase(Tween.EaseType.Out);
		_squashTween.TweenMethod(Callable.From<float>(SetSquash), 1.0f, scaleY, duration);
		_squashTween.TweenMethod(Callable.From<float>(SetSquash), scaleY, 1.0f, duration * 1.8f);
	}

	public void ResetPresentation() {
		_squashTween?.Kill();
		_squashTween = null;
		SetSquash(1.0f);
		SetTilt(0.0f);
		if (_animationTree is not null) {
			_animationTree.Active = false;
			_animationTree.Active = true;
		}
		_stateMachine?.Start(s_idleState, true);
	}

	// Imported animation method tracks call this exact GDScript-style method name.
	public void emit_footstep() {
		EmitSignal(SignalName.Footstep, 1.0f);
	}

	private void SetSquash(float scaleY) {
		if (_mesh is null) {
			return;
		}

		var scaleXZ = 2.0f - scaleY;
		_mesh.Scale = new Vector3(scaleXZ, scaleY, scaleXZ);
	}
}
