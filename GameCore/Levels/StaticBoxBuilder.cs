using Fixed;
using Fixed32;

namespace Space.GameCore;

public enum LevelColliderComponentKind : byte {
	Box,
}

/// <summary>Collects a static collider's components; the collider counterpart of <see cref="EntityPlacementBuilder"/>.</summary>
public sealed class StaticBoxBuilder {
	private readonly string _sourcePath;
	private readonly FWorldTransform _transform;
	private FVector3? _halfExtents;

	public StaticBoxBuilder(string sourcePath, FWorldTransform transform) {
		_sourcePath = sourcePath;
		_transform = transform;
	}

	public void SetBox(FVector3 halfExtents) => _halfExtents = halfExtents;

	public StaticBox Build() {
		if (!_halfExtents.HasValue) {
			throw new InvalidDataException($"{_sourcePath}: missing required collider components: {nameof(LevelColliderComponentKind.Box)}.");
		}
		var box = new StaticBox(_sourcePath, _transform, _halfExtents.GetValueOrDefault());
		LevelDataValidation.Validate(box);
		return box;
	}
}
