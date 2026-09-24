using Fixed64;
using Godot;
using Shenanicode.Rollback;
using Shenanicode.Rollback.LiteNetLib;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

public partial class ClientGame : Node3D {
	[Export] private EntityViewCatalog _viewCatalog;
	[Export] private FxPlayer _fxPlayer;

	private float _clientTime;
	private FVector2 _attackInput;
	private bool _inputConsumed;
	private EntityViewUpdater _viewUpdater;
	private bool _pendingJump;

	public override void _EnterTree() {
		var (host, port, offline) = ParseLaunchArgs();
		if (offline && !OfflineServer.TryStart(port)) {
			GD.Print($"Port {port} is occupied — connecting to an external server instead.");
		}

		var connection = new LiteNetLibServerConnection();
		ClientSetup.CreateAndInitialize(connection);
		connection.Connect(host, port);

		_viewUpdater = new EntityViewUpdater();
		AddChild(_viewUpdater);
		_viewUpdater.Initialize(_viewCatalog);

		AddChild(new PhysicsDebugView());
	}

	public override void _ExitTree() {
		_viewUpdater?.Cleanup();
		ClientSetup.Destroy();
		OfflineServer.Destroy();
	}

	public override void _Process(double delta) {
		OfflineServer.Update(delta);
		_clientTime += (float)delta;
		CLNT.Update(_clientTime);
		// After the update, so every tick it (re-)simulated has reported its effects and the head
		// tick for the late check is known.
		ClientSetup.FxLog.LocalChannel = CLNT.Channel;
		ClientSetup.FxLog.Flush(S.CurrentTick, _fxPlayer);
		var moveInput = Input.GetVector("move_left", "move_right", "move_forward", "move_backward").Normalized();
		var sendingAttack = !_inputConsumed;
		var jumping = Input.IsActionJustPressed("jump") || _pendingJump;
		var playerInput = new PlayerInput {
			MoveX = moveInput.X.ToFP(),
			MoveY = moveInput.Y.ToFP(),
			AttackX = _inputConsumed ? FP.Zero : _attackInput.X,
			AttackY = _inputConsumed ? FP.Zero : _attackInput.Y,
			Jump = jumping
		};
		// SetPrediction silently discards the data of a write to a tick that already has input
		// (returns Duplicate). Edge-triggered inputs -- one-frame flicks and jumps -- must survive
		// that rejection, so keep them pending until a fresh tick accepts them; a consumed flick
		// was otherwise lost for good ("can't shoot anymore after a lot of shooting").
		var accepted = S.SetPredictionInput(channel: CLNT.Channel, playerInput) != SetResult.Duplicate;
		if (accepted || !sendingAttack) {
			_inputConsumed = true;
		}
		_pendingJump = jumping && !accepted;
	}

	public void OnAttack(Vector2 attackInput) {
		attackInput = attackInput.Normalized();
		_attackInput = new FVector2(attackInput.X.ToFP(), attackInput.Y.ToFP());
		_inputConsumed = false;
	}

	private static (string host, ushort port, bool offline) ParseLaunchArgs() {
		var host = "127.0.0.1";
		var port = OfflineServer.DefaultPort;
		var offline = true;

		var args = OS.GetCmdlineUserArgs();
		for (var i = 0; i < args.Length; i++) {
			if (args[i] == "--server" && i + 1 < args.Length) {
				host = args[++i];
				offline = false;
			} else if (args[i] == "--port" && i + 1 < args.Length && ushort.TryParse(args[i + 1], out var parsedPort)) {
				port = parsedPort;
				i++;
			}
		}

		return (host, port, offline);
	}
}
