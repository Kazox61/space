using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

/// <summary>How static geometry contributes to the offline navmesh bake. Required on static geometry, not allowed elsewhere.</summary>
[Tool, GlobalClass]
public partial class NavigationComponent : EntityComponent {
	[Export]
	public NavContribution Value { get; set; } = NavContribution.Walkable;

	public override LevelEntityComponentKind Kind => LevelEntityComponentKind.Navigation;
	public override void Apply(EntityPlacementBuilder builder) => builder.SetNavigation(Value);
}
