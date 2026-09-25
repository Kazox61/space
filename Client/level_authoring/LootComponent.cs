using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

[Tool, GlobalClass]
public partial class LootComponent : EntityComponent {
	[Export]
	public LootKind Value { get; set; }

	public override LevelEntityComponentKind Kind => LevelEntityComponentKind.Loot;
	public override void Apply(EntityPlacementBuilder builder) => builder.SetLoot(Value);
}
