using Godot;
using Space.GameCore;

namespace Space.Client;

[GlobalClass]
public partial class EntityViewDefinition : Resource {
	[Export] public ViewAsset Asset { get; set; }
	[Export] public PackedScene Scene { get; set; }
	[Export(PropertyHint.Range, "0,256,1")] public int PrewarmCount { get; set; }
}
