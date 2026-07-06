using Fixed32;

namespace Space.GameCore;

/// <summary>
/// GJK simplex solving, ported from box3d's distance.c. Internal implementation detail of
/// <see cref="Distance"/>. Distinct from and unrelated to the generic <c>Fixed32.GJK</c>
/// (ISupportMappable-based, no closest-point-when-separated query) shipped by the fixed-point library.
/// </summary>
internal static class GJK {
	private static FP ScalarTripleProduct(FVector3 a, FVector3 b, FVector3 c) {
		return FVector3.Dot(a, FVector3.Cross(b, c));
	}

	/// <summary>
	/// Find the index of the proxy point farthest along axis. Shifts to the first vertex as the
	/// origin first, for precision, since proxy points can be far from the origin.
	/// </summary>
	internal static int GetProxySupport(ShapeProxy proxy, FVector3 axis) {
		var points = proxy.Points!;
		var count = points.Length;

		var origin = points[0];
		var maxIndex = 0;
		var maxProjection = FP.Zero;

		for (var index = 1; index < count; index++) {
			var projection = FVector3.Dot(axis, points[index] - origin);
			if (projection > maxProjection) {
				maxIndex = index;
				maxProjection = projection;
			}
		}

		return maxIndex;
	}

	private static void BarycentricCoordsEdge(FVector3 a, FVector3 b, out FP u, out FP v, out FP divisor) {
		var ab = b - a;
		divisor = FVector3.Dot(ab, ab);
		u = FVector3.Dot(b, ab);
		v = -FVector3.Dot(a, ab);
	}

	private static void BarycentricCoordsTri(FVector3 a, FVector3 b, FVector3 c, out FP u, out FP v, out FP w, out FP divisor) {
		var ab = b - a;
		var ac = c - a;

		var bXc = FVector3.Cross(b, c);
		var cXa = FVector3.Cross(c, a);
		var aXb = FVector3.Cross(a, b);

		var abXac = FVector3.Cross(ab, ac);

		divisor = FVector3.Dot(abXac, abXac);
		u = FVector3.Dot(bXc, abXac);
		v = FVector3.Dot(cXa, abXac);
		w = FVector3.Dot(aXb, abXac);
	}

	private static void BarycentricCoordsTet(
		FVector3 a, FVector3 b, FVector3 c, FVector3 d,
		out FP u, out FP v, out FP w, out FP x, out FP divisor) {
		var ab = b - a;
		var ac = c - a;
		var ad = d - a;

		// Last element is the divisor, forced to be positive.
		var rawDivisor = ScalarTripleProduct(ab, ac, ad);
		var sign = rawDivisor < FP.Zero ? -FP.One : FP.One;

		u = sign * ScalarTripleProduct(b, c, d);
		v = sign * ScalarTripleProduct(a, d, c);
		w = sign * ScalarTripleProduct(a, b, d);
		x = sign * ScalarTripleProduct(a, c, b);
		divisor = sign * rawDivisor;
	}

	internal static FP GetMetric(Simplex simplex) {
		switch (simplex.Count) {
			case 1:
				return FP.Zero;

			case 2:
				return FVector3.Distance(simplex.V0.W, simplex.V1.W);

			case 3: {
					var a = simplex.V0.W;
					var b = simplex.V1.W;
					var c = simplex.V2.W;
					return FVector3.Length(FVector3.Cross(b - a, c - a)) / 2;
				}

			case 4: {
					var a = simplex.V0.W;
					var b = simplex.V1.W;
					var c = simplex.V2.W;
					var d = simplex.V3.W;
					return ScalarTripleProduct(b - a, c - a, d - a) / 6;
				}

			default:
				return FP.Zero;
		}
	}

	internal static void WriteCache(ref SimplexCache cache, Simplex simplex) {
		var count = simplex.Count;
		cache.Metric = GetMetric(simplex);
		cache.Count = (ushort)count;
		for (var index = 0; index < count; index++) {
			ref var vertex = ref Simplex.VertexAt(ref simplex, index);
			cache.IndexA[index] = (byte)vertex.IndexA;
			cache.IndexB[index] = (byte)vertex.IndexB;
		}
	}

	internal static bool SolveSimplex2(ref Simplex simplex) {
		ref var v0 = ref Simplex.VertexAt(ref simplex, 0);
		ref var v1 = ref Simplex.VertexAt(ref simplex, 1);

		var a = v0.W;
		var b = v1.W;
		var ab = b - a;

		var divisor = FVector3.Dot(ab, ab);
		var u = FVector3.Dot(b, ab);
		var v = -FVector3.Dot(a, ab);

		// V(A)
		if (v <= FP.Zero) {
			simplex.Count = 1;
			v0.A = FP.One;
			return true;
		}

		// V(B)
		if (u <= FP.Zero) {
			simplex.Count = 1;
			v0 = v1;
			v0.A = FP.One;
			return true;
		}

		// Edge region
		if (divisor <= FP.Zero) {
			return false;
		}

		// VR(AB)
		var denominator = FP.One / divisor;
		v0.A = denominator * u;
		v1.A = denominator * v;

		return true;
	}

	internal static bool SolveSimplex3(ref Simplex simplex) {
		// Copy the simplex vertices; the slots get overwritten as regions are picked below.
		var vertex1 = simplex.V0;
		var vertex2 = simplex.V1;
		var vertex3 = simplex.V2;

		BarycentricCoordsEdge(vertex1.W, vertex2.W, out var wAB0, out var wAB1, out var wAB2);
		BarycentricCoordsEdge(vertex2.W, vertex3.W, out var wBC0, out var wBC1, out var wBC2);
		BarycentricCoordsEdge(vertex3.W, vertex1.W, out var wCA0, out var wCA1, out var wCA2);

		// VR(A)
		if (wAB1 <= FP.Zero && wCA0 <= FP.Zero) {
			simplex.Count = 1;
			simplex.V0 = vertex1;
			simplex.V0.A = FP.One;
			return true;
		}

		// VR(B)
		if (wBC1 <= FP.Zero && wAB0 <= FP.Zero) {
			simplex.Count = 1;
			simplex.V0 = vertex2;
			simplex.V0.A = FP.One;
			return true;
		}

		// VR(C)
		if (wCA1 <= FP.Zero && wBC0 <= FP.Zero) {
			simplex.Count = 1;
			simplex.V0 = vertex3;
			simplex.V0.A = FP.One;
			return true;
		}

		BarycentricCoordsTri(vertex1.W, vertex2.W, vertex3.W, out var wABC0, out var wABC1, out var wABC2, out var wABC3);

		// VR(AB)
		if (wABC2 <= FP.Zero && wAB0 > FP.Zero && wAB1 > FP.Zero) {
			simplex.Count = 2;
			simplex.V0 = vertex1;
			simplex.V1 = vertex2;

			if (wAB2 <= FP.Zero) {
				return false;
			}

			simplex.V0.A = wAB0 / wAB2;
			simplex.V1.A = wAB1 / wAB2;
			return true;
		}

		// VR(BC)
		if (wABC0 <= FP.Zero && wBC0 > FP.Zero && wBC1 > FP.Zero) {
			simplex.Count = 2;
			simplex.V0 = vertex2;
			simplex.V1 = vertex3;

			if (wBC2 <= FP.Zero) {
				return false;
			}

			simplex.V0.A = wBC0 / wBC2;
			simplex.V1.A = wBC1 / wBC2;
			return true;
		}

		// VR(CA)
		if (wABC1 <= FP.Zero && wCA0 > FP.Zero && wCA1 > FP.Zero) {
			simplex.Count = 2;
			simplex.V0 = vertex3;
			simplex.V1 = vertex1;

			if (wCA2 <= FP.Zero) {
				return false;
			}

			simplex.V0.A = wCA0 / wCA2;
			simplex.V1.A = wCA1 / wCA2;
			return true;
		}

		// Face region. vertex1/2/3 were never written back over simplex.V0/V1/V2, so they're
		// still there unchanged.
		if (wABC3 <= FP.Zero) {
			return false;
		}

		simplex.V0.A = wABC0 / wABC3;
		simplex.V1.A = wABC1 / wABC3;
		simplex.V2.A = wABC2 / wABC3;
		return true;
	}

	internal static bool SolveSimplex4(ref Simplex simplex) {
		var vertexA = simplex.V0;
		var vertexB = simplex.V1;
		var vertexC = simplex.V2;
		var vertexD = simplex.V3;

		BarycentricCoordsEdge(vertexA.W, vertexB.W, out var wAB0, out var wAB1, out var wAB2);
		BarycentricCoordsEdge(vertexA.W, vertexC.W, out var wAC0, out var wAC1, out var wAC2);
		BarycentricCoordsEdge(vertexA.W, vertexD.W, out var wAD0, out var wAD1, out var wAD2);
		BarycentricCoordsEdge(vertexB.W, vertexC.W, out var wBC0, out var wBC1, out var wBC2);
		BarycentricCoordsEdge(vertexC.W, vertexD.W, out var wCD0, out var wCD1, out var wCD2);
		BarycentricCoordsEdge(vertexD.W, vertexB.W, out var wDB0, out var wDB1, out var wDB2);

		// VR(A)
		if (wAB1 <= FP.Zero && wAC1 <= FP.Zero && wAD1 <= FP.Zero) {
			simplex.Count = 1;
			simplex.V0 = vertexA;
			simplex.V0.A = FP.One;
			return true;
		}

		// VR(B)
		if (wAB0 <= FP.Zero && wDB0 <= FP.Zero && wBC1 <= FP.Zero) {
			simplex.Count = 1;
			simplex.V0 = vertexB;
			simplex.V0.A = FP.One;
			return true;
		}

		// VR(C)
		if (wAC0 <= FP.Zero && wBC0 <= FP.Zero && wCD1 <= FP.Zero) {
			simplex.Count = 1;
			simplex.V0 = vertexC;
			simplex.V0.A = FP.One;
			return true;
		}

		// VR(D)
		if (wAD0 <= FP.Zero && wCD0 <= FP.Zero && wDB1 <= FP.Zero) {
			simplex.Count = 1;
			simplex.V0 = vertexD;
			simplex.V0.A = FP.One;
			return true;
		}

		BarycentricCoordsTri(vertexA.W, vertexC.W, vertexB.W, out var wACB0, out var wACB1, out var wACB2, out var wACB3);
		BarycentricCoordsTri(vertexA.W, vertexB.W, vertexD.W, out var wABD0, out var wABD1, out var wABD2, out var wABD3);
		BarycentricCoordsTri(vertexA.W, vertexD.W, vertexC.W, out var wADC0, out var wADC1, out var wADC2, out var wADC3);
		BarycentricCoordsTri(vertexB.W, vertexC.W, vertexD.W, out var wBCD0, out var wBCD1, out var wBCD2, out var wBCD3);

		// VR(AB)
		if (wABD2 <= FP.Zero && wACB1 <= FP.Zero && wAB0 > FP.Zero && wAB1 > FP.Zero) {
			simplex.Count = 2;
			simplex.V0 = vertexA;
			simplex.V1 = vertexB;

			if (wAB2 <= FP.Zero) {
				return false;
			}

			simplex.V0.A = wAB0 / wAB2;
			simplex.V1.A = wAB1 / wAB2;
			return true;
		}

		// VR(AC)
		if (wACB2 <= FP.Zero && wADC1 <= FP.Zero && wAC0 > FP.Zero && wAC1 > FP.Zero) {
			simplex.Count = 2;
			simplex.V0 = vertexA;
			simplex.V1 = vertexC;

			if (wAC2 <= FP.Zero) {
				return false;
			}

			simplex.V0.A = wAC0 / wAC2;
			simplex.V1.A = wAC1 / wAC2;
			return true;
		}

		// VR(AD)
		if (wADC2 <= FP.Zero && wABD1 <= FP.Zero && wAD0 > FP.Zero && wAD1 > FP.Zero) {
			simplex.Count = 2;
			simplex.V0 = vertexA;
			simplex.V1 = vertexD;

			if (wAD2 <= FP.Zero) {
				return false;
			}

			simplex.V0.A = wAD0 / wAD2;
			simplex.V1.A = wAD1 / wAD2;
			return true;
		}

		// VR(BC)
		if (wACB0 <= FP.Zero && wBCD2 <= FP.Zero && wBC0 > FP.Zero && wBC1 > FP.Zero) {
			simplex.Count = 2;
			simplex.V0 = vertexB;
			simplex.V1 = vertexC;

			if (wBC2 <= FP.Zero) {
				return false;
			}

			simplex.V0.A = wBC0 / wBC2;
			simplex.V1.A = wBC1 / wBC2;
			return true;
		}

		// VR(CD)
		if (wADC0 <= FP.Zero && wBCD0 <= FP.Zero && wCD0 > FP.Zero && wCD1 > FP.Zero) {
			simplex.Count = 2;
			simplex.V0 = vertexC;
			simplex.V1 = vertexD;

			if (wCD2 <= FP.Zero) {
				return false;
			}

			simplex.V0.A = wCD0 / wCD2;
			simplex.V1.A = wCD1 / wCD2;
			return true;
		}

		// VR(DB)
		if (wABD0 <= FP.Zero && wBCD1 <= FP.Zero && wDB0 > FP.Zero && wDB1 > FP.Zero) {
			simplex.Count = 2;
			simplex.V0 = vertexD;
			simplex.V1 = vertexB;

			if (wDB2 <= FP.Zero) {
				return false;
			}

			simplex.V0.A = wDB0 / wDB2;
			simplex.V1.A = wDB1 / wDB2;
			return true;
		}

		// Face regions
		BarycentricCoordsTet(vertexA.W, vertexB.W, vertexC.W, vertexD.W,
			out var wABCD0, out var wABCD1, out var wABCD2, out var wABCD3, out var wABCD4);

		// VR(ACB)
		if (wABCD3 < FP.Zero && wACB0 > FP.Zero && wACB1 > FP.Zero && wACB2 > FP.Zero) {
			simplex.Count = 3;
			simplex.V0 = vertexA;
			simplex.V1 = vertexC;
			simplex.V2 = vertexB;

			if (wACB3 <= FP.Zero) {
				return false;
			}

			simplex.V0.A = wACB0 / wACB3;
			simplex.V1.A = wACB1 / wACB3;
			simplex.V2.A = wACB2 / wACB3;
			return true;
		}

		// VR(ABD)
		if (wABCD2 < FP.Zero && wABD0 > FP.Zero && wABD1 > FP.Zero && wABD2 > FP.Zero) {
			simplex.Count = 3;
			simplex.V0 = vertexA;
			simplex.V1 = vertexB;
			simplex.V2 = vertexD;

			if (wABD3 <= FP.Zero) {
				return false;
			}

			simplex.V0.A = wABD0 / wABD3;
			simplex.V1.A = wABD1 / wABD3;
			simplex.V2.A = wABD2 / wABD3;
			return true;
		}

		// VR(ADC)
		if (wABCD1 < FP.Zero && wADC0 > FP.Zero && wADC1 > FP.Zero && wADC2 > FP.Zero) {
			simplex.Count = 3;
			simplex.V0 = vertexA;
			simplex.V1 = vertexD;
			simplex.V2 = vertexC;

			if (wADC3 <= FP.Zero) {
				return false;
			}

			simplex.V0.A = wADC0 / wADC3;
			simplex.V1.A = wADC1 / wADC3;
			simplex.V2.A = wADC2 / wADC3;
			return true;
		}

		// VR(BCD)
		if (wABCD0 < FP.Zero && wBCD0 > FP.Zero && wBCD1 > FP.Zero && wBCD2 > FP.Zero) {
			simplex.Count = 3;
			simplex.V0 = vertexB;
			simplex.V1 = vertexC;
			simplex.V2 = vertexD;

			if (wBCD3 <= FP.Zero) {
				return false;
			}

			simplex.V0.A = wBCD0 / wBCD3;
			simplex.V1.A = wBCD1 / wBCD3;
			simplex.V2.A = wBCD2 / wBCD3;
			return true;
		}

		// Inside the tetrahedron.
		if (wABCD4 <= FP.Zero) {
			return false;
		}

		// VR(ABCD). vertexA/B/C/D were never written back, so simplex.V0..V3 are still the originals.
		simplex.V0.A = wABCD0 / wABCD4;
		simplex.V1.A = wABCD1 / wABCD4;
		simplex.V2.A = wABCD2 / wABCD4;
		simplex.V3.A = wABCD3 / wABCD4;
		return true;
	}

	internal static void ComputeWitnessPoints(Simplex simplex, out FVector3 vertexA, out FVector3 vertexB) {
		switch (simplex.Count) {
			case 1:
				vertexA = simplex.V0.WA;
				vertexB = simplex.V0.WB;
				break;

			case 2:
				vertexA = FVector3.Blend2(simplex.V0.A, simplex.V0.WA, simplex.V1.A, simplex.V1.WA);
				vertexB = FVector3.Blend2(simplex.V0.A, simplex.V0.WB, simplex.V1.A, simplex.V1.WB);
				break;

			case 3:
				vertexA = FVector3.Blend3(simplex.V0.A, simplex.V0.WA, simplex.V1.A, simplex.V1.WA, simplex.V2.A, simplex.V2.WA);
				vertexB = FVector3.Blend3(simplex.V0.A, simplex.V0.WB, simplex.V1.A, simplex.V1.WB, simplex.V2.A, simplex.V2.WB);
				break;

			case 4: {
					// Force identical points and zero distance.
					var sum = FVector3.Blend2(simplex.V0.A, simplex.V0.WA, simplex.V1.A, simplex.V1.WA)
						+ FVector3.Blend2(simplex.V2.A, simplex.V2.WA, simplex.V3.A, simplex.V3.WA);
					vertexA = sum;
					vertexB = sum;
					break;
				}

			default:
				vertexA = FVector3.Zero;
				vertexB = FVector3.Zero;
				break;
		}
	}
}
