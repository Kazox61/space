using Godot;

namespace Space.Client;

/// <summary>
/// A detached effect made of several <see cref="GpuParticles3D"/> children: fires them all once and
/// frees itself when the longest one ends. <see cref="Play"/> skips the first
/// <c>skipSeconds</c>, so an effect reported late still ends when it would have had it started on time.
/// </summary>
public partial class OneShotEffect : Node3D {
	public void Play(float skipSeconds) {
		var duration = 0.0;
		foreach (var child in GetChildren()) {
			if (child is not GpuParticles3D particles) {
				continue;
			}

			particles.OneShot = true;
			particles.Preprocess = skipSeconds;
			particles.Restart();
			// A frozen emitter never ends; it goes with the others rather than keep the effect alive.
			if (particles.SpeedScale <= 0.0) {
				continue;
			}

			// Without full explosiveness a one-shot emits over (1 - explosiveness) of a lifetime,
			// so its last particle outlives the first by that much.
			var span = particles.Lifetime * (2.0 - particles.Explosiveness);
			duration = Mathf.Max(duration, span / particles.SpeedScale);
		}

		// Not processAlways: the particles stop while the tree is paused, so the timer must too.
		GetTree().CreateTimer(Mathf.Max(duration - skipSeconds, 0.0) + 0.1, processAlways: false).Timeout += QueueFree;
	}
}
