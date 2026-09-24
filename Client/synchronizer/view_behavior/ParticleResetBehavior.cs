using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using Fixed32;
using Godot;
using Space.GameCore;

namespace Space.Client;

/// <summary>
/// Clears every world-space particle system in a pooled view when it is assigned to a new entity.
/// Without this, a reused view keeps the particles and trail history of its previous entity, so the
/// trail draws a streak from where the old entity died to where the new one spawned.
/// <para>
/// <see cref="GpuParticles3D.Restart"/> is not enough for a GPUTrail3D: its shader uses
/// <c>keep_data</c>, which keeps particle data across a restart. Changing <c>Amount</c> makes the
/// rendering server drop the particle buffer and allocate a fresh one, the same state a newly
/// instantiated trail starts from.
/// </para>
/// <para>
/// Must come after <see cref="TransformViewBehavior"/> in the view's behavior list, so the view is
/// already at the new entity's position when the particles restart.
/// </para>
/// </summary>
[GlobalClass]
public partial class ParticleResetBehavior : EntityBehavior {
	private static readonly StringName s_trailOldPosition = "_old_pos";
	private static readonly StringName s_trailUpdateBillboard = "_update_billboard_transform";

	// The view is added to the tree during EntityViewUpdater's process pass, so a trail's own
	// _process -- which turns it to face the camera -- first runs next frame. Until then it would
	// emit with the unrotated node transform, a wide vertical band. The updater calls OnEntityUpdate
	// once in the assigning frame and again next frame; show the trail on that second call.
	private const int HiddenUpdates = 2;

	private readonly List<GpuParticles3D> _particles = [];
	private readonly List<GpuParticles3D> _trails = [];
	private int _hiddenUpdatesRemaining;

	public override void _Ready() {
		Collect(GetParent());
	}

	public override void OnEntityAssigned(EntityGID entityGid) {
		var direction = TravelDirection(entityGid);
		foreach (var particles in _particles) {
			var amount = particles.Amount;
			particles.Amount = amount + 1;
			particles.Amount = amount;
			particles.Restart();
		}

		foreach (var trail in _trails) {
			// GPUTrail3D orients its billboard from the distance moved since last frame; reset that
			// too, or its first frame spans the jump from the old position.
			trail.Set(s_trailOldPosition, trail.GlobalPosition);
			if (direction != Vector3.Zero) {
				trail.Call(s_trailUpdateBillboard, trail.GlobalBasis.Y.Length() * direction);
			}
			trail.Visible = false;
		}
		_hiddenUpdatesRemaining = HiddenUpdates;
	}

	public override void OnEntityRemoved(EntityGID entityGid) {
		_hiddenUpdatesRemaining = 0;
	}

	public override void OnEntityUpdate(EntityGID entityGid) {
		if (_hiddenUpdatesRemaining == 0 || --_hiddenUpdatesRemaining > 0) {
			return;
		}

		foreach (var trail in _trails) {
			trail.Visible = true;
		}
	}

	private static Vector3 TravelDirection(EntityGID entityGid) {
		if (!entityGid.TryUnpack<ClientWorld>(out var entity) || !entity.Has<Body>()) {
			return Vector3.Zero;
		}

		var velocity = entity.Read<Body>().LinearVelocity;
		return new Vector3(velocity.X.ToFloat(), velocity.Y.ToFloat(), velocity.Z.ToFloat()).Normalized();
	}

	private void Collect(Node node) {
		foreach (var child in node.GetChildren()) {
			if (child is GpuParticles3D particles) {
				_particles.Add(particles);
				if (particles.HasMethod(s_trailUpdateBillboard)) {
					_trails.Add(particles);
				}
			}
			Collect(child);
		}
	}
}
