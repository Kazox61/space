using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

[Tool, GlobalClass]
public partial class HealthComponent : EntityComponent {
	[Export(PropertyHint.Range, "1,1000,1")]
	public int Value { get; set; } = 100;

	public override LevelEntityComponentKind Kind => LevelEntityComponentKind.Health;
	public override void Apply(EntityPlacementBuilder builder) => builder.SetHealth(Value);
}
