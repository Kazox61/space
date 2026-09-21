using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using Fixed64;
using Godot;
using Shenanicode.Rollback.LiteNetLib;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

public partial class Test : Node3D {
	private float _clientTime;
	private readonly Dictionary<EntityGID, EntityView> _views = [];
	private FVector2 _attackInput;
	private bool _inputConsumed;

	public override void _EnterTree() {
		var (host, port, offline) = ParseLaunchArgs();
		if (offline && !OfflineServer.TryStart(port)) {
			GD.Print($"Port {port} is occupied — connecting to an external server instead.");
		}

		var connection = new LiteNetLibServerConnection();
		ClientSetup.CreateAndInitialize(connection);
		connection.Connect(host, port);
	}

	public override void _ExitTree() {
		ClientSetup.Destroy();
		OfflineServer.Destroy();
	}

	public override void _Process(double delta) {
		OfflineServer.Update(delta);
		_clientTime += (float)delta;
		CLNT.Update(_clientTime);
		SyncViews();
		var moveInput = Input.GetVector("move_left", "move_right", "move_forward", "move_backward").Normalized();
		var playerInput = new PlayerInput {
			MoveX = moveInput.X.ToFP(),
			MoveY = moveInput.Y.ToFP(),
			AttackX = _inputConsumed ? FP.Zero : _attackInput.X,
			AttackY = _inputConsumed ? FP.Zero : _attackInput.Y,
			Jump = Input.IsActionJustPressed("jump")
		};
		_inputConsumed = true;
		S.SetPredictionInput(channel: CLNT.Channel, playerInput);
	}

	private void SyncViews() {
		foreach (var entity in W.Query<All<ViewId>>().Entities()) {
			if (_views.ContainsKey(entity.GID)) {
				continue;
			}

			var viewId = entity.Read<ViewId>();
			var path = viewId.Value switch {
				ViewAsset.Player => "res://player.tscn",
				ViewAsset.Projectile => "res://projectile.tscn",
				ViewAsset.Sphere => "res://sphere.tscn",
				ViewAsset.Platform => "res://platform.tscn",
				ViewAsset.Box => "res://box.tscn",
				ViewAsset.Dummy => "res://dummy.tscn",
				_ => ""
			};
			var packedScene = GD.Load<PackedScene>(path);
			var view = packedScene.Instantiate<EntityView>();
			_views[entity.GID] = view;
			view.AssignEntity(entity.GID);
		}

		List<EntityGID> toRemove = [];
		foreach (var (gid, view) in _views) {
			if (!gid.TryUnpack<ClientWorld>(out _)) {
				view.RemoveEntity(gid);
				toRemove.Add(gid);
			}
		}

		foreach (var gid in toRemove) {
			_views.Remove(gid);
		}

		foreach (var (gid, view) in _views) {
			view.UpdateEntity(gid);
		}
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
