using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

/// <summary>The <see cref="NavZone"/> a door blocks or a pressure plate switches, by its zone id.</summary>
[Tool, GlobalClass]
public partial class ZoneLinkComponent : EntityComponent {
	[Export]
	public string ZoneId { get; set; } = "";

	public override LevelEntityComponentKind Kind => LevelEntityComponentKind.ZoneLink;
	public override void Apply(EntityPlacementBuilder builder) => builder.SetZoneLink(ZoneId);
}
