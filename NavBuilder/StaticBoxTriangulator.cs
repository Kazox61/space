using Fixed;
using Fixed32;
using Space.GameCore;

namespace Space.NavBuilder;

public static class StaticBoxTriangulator {
	private static readonly int[] s_boxIndices = [
		0, 1, 2, 0, 2, 3, // Bottom (-Y)
		4, 7, 6, 4, 6, 5, // Top (+Y)
		0, 4, 5, 0, 5, 1, // Front (-Z)
		3, 2, 6, 3, 6, 7, // Back (+Z)
		0, 3, 7, 0, 7, 4, // Left (-X)
		1, 5, 6, 1, 6, 2, // Right (+X)
	];

	public static NavTriangleSoup Build(IEnumerable<StaticBox> boxes) {
		var ordered = boxes.OrderBy(static box => box.SourcePath, StringComparer.Ordinal).ToArray();
		var vertices = new List<Fixed64.FVector3>();
		var indices = new List<int>();
		var contributions = new List<NavContribution>();
		string? previousSourcePath = null;

		foreach (var box in ordered) {
			box.Validate();
			if (previousSourcePath is not null && StringComparer.Ordinal.Equals(previousSourcePath, box.SourcePath)) {
				throw new InvalidDataException($"Static box source path '{box.SourcePath}' is duplicated.");
			}
			previousSourcePath = box.SourcePath;
			if (box.Navigation == NavContribution.Excluded) {
				continue;
			}
			var vertexStart = vertices.Count;
			AddVertices(vertices, box);
			foreach (var index in s_boxIndices) {
				indices.Add(vertexStart + index);
			}
			for (var i = 0; i < s_boxIndices.Length / 3; i++) {
				contributions.Add(box.Navigation);
			}
		}

		return new NavTriangleSoup(vertices.ToArray(), indices.ToArray(), contributions.ToArray());
	}

	private static void AddVertices(List<Fixed64.FVector3> vertices, in StaticBox box) {
		var h = box.HalfExtents;
		var transform = box.Transform;
		transform.Rotation = FQuaternion.Normalize(transform.Rotation);
		AddVertex(vertices, transform, new FVector3(-h.X, -h.Y, -h.Z));
		AddVertex(vertices, transform, new FVector3(h.X, -h.Y, -h.Z));
		AddVertex(vertices, transform, new FVector3(h.X, -h.Y, h.Z));
		AddVertex(vertices, transform, new FVector3(-h.X, -h.Y, h.Z));
		AddVertex(vertices, transform, new FVector3(-h.X, h.Y, -h.Z));
		AddVertex(vertices, transform, new FVector3(h.X, h.Y, -h.Z));
		AddVertex(vertices, transform, new FVector3(h.X, h.Y, h.Z));
		AddVertex(vertices, transform, new FVector3(-h.X, h.Y, h.Z));
	}

	private static void AddVertex(List<Fixed64.FVector3> vertices, in FWorldTransform transform, FVector3 localPoint) {
		var worldPoint = FWorldTransform.TransformPoint(transform, localPoint);
		vertices.Add(new Fixed64.FVector3(worldPoint.X, worldPoint.Y, worldPoint.Z));
	}
}
