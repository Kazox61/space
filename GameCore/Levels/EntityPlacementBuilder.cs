using Fixed;
using Fixed32;

namespace Space.GameCore;

public enum LevelEntityComponentKind : byte {
	Health,
	Loot,
	Body,
	BoxShape,
	View,
}

public sealed class EntityPlacementBuilder {
	private readonly string _sourcePath;
	private readonly LevelEntityType _type;
	private readonly FWorldTransform _transform;
	private int? _health;
	private LootKind? _loot;
	private BodyType? _bodyType;
	private FVector3? _boxHalfExtents;
	private FP? _density;
	private ViewAsset? _view;

	public EntityPlacementBuilder(string sourcePath, LevelEntityType type, FWorldTransform transform) {
		_sourcePath = sourcePath;
		_type = type;
		_transform = transform;
	}

	public void SetHealth(int health) => _health = health;
	public void SetLoot(LootKind loot) => _loot = loot;
	public void SetBody(BodyType bodyType) => _bodyType = bodyType;
	public void SetBoxShape(FVector3 halfExtents, FP density) {
		_boxHalfExtents = halfExtents;
		_density = density;
	}
	public void SetView(ViewAsset view) => _view = view;

	public EntityPlacement Build() {
		var missing = new List<string>();
		if (!_health.HasValue)
			missing.Add(nameof(LevelEntityComponentKind.Health));
		if (!_loot.HasValue)
			missing.Add(nameof(LevelEntityComponentKind.Loot));
		if (!_bodyType.HasValue)
			missing.Add(nameof(LevelEntityComponentKind.Body));
		if (!_boxHalfExtents.HasValue || !_density.HasValue)
			missing.Add(nameof(LevelEntityComponentKind.BoxShape));
		if (!_view.HasValue)
			missing.Add(nameof(LevelEntityComponentKind.View));
		if (missing.Count > 0) {
			throw new InvalidDataException($"{_sourcePath}: missing required entity components: {string.Join(", ", missing)}.");
		}

		var placement = new EntityPlacement(
			_sourcePath,
			_type,
			_transform,
			new CratePlacementData(
				_health.GetValueOrDefault(),
				_loot.GetValueOrDefault(),
				_bodyType.GetValueOrDefault(),
				_boxHalfExtents.GetValueOrDefault(),
				_density.GetValueOrDefault(),
				_view.GetValueOrDefault()
			)
		);
		LevelDataValidation.Validate(placement);
		return placement;
	}
}
