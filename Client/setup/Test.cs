using Godot;
using Shenanicode.Rollback;
using Shenanicode.Rollback.LiteNetLib;
using Space.GameCore;
using FFS.Libraries.StaticEcs;
using static Space.GameCore.Core<Space.Client.ClientWorld>;
using System.Collections.Generic;
using Fixed64;

namespace Space.Client;


public partial class Test : Node3D {
	private float _clientTime;
	private readonly Dictionary<EntityGID, EntityView> _views = [];
	private FVector2 _attackInput;
	private bool _inputConsumed;

	public override void _EnterTree() {
		var connection = new LiteNetLibServerConnection();
		ClientSetup.CreateAndInitialize(connection);
		connection.Connect("127.0.0.1", 8153);
	}

	public override void _Process(double delta) {
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
				ViewAsset.BigSphere => "res://big_sphere.tscn",
				_ => ""
			};
			GD.Print($"Loading view for entity {entity.GID} from {path} with viewId {viewId.Value}");
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
}
