using Fixed64;
using Godot;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

/// <summary>
/// Maps deduplicated simulation effects (<see cref="FxLog"/>) to sounds and VFX. Everything here
/// is fire-and-forget: effects spawn detached from entity views, so a pooled view leaving the tree,
/// or a correction that removes the event, never cuts one off.
/// </summary>
[GlobalClass]
public partial class FxPlayer : Node, IFxPlayer {
	[Export] private AudioStream _attackSound;
	[Export] private AudioStream _hitSound;
	[Export] private PackedScene _hitVfx;

	public void Play(in FxEvent fx, int ageTicks) {
		switch (fx.Kind) {
			case FxKind.AttackStarted:
				PlaySfx(_attackSound, ageTicks);
				break;
			case FxKind.ProjectileHit:
				PlaySfx(_hitSound, ageTicks);
				SpawnVfx(_hitVfx, fx.Position, ToGodot(fx.Direction), ageTicks);
				break;
			case FxKind.ShotReleased:
				// Reserved for a muzzle flash.
				break;
		}
	}

	/// <summary>
	/// Spawns <paramref name="scene"/> with its +Y along <paramref name="up"/> (world up when zero).
	/// Like <see cref="PlaySfx"/>, a late effect skips ahead by its age.
	/// </summary>
	private void SpawnVfx(PackedScene scene, FVector3 position, Vector3 up, int ageTicks) {
		if (scene?.Instantiate() is not OneShotEffect effect) {
			return;
		}

		GetParent().AddChild(effect);
		var rotation = up.IsZeroApprox() ? Quaternion.Identity : new Quaternion(Vector3.Up, up.Normalized());
		effect.GlobalTransform = new Transform3D(new Basis(rotation), ToGodot(position));
		effect.Play((float)ageTicks / S.TickRate);
	}

	private static Vector3 ToGodot(FVector3 v) => new(v.X.ToFloat(), v.Y.ToFloat(), v.Z.ToFloat());

	/// <summary>A late event starts part-way in, so it ends when it would have had it played on time.</summary>
	private static void PlaySfx(AudioStream stream, int ageTicks) {
		if (stream is null) {
			return;
		}

		var offset = (float)ageTicks / S.TickRate;
		if (offset >= stream.GetLength()) {
			return;
		}

		Audio.PlaySfx(stream, offset);
	}
}
