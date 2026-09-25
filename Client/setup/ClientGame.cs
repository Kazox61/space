using System;
using System.Collections.Generic;
using System.IO;
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
	/// <summary>Level loaded from <c>res://maps/</c> unless <c>--level</c> is passed; empty means <see cref="LevelFile.DefaultName"/>.</summary>
	[Export] private string _levelName = "";

	private float _clientTime;
	private FVector2 _attackInput;
	private bool _inputConsumed;
	private EntityViewUpdater _viewUpdater;
	private bool _pendingJump;
	private readonly List<PositionCorrection> _corrections = [];
	private LevelFile _level;

	public override void _EnterTree() {
		var (host, port, offline, levelName) = ParseLaunchArgs(string.IsNullOrWhiteSpace(_levelName) ? LevelFile.DefaultName : _levelName);
		try {
			_level = ReadLevel(levelName);
		} catch (Exception exception) when (exception is IOException or InvalidDataException) {
			GD.PushError(exception.Message);
			SetProcess(false);
			return;
		}
		GD.Print($"Level: {_level.Name} entities={_level.Data.Entities.Count} staticBoxes={_level.Data.StaticBoxes.Count} sha256={_level.ContentHash}");
		AddMapVisuals(_level.Name);

		if (offline && !OfflineServer.TryStart(_level, port)) {
			GD.Print($"Port {port} is occupied — connecting to an external server instead.");
		}

		var connection = new LiteNetLibServerConnection();
		ClientSetup.CreateAndInitialize(connection, _level);
		connection.Connect(host, port, _level.ConnectionKey);

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
		ClientSetup.CorrectionProbe.BeginUpdate();
		CLNT.Update(_clientTime);
		RenderInterpolation.Alpha = CLNT.CalculateInterpolation(_clientTime);
		if (ClientSetup.CorrectionProbe.EndUpdate(_corrections)) {
			ClientSetup.Corrections.Apply(_corrections);
		} else {
			ClientSetup.Corrections.Clear();
		}
		ClientSetup.Corrections.Advance((float)delta);
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

	/// <summary>Reads <c>res://maps/&lt;name&gt;.level.bytes</c> and verifies its embedded content hash.</summary>
	private static LevelFile ReadLevel(string name) {
		var path = $"res://maps/{name}{LevelFile.Extension}";
		var bytes = Godot.FileAccess.GetFileAsBytes(path);
		if (bytes.Length == 0) {
			throw new FileNotFoundException($"Level file '{path}' is missing or empty ({Godot.FileAccess.GetOpenError()}).", path);
		}
		return LevelFile.Read(name, bytes);
	}

	/// <summary>
	/// Instances <c>res://maps/&lt;name&gt;.tscn</c> for its visuals. Its <c>LevelCollider</c>s draw nothing in the
	/// game; the simulation collides against the level file.
	/// </summary>
	private void AddMapVisuals(string name) {
		var path = $"res://maps/{name}.tscn";
		if (!ResourceLoader.Exists(path)) {
			GD.PushWarning($"Map scene '{path}' not found; static geometry will be invisible.");
			return;
		}
		AddChild(GD.Load<PackedScene>(path).Instantiate());
	}

	private static (string host, ushort port, bool offline, string level) ParseLaunchArgs(string defaultLevel) {
		var host = "127.0.0.1";
		var port = OfflineServer.DefaultPort;
		var offline = true;
		var level = defaultLevel;

		var args = OS.GetCmdlineUserArgs();
		for (var i = 0; i < args.Length; i++) {
			if (args[i] == "--server" && i + 1 < args.Length) {
				host = args[++i];
				offline = false;
			} else if (args[i] == "--port" && i + 1 < args.Length && ushort.TryParse(args[i + 1], out var parsedPort)) {
				port = parsedPort;
				i++;
			} else if (args[i] == "--level" && i + 1 < args.Length) {
				level = args[++i];
			}
		}

		return (host, port, offline, level);
	}
}
