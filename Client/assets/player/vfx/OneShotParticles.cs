using Godot;

namespace Space.Client;

public partial class OneShotParticles : GpuParticles3D {
	public override void _Ready() {
		OneShot = true;
		Finished += QueueFree;
		Emitting = true;
	}
}
