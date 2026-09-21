using Godot;
using Godot.Collections;

namespace Space.Client;

[GlobalClass]
public partial class EntityViewCatalog : Resource {
	[Export] public Array<EntityViewDefinition> Entries { get; set; } = [];
}
