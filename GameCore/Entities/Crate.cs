using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

public struct Crate : IEntityType {
	public int InitialHealth;
	public LootKind Loot;
	public ViewAsset View;

	public byte Id() => 4;

	public void OnCreate<TWorld>(World<TWorld>.Entity entity) where TWorld : struct, IWorldType {
		entity.Set(
			new Health { Value = InitialHealth, MaxValue = InitialHealth },
			new LootDrop { Kind = Loot },
			new ViewId { Value = View }
		);
	}
}
