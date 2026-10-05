using Fixed32;

namespace Space.GameCore;

internal static class LevelShapeFactory {
	public static Shape Create(in PlacementComponents components) {
		Shape shape;
		if (components.SphereShape is { } sphere) {
			shape = Shape.MakeSphere(FVector3.Zero, sphere.Radius);
			shape.Density = sphere.Density;
		} else {
			var box = components.BoxShape!.Value;
			shape = Shape.MakeBox(FVector3.Zero, box.HalfExtents);
			shape.Density = box.Density;
		}
		if (components.SurfaceProperties is { } surface) {
			shape.Material = surface.Material;
			shape.CharacterBounceSpeed = surface.CharacterBounceSpeed;
		}
		return shape;
	}
}
