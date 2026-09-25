using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

[Tool, GlobalClass]
public partial class ViewComponent : EntityComponent {
	[Export]
	public ViewAsset Asset { get; set; } = ViewAsset.Crate;

	public override LevelEntityComponentKind Kind => LevelEntityComponentKind.View;
	public override void Apply(EntityPlacementBuilder builder) => builder.SetView(Asset);
}
