using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

/// <summary>How static geometry or a platform's start pose contributes to the offline navmesh bake.</summary>
[Tool, GlobalClass]
public partial class NavigationComponent : EntityComponent {
	[Export]
	public NavContribution Value { get; set; } = NavContribution.Walkable;

	public override LevelEntityComponentKind Kind => LevelEntityComponentKind.Navigation;
	public override void Apply(EntityPlacementBuilder builder) => builder.SetNavigation(Value);
}
