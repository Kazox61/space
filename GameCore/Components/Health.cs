using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

public struct Health : IComponent {
	public int Value;
	public int MaxValue;

	public override string ToString() {
		return $"Health: {Value}/{MaxValue}";
	}
}
