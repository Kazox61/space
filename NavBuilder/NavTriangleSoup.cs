using Space.GameCore;

namespace Space.NavBuilder;

public sealed class NavTriangleSoup {
	public IReadOnlyList<Fixed64.FVector3> Vertices { get; }
	public IReadOnlyList<int> Indices { get; }
	public IReadOnlyList<NavContribution> TriangleContributions { get; }

	internal NavTriangleSoup(
		Fixed64.FVector3[] vertices,
		int[] indices,
		NavContribution[] triangleContributions
	) {
		Vertices = Array.AsReadOnly(vertices);
		Indices = Array.AsReadOnly(indices);
		TriangleContributions = Array.AsReadOnly(triangleContributions);
	}
}
