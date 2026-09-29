using Fixed32;

namespace Space.GameCore;

internal static class LevelDataValidation {
	private readonly record struct TypeRules(
		LevelEntityComponentKind[] Required,
		LevelEntityComponentKind[] Forbidden,
		BodyType[] Bodies
	);

	private static readonly TypeRules s_crate = new(
		[LevelEntityComponentKind.Health, LevelEntityComponentKind.Loot, LevelEntityComponentKind.Body, LevelEntityComponentKind.BoxShape, LevelEntityComponentKind.View],
		[LevelEntityComponentKind.Navigation],
		[BodyType.Dynamic, BodyType.Kinematic]
	);

	// No View: the client draws static geometry from the map scene itself, so a view would draw it twice.
	private static readonly TypeRules s_staticGeometry = new(
		[LevelEntityComponentKind.Body, LevelEntityComponentKind.BoxShape, LevelEntityComponentKind.Navigation],
		[LevelEntityComponentKind.Health, LevelEntityComponentKind.Loot, LevelEntityComponentKind.View],
		[BodyType.Static]
	);

	private static readonly TypeRules s_door = new(
		[LevelEntityComponentKind.Body, LevelEntityComponentKind.BoxShape, LevelEntityComponentKind.View, LevelEntityComponentKind.ZoneLink, LevelEntityComponentKind.DoorMotion],
		[LevelEntityComponentKind.Health, LevelEntityComponentKind.Loot, LevelEntityComponentKind.Navigation],
		[BodyType.Kinematic]
	);

	// No View, like static geometry: a plate never moves, so the map scene draws it.
	private static readonly TypeRules s_pressurePlate = new(
		[LevelEntityComponentKind.Body, LevelEntityComponentKind.BoxShape, LevelEntityComponentKind.ZoneLink],
		[LevelEntityComponentKind.Health, LevelEntityComponentKind.Loot, LevelEntityComponentKind.View, LevelEntityComponentKind.Navigation, LevelEntityComponentKind.DoorMotion],
		[BodyType.Static]
	);

	public static void Validate(in EntityPlacement placement) {
		if (string.IsNullOrWhiteSpace(placement.SourcePath)) {
			throw new InvalidDataException("Level entity source path is required.");
		}
		var rules = placement.Type switch {
			LevelEntityType.Crate => s_crate,
			LevelEntityType.StaticGeometry => s_staticGeometry,
			LevelEntityType.Door => s_door,
			LevelEntityType.PressurePlate => s_pressurePlate,
			_ => throw new InvalidDataException($"{placement.SourcePath}: unsupported entity type {placement.Type}."),
		};
		var components = placement.Components;
		var missing = rules.Required.Where(kind => !components.Has(kind)).ToArray();
		if (missing.Length > 0) {
			throw new InvalidDataException($"{placement.SourcePath}: missing required entity components: {string.Join(", ", missing)}.");
		}
		var forbidden = rules.Forbidden.Where(kind => components.Has(kind)).ToArray();
		if (forbidden.Length > 0) {
			throw new InvalidDataException($"{placement.SourcePath}: {string.Join(", ", forbidden)} not allowed on {placement.Type}.");
		}

		if (components.Health is < 1 or > 1000) {
			throw new InvalidDataException($"{placement.SourcePath}: health must be between 1 and 1000.");
		}
		if (components.Loot is { } loot && !Enum.IsDefined(loot)) {
			throw new InvalidDataException($"{placement.SourcePath}: invalid loot value {loot}.");
		}
		if (components.Body is { } body && !rules.Bodies.Contains(body)) {
			throw new InvalidDataException($"{placement.SourcePath}: {placement.Type} must use a {string.Join(" or ", rules.Bodies)} body.");
		}
		if (components.BoxShape is { } box) {
			ValidateBoxShape(box, components.Body ?? BodyType.Static, placement.SourcePath);
		}
		if (components.View is { } view && !Enum.IsDefined(view)) {
			throw new InvalidDataException($"{placement.SourcePath}: invalid view {view}.");
		}
		if (components.Navigation is { } navigation && !Enum.IsDefined(navigation)) {
			throw new InvalidDataException($"{placement.SourcePath}: invalid navigation contribution {navigation}.");
		}
		if (components.ZoneLink is { } zoneLink && (string.IsNullOrWhiteSpace(zoneLink) || zoneLink.Length > NavZoneData.MaxIdLength)) {
			throw new InvalidDataException($"{placement.SourcePath}: zone link '{zoneLink}' must be non-empty and at most {NavZoneData.MaxIdLength} characters.");
		}
		if (components.DoorMotion is { } motion) {
			var travelLimit = FP.FromRatio(80, 1);
			if (motion.OpenOffset == FVector3.Zero
				|| FP.Abs(motion.OpenOffset.X) > travelLimit || FP.Abs(motion.OpenOffset.Y) > travelLimit || FP.Abs(motion.OpenOffset.Z) > travelLimit) {
				throw new InvalidDataException($"{placement.SourcePath}: door open offset must be non-zero and at most {travelLimit} per axis.");
			}
			if (motion.Speed <= FP.Zero || motion.Speed > FP.FromRatio(20, 1)) {
				throw new InvalidDataException($"{placement.SourcePath}: door speed must be in (0, 20].");
			}
		}
		if (FQuaternion.LengthSqr(placement.Transform.Rotation) <= FP.CalculationsEpsilonSqr) {
			throw new InvalidDataException($"{placement.SourcePath}: rotation must be non-zero.");
		}

		ValidateOrigin(placement.Transform.Position, placement.SourcePath);
	}

	/// <summary>Every <see cref="PlacementComponents.ZoneLink"/> must name one of the level's zone volumes.</summary>
	public static void ValidateZoneLinks(IEnumerable<EntityPlacement> placements, IReadOnlyList<NavZoneVolume> zones) {
		foreach (var placement in placements) {
			if (placement.Components.ZoneLink is { } zoneLink && !zones.Any(zone => StringComparer.Ordinal.Equals(zone.Id, zoneLink))) {
				throw new InvalidDataException($"{placement.SourcePath}: zone link '{zoneLink}' names no navigation zone in the level.");
			}
		}
	}

	private static void ValidateBoxShape(in BoxShapeData box, BodyType body, string sourcePath) {
		if (box.HalfExtents.X <= FP.Zero || box.HalfExtents.Y <= FP.Zero || box.HalfExtents.Z <= FP.Zero) {
			throw new InvalidDataException($"{sourcePath}: box half-extents must be positive.");
		}
		var extentLimit = body == BodyType.Dynamic ? FP.FromRatio(4, 1) : FP.FromRatio(40, 1);
		if (box.HalfExtents.X > extentLimit || box.HalfExtents.Y > extentLimit || box.HalfExtents.Z > extentLimit) {
			throw new InvalidDataException($"{sourcePath}: box half-extents exceed the {extentLimit} physics limit; split the box.");
		}
		if (box.Density <= FP.Zero || box.Density > FP.Two) {
			throw new InvalidDataException($"{sourcePath}: density must be in (0, 2].");
		}
		var shape = Shape.MakeBox(FVector3.Zero, box.HalfExtents);
		shape.Density = box.Density;
		try {
			PhysicsMassValidation.ValidateShape(shape, body, sourcePath);
		} catch (ArgumentOutOfRangeException exception) {
			throw new InvalidDataException($"{sourcePath}: {exception.Message}", exception);
		}
	}

	public static void Validate(in NavSourceBox box) {
		if (string.IsNullOrWhiteSpace(box.SourcePath)) {
			throw new InvalidDataException("Navigation source path is required.");
		}
		if (!Enum.IsDefined(box.Navigation)) {
			throw new InvalidDataException($"{box.SourcePath}: invalid navigation contribution {box.Navigation}.");
		}
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

	public static void Validate(in NavZoneVolume zone) {
		if (string.IsNullOrWhiteSpace(zone.Id) || zone.Id.Length > NavZoneData.MaxIdLength) {
			throw new InvalidDataException($"Navigation zone id '{zone.Id}' must be non-empty and at most {NavZoneData.MaxIdLength} characters.");
		}
		var extentLimit = FP.FromRatio(1024, 1);
		if (zone.HalfExtents.X <= FP.Zero || zone.HalfExtents.Y <= FP.Zero || zone.HalfExtents.Z <= FP.Zero) {
			throw new InvalidDataException($"Navigation zone '{zone.Id}': half-extents must be positive.");
		}
		if (zone.HalfExtents.X > extentLimit || zone.HalfExtents.Y > extentLimit || zone.HalfExtents.Z > extentLimit) {
			throw new InvalidDataException($"Navigation zone '{zone.Id}': half-extents exceed {extentLimit}.");
		}
		if (FQuaternion.LengthSqr(zone.Transform.Rotation) <= FP.CalculationsEpsilonSqr) {
			throw new InvalidDataException($"Navigation zone '{zone.Id}': rotation must be non-zero.");
		}
		var up = FQuaternion.Normalize(zone.Transform.Rotation) * FVector3.Up;
		var rotationTolerance = FP.FromRatio(1, 1000);
		if (FP.Abs(up.X) > rotationTolerance || FP.Abs(up.Z) > rotationTolerance || up.Y < FP.One - rotationTolerance) {
			throw new InvalidDataException($"Navigation zone '{zone.Id}': rotation may only be about the Y axis.");
		}
		ValidateOrigin(zone.Transform.Position, $"Navigation zone '{zone.Id}'");
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
