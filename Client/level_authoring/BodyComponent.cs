using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

[Tool, GlobalClass]
public partial class BodyComponent : EntityComponent {
	[Export]
	public BodyType Type { get; set; } = BodyType.Dynamic;

	public override LevelEntityComponentKind Kind => LevelEntityComponentKind.Body;
	public override void Apply(EntityPlacementBuilder builder) => builder.SetBody(Type);
}
