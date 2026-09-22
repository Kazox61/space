using Godot;

namespace Space.Client;

public partial class OneShotParticles : GpuParticles3D {
	public override void _Ready() {
		Emitting = false;
		OneShot = true;
		Finished += QueueFree;
		Restart();
	}
}
