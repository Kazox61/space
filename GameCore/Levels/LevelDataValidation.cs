using Fixed32;

namespace Space.GameCore;

internal static class LevelDataValidation {
	public static void Validate(in EntityPlacement placement) {
		if (placement.Type != LevelEntityType.Crate) {
			throw new InvalidDataException($"{placement.SourcePath}: unsupported entity type {placement.Type}.");
		}
		if (placement.Crate.Health is < 1 or > 1000) {
			throw new InvalidDataException($"{placement.SourcePath}: crate health must be between 1 and 1000.");
		}
		if (!Enum.IsDefined(placement.Crate.Loot)) {
			throw new InvalidDataException($"{placement.SourcePath}: invalid loot value {placement.Crate.Loot}.");
		}
		if (placement.Crate.BodyType is not (BodyType.Dynamic or BodyType.Kinematic)) {
			throw new InvalidDataException($"{placement.SourcePath}: map entities must use a dynamic or kinematic body.");
		}
		if (placement.Crate.BoxHalfExtents.X <= FP.Zero
			|| placement.Crate.BoxHalfExtents.Y <= FP.Zero
			|| placement.Crate.BoxHalfExtents.Z <= FP.Zero) {
			throw new InvalidDataException($"{placement.SourcePath}: box half-extents must be positive.");
		}
		var extentLimit = placement.Crate.BodyType == BodyType.Dynamic ? FP.FromRatio(4, 1) : FP.FromRatio(40, 1);
		if (placement.Crate.BoxHalfExtents.X > extentLimit
			|| placement.Crate.BoxHalfExtents.Y > extentLimit
			|| placement.Crate.BoxHalfExtents.Z > extentLimit) {
			throw new InvalidDataException($"{placement.SourcePath}: box half-extents exceed the {extentLimit} physics limit.");
		}
		if (placement.Crate.Density <= FP.Zero || placement.Crate.Density > FP.Two) {
			throw new InvalidDataException($"{placement.SourcePath}: density must be in (0, 2].");
		}
		var shape = Shape.MakeBox(FVector3.Zero, placement.Crate.BoxHalfExtents);
		shape.Density = placement.Crate.Density;
		try {
			PhysicsMassValidation.ValidateShape(shape, placement.Crate.BodyType, placement.SourcePath);
		} catch (ArgumentOutOfRangeException exception) {
			throw new InvalidDataException($"{placement.SourcePath}: {exception.Message}", exception);
		}
		if (!Enum.IsDefined(placement.Crate.View)) {
			throw new InvalidDataException($"{placement.SourcePath}: invalid view {placement.Crate.View}.");
		}
		if (FQuaternion.LengthSqr(placement.Transform.Rotation) <= FP.CalculationsEpsilonSqr) {
			throw new InvalidDataException($"{placement.SourcePath}: rotation must be non-zero.");
		}

		ValidateOrigin(placement.Transform.Position, placement.SourcePath);
	}

	public static void Validate(in StaticBox box) {
		var extentLimit = FP.FromRatio(40, 1);
		if (box.HalfExtents.X <= FP.Zero || box.HalfExtents.Y <= FP.Zero || box.HalfExtents.Z <= FP.Zero) {
			throw new InvalidDataException($"{box.SourcePath}: box half-extents must be positive.");
		}
		if (box.HalfExtents.X > extentLimit || box.HalfExtents.Y > extentLimit || box.HalfExtents.Z > extentLimit) {
			throw new InvalidDataException($"{box.SourcePath}: box half-extents exceed the {extentLimit} static physics limit; split the box.");
		}
		if (FQuaternion.LengthSqr(box.Transform.Rotation) <= FP.CalculationsEpsilonSqr) {
			throw new InvalidDataException($"{box.SourcePath}: rotation must be non-zero.");
		}
		ValidateOrigin(box.Transform.Position, box.SourcePath);
	}

	private static void ValidateOrigin(Fixed.FPos position, string sourcePath) {
		// Reserve enough room for the largest supported rotated shape around the body origin.
		var coordinateLimit = Fixed64.FP.FromRatio(8064, 1);
		if (position.X < -coordinateLimit || position.X > coordinateLimit
			|| position.Y < -coordinateLimit || position.Y > coordinateLimit
			|| position.Z < -coordinateLimit || position.Z > coordinateLimit) {
			throw new InvalidDataException($"{sourcePath}: body origin must be within +/-8064.");
		}
	}
}
