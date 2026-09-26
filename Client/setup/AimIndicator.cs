using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed64;
using Godot;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

/// <summary>Draws the local player's touch aim and clips it against the predicted game world.</summary>
[GlobalClass]
public partial class AimIndicator : Node3D {
	private const float GroundClearance = 0.4f;
	private const float WidthMultiplier = 1.4f;
	private const float BorderWidth = 0.07f;
	private const float FillHeightOffset = 0.002f;

	[Export] private MeshInstance3D _leftBorder;
	[Export] private MeshInstance3D _rightBorder;
	[Export] private MeshInstance3D _endBorder;
	[Export] private MeshInstance3D _fill;

	public override void _Ready() {
		HideIndicator();
	}

	public override void _Process(double delta) {
		var aim = Input.GetVector("aim_left", "aim_right", "aim_forward", "aim_backward");
		if (aim.LengthSquared() <= 0.0001f || !TryGetLocalPlayer(out var player)) {
			HideIndicator();
			return;
		}

		aim = aim.Normalized();
		ref readonly var transform = ref player.Read<Transform>();
		ref readonly var mover = ref player.Read<Mover>();
		ref readonly var playerInfo = ref player.Read<PlayerInfo>();
		var config = Systems.GetResource<CharacterRes>();
		var direction = new Fixed64.FVector3(aim.X.ToFP(), Fixed64.FP.Zero, aim.Y.ToFP());
		var origin = transform.Position + direction * config.ProjectileSpawnOffset;
		var translation = (direction * config.ProjectileMaxRange).To32();
		var filter = Filter.Default;
		filter.GroupIndex = Filter.SelfGroup(playerInfo.InputChannel);

		var length = Fixed32.FConversions.ToFloat(Fixed32.FVector3.Length(translation));
		if (W.HasResource<BroadPhase>() && PhysicsQueries.CastShapeClosest(
			W.GetResource<BroadPhase>(),
			new FWorldTransform(new FPos(origin.X, origin.Y, origin.Z), Fixed32.FQuaternion.Identity),
			ShapeProxy.MakePoint(Fixed32.FVector3.Zero, config.ProjectileRadius.To32()),
			translation,
			filter,
			QuerySensorMode.Exclude,
			out var hit)) {
			length *= Fixed32.FConversions.ToFloat(hit.Fraction);
		}

		var forward = new Vector3(aim.X, 0.0f, aim.Y);
		var right = Vector3.Up.Cross(forward).Normalized();
		var width = config.ProjectileRadius.ToFloat() * 2.0f * WidthMultiplier;
		var start = new Vector3(
			origin.X.ToFloat(),
			transform.Position.Y.ToFloat()
				+ Fixed32.FConversions.ToFloat(mover.CapsuleCenter1.Y)
				- Fixed32.FConversions.ToFloat(mover.CapsuleRadius)
				+ GroundClearance,
			origin.Z.ToFloat());
		var end = start + forward * length;
		var fillWidth = Mathf.Max(0.0f, width - BorderWidth * 2.0f);
		var fillLength = Mathf.Max(0.0f, length - BorderWidth);
		var center = (start + end) * 0.5f;
		var sideOffset = right * (width - BorderWidth) * 0.5f;
		var endOffset = forward * (length - BorderWidth) * 0.5f;
		var fillCenter = start + forward * fillLength * 0.5f;

		SetStrip(_leftBorder, center - sideOffset, right, forward, BorderWidth, length);
		SetStrip(_rightBorder, center + sideOffset, right, forward, BorderWidth, length);
		SetStrip(_endBorder, center + endOffset, right, forward, width, BorderWidth);
		_fill.GlobalTransform = new Transform3D(
			new Basis(right * fillWidth, Vector3.Up, forward * fillLength),
			fillCenter + Vector3.Up * FillHeightOffset);
		var visible = length > 0.001f;
		_leftBorder.Visible = visible;
		_rightBorder.Visible = visible;
		_endBorder.Visible = visible;
		_fill.Visible = fillWidth > 0.001f && fillLength > 0.001f;
	}

	private static bool TryGetLocalPlayer(out W.Entity player) {
		player = default;
		if (!W.IsWorldInitialized) {
			return false;
		}

		foreach (var candidate in W.Query<All<PlayerInfo, Transform, Mover>>().Entities()) {
			if (candidate.Read<PlayerInfo>().InputChannel == CLNT.Channel) {
				player = candidate;
				return true;
			}
		}
		return false;
	}

	private static void SetStrip(MeshInstance3D strip, Vector3 center, Vector3 right, Vector3 forward, float width, float length) {
		strip.GlobalTransform = new Transform3D(
			new Basis(right * width, Vector3.Up, forward * length),
			center);
	}

	private void HideIndicator() {
		_leftBorder.Visible = false;
		_rightBorder.Visible = false;
		_endBorder.Visible = false;
		_fill.Visible = false;
	}
}
