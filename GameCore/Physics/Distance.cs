using Fixed32;

namespace Space.GameCore;

/// <summary>
/// Shape distance and shape cast queries, ported from box3d's distance.c. Continuous
/// time-of-impact (b3TimeOfImpact) is not yet ported.
/// </summary>
public static class Distance {
	private const int MaxSimplexVertices = 4;
	private const int MaxGjkIterations = 32;

	/// <summary>
	/// Tolerance for validating the GJK separating normal is (close enough to) unit length.
	/// box3d's C implementation checks this against a float32-scale epsilon (100 * FLT_EPSILON),
	/// which a chain of cross products followed by a LUT-based Sqrt in Fixed32 (16 fractional bits,
	/// ~4-5 significant digits) cannot reliably meet - normalizing a well-formed separating axis can
	/// legitimately land ~5e-4 off from unit length here. Looser than <see cref="FP.CalculationsEpsilon"/>.
	/// </summary>
	private static readonly FP NormalTolerance = FP.FromRatio(1, 100);

	/// <summary>
	/// Compute the closest points between two shapes represented as point clouds. The cache is
	/// input/output for warm-starting; zero-initialize <see cref="SimplexCache.Count"/> on the first
	/// call. The query runs in frame A, so the witness points and normal are returned in frame A.
	/// </summary>
	/// <param name="input">The distance query input.</param>
	/// <param name="cache">Warm-start cache, updated in place.</param>
	/// <param name="debugSimplexes">Optional buffer to record each GJK iteration's simplex, for debugging.</param>
	public static DistanceOutput ShapeDistance(DistanceInput input, ref SimplexCache cache, Simplex[]? debugSimplexes = null) {
		// The query runs in frame A using the relative pose of B in A.
		var xf = input.Transform;

		// Use a matrix for faster repeated rotation.
		var m = FMatrix3.FromQuaternion(xf.Rotation);
		var mt = FMatrix3.Transpose(m);

		var proxyA = input.ProxyA;
		var proxyB = input.ProxyB;
		var pointsA = proxyA.Points!;
		var pointsB = proxyB.Points!;

		// Compute the initial simplex from the cache.
		var simplex = Simplex.Empty;
		simplex.Count = cache.Count;
		for (var i = 0; i < cache.Count; i++) {
			var index1 = cache.IndexA[i];
			var index2 = cache.IndexB[i];

			var vertex1 = pointsA[index1];
			var vertex2 = m * pointsB[index2] + xf.Position;

			ref var vertex = ref Simplex.VertexAt(ref simplex, i);
			vertex.IndexA = index1;
			vertex.IndexB = index2;
			vertex.WA = vertex1;
			vertex.WB = vertex2;
			vertex.W = vertex2 - vertex1;
			vertex.A = FP.Zero;
		}

		// If the new simplex metric is substantially different than the old one, flush the simplex.
		if (simplex.Count > 0) {
			var metric1 = cache.Metric;
			var metric2 = GJK.GetMetric(simplex);

			if (2 * metric1 < metric2 || metric2 < FP.Half * metric1 || metric2 < FP.CalculationsEpsilon) {
				simplex.Count = 0;
			}
		}

		// If the cache is invalid or empty.
		if (simplex.Count == 0) {
			var vertex1 = pointsA[0];
			var vertex2 = m * pointsB[0] + xf.Position;

			simplex.Count = 1;
			simplex.V0.IndexA = 0;
			simplex.V0.IndexB = 0;
			simplex.V0.WA = vertex1;
			simplex.V0.WB = vertex2;
			simplex.V0.W = vertex2 - vertex1;
			simplex.V0.A = FP.Zero;
		}

		var backup = Simplex.Empty;

		var simplexIndex = 0;
		if (debugSimplexes != null && simplexIndex < debugSimplexes.Length) {
			debugSimplexes[simplexIndex] = simplex;
			simplexIndex += 1;
		}

		var distanceOutput = new DistanceOutput();

		// Keep track of squared distance.
		var distanceSq = FP.MaxValue;
		var normal = FVector3.Zero;

		var iteration = 0;
		for (; iteration < MaxGjkIterations; iteration++) {
			bool solved;
			switch (simplex.Count) {
				case 1:
					simplex.V0.A = FP.One;
					solved = true;
					break;

				case 2:
					solved = GJK.SolveSimplex2(ref simplex);
					break;

				case 3:
					solved = GJK.SolveSimplex3(ref simplex);
					break;

				case 4:
					solved = GJK.SolveSimplex4(ref simplex);
					break;

				default:
					solved = false;
					break;
			}

			if (!solved) {
				// No progress - reconstruct the last simplex.
				simplex = backup;
				break;
			}

			if (debugSimplexes != null && simplexIndex < debugSimplexes.Length) {
				debugSimplexes[simplexIndex] = simplex;
				simplexIndex += 1;
				distanceOutput.Iterations = iteration;
				distanceOutput.SimplexCount = simplexIndex;
			}

			if (simplex.Count == MaxSimplexVertices) {
				// Overlap. box3d's own b3ShapeDistance leaves Normal at its default here too (no
				// well-defined separating normal for a confirmed 4-point Minkowski-difference
				// overlap) -- harmless for callers that only read Distance/Fraction (e.g.
				// CharacterMover.CastMover), but a caller that also needs a normal (e.g. a ground
				// probe checking slope) would otherwise silently get (0,0,0) and misread it as "not
				// standable." A same-point fallback from the witness points is still meaningful even
				// under deep overlap, so populate it rather than leaving it blank.
				GJK.ComputeWitnessPoints(simplex, out var localPointA, out var localPointB);
				distanceOutput.PointA = localPointA;
				distanceOutput.PointB = localPointB;
				distanceOutput.Normal = FVector3.NormalizeSafe(localPointB - localPointA, FVector3.Up);
				return distanceOutput;
			}

			// Assure distance progression.
			var oldDistanceSq = distanceSq;

			FVector3 closestPoint;
			switch (simplex.Count) {
				case 1:
					closestPoint = simplex.V0.W;
					break;

				case 2:
					closestPoint = FVector3.Blend2(simplex.V0.A, simplex.V0.W, simplex.V1.A, simplex.V1.W);
					break;

				case 3:
					closestPoint = FVector3.Blend3(simplex.V0.A, simplex.V0.W, simplex.V1.A, simplex.V1.W, simplex.V2.A, simplex.V2.W);
					break;

				case 4:
					closestPoint = FVector3.Blend2(simplex.V0.A, simplex.V0.W, simplex.V1.A, simplex.V1.W)
						+ FVector3.Blend2(simplex.V2.A, simplex.V2.W, simplex.V3.A, simplex.V3.W);
					break;

				default:
					closestPoint = FVector3.Zero;
					break;
			}

			distanceSq = FVector3.Dot(closestPoint, closestPoint);

			if (distanceSq >= oldDistanceSq) {
				// No progress - reconstruct the last simplex.
				simplex = backup;
				break;
			}

			// Build a new tentative support point.
			FVector3 searchDirection;
			switch (simplex.Count) {
				case 1:
					// v = -A
					searchDirection = -simplex.V0.W;
					break;

				case 2: {
						// v = (AB x AO) x AB
						var a = simplex.V0.W;
						var b = simplex.V1.W;
						// box3d's own b3ComputeSimplexSearchDirection (distance.c) crosses the raw,
						// unnormalized edge vector here -- safe for box3d's float32 (a large product
						// just loses relative precision gracefully), not for Fixed32: two chained
						// cross products of edges spanning tens of units (an ordinary-sized static
						// shape, not a pathological input) compound to magnitudes that silently
						// overflow Fixed32's ~32767 ceiling (FP's multiply operator computes the
						// 64-bit product correctly but casts back to a 32-bit raw value unchecked),
						// corrupting the search direction and derailing the whole GJK iteration.
						// Direction-only usage (GJK.GetProxySupport only needs the argmax of a dot
						// product, not searchDirection's actual magnitude), so normalizing the edge
						// first is free: it bounds the result to the magnitude of `a` alone instead
						// of the square of the edge length, and, as a bonus, makes the near-zero
						// degenerate check below purely about the angle between the vectors instead
						// of being conflated with their scale. Same fix already applied once in this
						// codebase for the same box3d-raw-cross-product-on-large-shapes issue -- see
						// Manifold.IsMinkowskiFace's remarks.
						var ab = FVector3.NormalizeSafe(b - a);
						searchDirection = FVector3.Cross(FVector3.Cross(ab, -a), ab);
						break;
					}

				case 3: {
						// v = AB x AC or v = AC x AB
						var a = simplex.V0.W;
						var b = simplex.V1.W;
						var c = simplex.V2.W;
						// See the case 2 remarks just above -- same box3d-raw-edge-cross-product
						// overflow risk, same fix (normalize the edges; only the sign/direction of
						// n matters here, not its magnitude).
						var ab = FVector3.NormalizeSafe(b - a);
						var ac = FVector3.NormalizeSafe(c - a);
						var n = FVector3.Cross(ab, ac);
						searchDirection = FVector3.Dot(n, a) < FP.Zero ? n : -n;
						break;
					}

				default:
					searchDirection = FVector3.Zero;
					break;
			}

			if (FVector3.LengthSqr(searchDirection) < FP.CalculationsEpsilonSqr) {
				// The origin is probably contained by a line segment or triangle. The shapes are
				// overlapped. See the MaxSimplexVertices branch above's remarks on why Normal is
				// populated here too (a same-point fallback), not left at its zero default.
				GJK.ComputeWitnessPoints(simplex, out var localPointA, out var localPointB);
				distanceOutput.PointA = localPointA;
				distanceOutput.PointB = localPointB;
				distanceOutput.Normal = FVector3.NormalizeSafe(localPointB - localPointA, FVector3.Up);
				return distanceOutput;
			}

			normal = -searchDirection;

			// Get new support points.
			var indexA = GJK.GetProxySupport(proxyA, -searchDirection);
			var supportA = pointsA[indexA];
			var searchDirection2 = mt * searchDirection;
			var indexB = GJK.GetProxySupport(proxyB, searchDirection2);
			var supportB = m * pointsB[indexB] + xf.Position;

			// Save the current simplex; adding the new vertex can fail if we detect cycling.
			backup = simplex;

			// Check for duplicate support points. This is the main termination criterion.
			var duplicate = false;
			for (var i = 0; i < simplex.Count; i++) {
				ref var vertex = ref Simplex.VertexAt(ref simplex, i);
				if (vertex.IndexA == indexA && vertex.IndexB == indexB) {
					duplicate = true;
					break;
				}
			}

			if (duplicate) {
				break;
			}

			ref var newVertex = ref Simplex.VertexAt(ref simplex, simplex.Count);
			newVertex.IndexA = indexA;
			newVertex.IndexB = indexB;
			newVertex.WA = supportA;
			newVertex.WB = supportB;
			newVertex.W = supportB - supportA;
			simplex.Count += 1;
		}

		normal = FVector3.Normalize(normal);
		if (!FVector3.IsNormalized(normal, NormalTolerance)) {
			// Treat as overlap -- a third "no well-defined separating normal" exit (the main loop
			// ended via no-progress/cycling/duplicate-vertex rather than one of the two explicit
			// overlap returns above), which left PointA/PointB/Normal at their defaults too. Same
			// fallback as those two: witness points from whatever simplex the loop landed on are
			// still meaningful under overlap, so populate them instead of returning them blank.
			GJK.ComputeWitnessPoints(simplex, out var localPointA, out var localPointB);
			distanceOutput.PointA = localPointA;
			distanceOutput.PointB = localPointB;
			distanceOutput.Normal = FVector3.NormalizeSafe(localPointB - localPointA, FVector3.Up);
			return distanceOutput;
		}

		// Build witness points and save the cache.
		GJK.ComputeWitnessPoints(simplex, out var finalPointA, out var finalPointB);
		GJK.WriteCache(ref cache, simplex);

		// Results stay in frame A.
		distanceOutput.PointA = finalPointA;
		distanceOutput.PointB = finalPointB;
		distanceOutput.Distance = FVector3.Distance(finalPointA, finalPointB);
		distanceOutput.Normal = normal;
		distanceOutput.Iterations = iteration;
		distanceOutput.SimplexCount = simplexIndex;

		// Apply radii if requested.
		if (input.UseRadii) {
			var rA = proxyA.Radius;
			var rB = proxyB.Radius;
			distanceOutput.Distance = FP.Max(FP.Zero, distanceOutput.Distance - rA - rB);

			// Keep closest points on the perimeter even if overlapped, so points move smoothly.
			distanceOutput.PointA += rA * normal;
			distanceOutput.PointB -= rB * normal;
		}

		return distanceOutput;
	}

	/// <summary>
	/// Perform a linear shape cast of shape B moving and shape A fixed. Determines the hit point,
	/// normal, and translation fraction. The query runs in frame A, so the hit point and normal are
	/// returned in frame A. Initially touching shapes are a miss unless <see cref="ShapeCastPairInput.CanEncroach"/> is set.
	/// </summary>
	public static CastOutput ShapeCast(ShapeCastPairInput input) {
		var linearSlop = B3Config.LinearSlop;
		var totalRadius = input.ProxyA.Radius + input.ProxyB.Radius;
		var target = FP.Max(linearSlop, totalRadius - linearSlop);
		var tolerance = FP.FromRatio(1, 4) * linearSlop;

		var cache = SimplexCache.Empty;

		var alpha = FP.Zero;

		var distanceInput = new DistanceInput {
			ProxyA = input.ProxyA,
			ProxyB = input.ProxyB,
			UseRadii = false,
			Transform = input.Transform,
		};

		var delta2 = input.TranslationB;
		var output = new CastOutput { TriangleIndex = -1 };

		const int maxIterations = 20;
		for (var iteration = 0; iteration < maxIterations; iteration++) {
			output.Iterations += 1;

			var distanceOutput = ShapeDistance(distanceInput, ref cache);

			if (distanceOutput.Distance < target + tolerance) {
				if (iteration == 0) {
					if (input.CanEncroach && distanceOutput.Distance > 2 * linearSlop) {
						target = distanceOutput.Distance - linearSlop;
					}
					else {
						// Initial overlap.
						output.Hit = true;

						// Compute a common point.
						var c1 = distanceOutput.PointA + input.ProxyA.Radius * distanceOutput.Normal;
						var c2 = distanceOutput.PointB - input.ProxyB.Radius * distanceOutput.Normal;
						output.Point = FVector3.Blend2(FP.Half, c1, FP.Half, c2);
						return output;
					}
				}
				else {
					if (distanceOutput.Distance > FP.Zero && !FVector3.IsNormalized(distanceOutput.Normal, NormalTolerance)) {
						// Numerical problem, likely extreme input.
						return output;
					}

					output.Fraction = alpha;
					output.Point = distanceOutput.PointA + input.ProxyA.Radius * distanceOutput.Normal;
					output.Normal = distanceOutput.Normal;
					output.Hit = true;
					return output;
				}
			}

			// Check if the shapes are approaching each other.
			var denominator = FVector3.Dot(delta2, distanceOutput.Normal);
			if (denominator >= FP.Zero) {
				// Miss.
				return output;
			}

			// Advance the sweep.
			alpha += (target - distanceOutput.Distance) / denominator;
			if (alpha >= input.MaxFraction) {
				// Success.
				return output;
			}

			distanceInput.Transform.Position = input.Transform.Position + alpha * delta2;
		}

		// Failure.
		return output;
	}
}
