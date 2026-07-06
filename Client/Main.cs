using Godot;
using System;
using FFS.Libraries.StaticEcs;
using Fixed64;
using Space.GameCore;


namespace Space.Client;

public partial class Main : Node3D {
	/*

	private FVector2 _attackInput;
	private bool _inputConsumed;

	public override void _EnterTree() {
		W.Create();

		W.Types().RegisterAll();

		W.Initialize();

		W.SetResource(new TimeRes());

		W.NewEntity<Player>();

		Systems.Create();
		Systems.SetResource(new PhysicsRes());
		Systems.SetResource(new CharacterRes());
		Systems.SetResource(new InputRes());
		Systems.SetResource(new PlanetRes());
		Systems.Add(new DamageSystem(), order: 0);
		Systems.Add(new DeathSystem(), order: 1);
		Systems.Add(new MovementSystem(), order: 2);
		Systems.Add(new ProjectileMovementSystem(), order: 3);
		Systems.Initialize();
		SynchronizeSys.Create();
		SynchronizeSys.Add(new ViewSynchronizeSystem());
		SynchronizeSys.Initialize();
	}

	public override void _Process(double delta) {
		ref var inputRes = ref Systems.GetResource<InputRes>();
		var moveInput = Input.GetVector("move_left", "move_right", "move_forward", "move_backward").Normalized();
		inputRes.MoveInput = new FVector2(moveInput.X.ToFP(), moveInput.Y.ToFP());

		if (_inputConsumed) {
			inputRes.Jumped = Input.IsActionJustPressed("jump");
			_attackInput = FVector2.Zero;
		}

		inputRes.AttackInput = _attackInput;

		ref var timeRes = ref W.GetResource<TimeRes>();
		var deltaTime = delta.ToFP();
		timeRes.TotalTime += deltaTime;
		timeRes.Delta = deltaTime;

		timeRes.Accumulator += deltaTime;
		while (timeRes.Accumulator > timeRes.FixedStep) {
			Systems.Update();
			timeRes.Accumulator -= timeRes.FixedStep;
			_inputConsumed = true;
		}

		SynchronizeSys.Update();

		foreach (var entity in W.Query().Entities()) {
			//GD.Print(entity.PrettyString);
		}

		W.Tick();
	}

	public override void _ExitTree() {
		Systems.Destroy();
		W.Destroy();
	}

	public void OnAttack(Vector2 attackInput) {
		_attackInput = new FVector2(attackInput.X.ToFP(), attackInput.Y.ToFP());
		_inputConsumed = false;
	}

	*/
}
