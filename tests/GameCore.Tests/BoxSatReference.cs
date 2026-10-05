using System.Linq.Expressions;
using System.Reflection;
using Fixed;
using Fixed32;

namespace Space.GameCore.Tests;

/// <summary>
/// Frozen pre-B box SAT, including the large-ground support fix. Only the unchanged contact
/// builders/transforms are shared (through test-only delegates); no production SAT query is used.
/// Keep the repeated arithmetic here: this is the differential oracle, not a second optimized path.
/// </summary>
internal static class BoxSatReference {
	internal delegate (FP Separation, int EdgeA, int EdgeB) EdgeQuery(FVector3 heA, FVector3 centerB, FQuaternion rotationB, FVector3 heB);
	private delegate (FP Separation, int FaceIndex) FaceQuery(FVector3 heA, FVector3 centerB, FQuaternion rotationB, FVector3 heB);
	// Adapt the private result type outside the timed loop. Both timed queries then use one
	// delegate call with the same inputs/output, without contact-builder dispatch differences.
	internal static readonly EdgeQuery OptimizedEdge = BindQuery<EdgeQuery>("QueryEdgeDirectionsBoxBox", "Separation", "EdgeA", "EdgeB");
	private static readonly FaceQuery s_optimizedFace = BindQuery<FaceQuery>("QueryFaceDirectionsBoxBox", "Separation", "FaceIndex");
	private static T BindQuery<T>(string name, params string[] fields) where T : Delegate {
		var method = typeof(Manifold).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
		var args = method.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
		var value = Expression.Variable(method.ReturnType);
		var tupleType = typeof(T).GetMethod("Invoke")!.ReturnType;
		var tuple = Expression.New(tupleType.GetConstructors().Single(), fields.Select(f => Expression.Field(value, f)));
		return Expression.Lambda<T>(Expression.Block([value], Expression.Assign(value, Expression.Call(method, args)), tuple), args).Compile();
	}
	private delegate bool FaceBuilder(FVector3 heRef, FVector3 centerInc, FQuaternion rotationInc, FVector3 heInc, int face, ref Manifold manifold);
	private delegate bool EdgeBuilder(FVector3 pA, FVector3 eA, FVector3 pB, FVector3 eB, int edgeA, int edgeB, ref Manifold manifold);
	private delegate void FlipTransform(ref Manifold manifold, FVector3 center, FQuaternion rotation);
	private delegate Manifold BoxTransform(Manifold manifold, FVector3 center, FQuaternion rotation);
	private static T Bind<T>(string name) where T : Delegate => typeof(Manifold)
		.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<T>();
	private static readonly FaceBuilder s_face = Bind<FaceBuilder>("BuildBoxFaceContact");
	private static readonly EdgeBuilder s_edge = Bind<EdgeBuilder>("BuildBoxEdgeContact");
	private static readonly FlipTransform s_flip = Bind<FlipTransform>("TransformManifoldBToA");
	private static readonly BoxTransform s_transform = Bind<BoxTransform>("TransformBoxManifold");

	public static Manifold Collide(in Shape a, FWorldTransform xfA, in Shape b, FWorldTransform xfB) => CollideWithQueries(a, xfA, b, xfB, QueryFace, QueryEdge);
	internal static Manifold CollideWithOptimizedSat(in Shape a, FWorldTransform xfA, in Shape b, FWorldTransform xfB) => CollideWithQueries(a, xfA, b, xfB, s_optimizedFace, OptimizedEdge);
	private static Manifold CollideWithQueries(in Shape a, FWorldTransform xfA, in Shape b, FWorldTransform xfB, FaceQuery queryFace, EdgeQuery queryEdge) {
		var xfBinA = FWorldTransform.InvMul(xfA, xfB);
		var centerB = FTransform.TransformPoint(xfBinA, b.HullShape.Center);
		var rotationB = xfBinA.Rotation * b.HullShape.Rotation;
		return CollideBoxBox(a.HullShape.Center, a.HullShape.Rotation, a.HullShape.HalfExtents,
			centerB, rotationB, b.HullShape.HalfExtents, queryFace, queryEdge);
	}

	private static Manifold CollideBoxBox(FVector3 centerA, FQuaternion rotationA, FVector3 heA, FVector3 centerB, FQuaternion rotationB, FVector3 heB, FaceQuery queryFace, EdgeQuery queryEdge) {
		var invRotationA = FQuaternion.Inverse(rotationA);
		var localCenterB = invRotationA * (centerB - centerA);
		var localRotationB = invRotationA * rotationB;
		var manifold = new Manifold();
		var faceQueryA = queryFace(heA, localCenterB, localRotationB, heB);
		if (faceQueryA.Separation > B3Config.SpeculativeDistance)
			return s_transform(manifold, centerA, rotationA);
		var invLocalRotationB = FQuaternion.Inverse(localRotationB);
		var centerAInB = -(invLocalRotationB * localCenterB);
		var faceQueryB = queryFace(heB, centerAInB, invLocalRotationB, heA);
		if (faceQueryB.Separation > B3Config.SpeculativeDistance)
			return s_transform(manifold, centerA, rotationA);
		var edgeQuery = queryEdge(heA, localCenterB, localRotationB, heB);
		if (edgeQuery.Separation > B3Config.SpeculativeDistance)
			return s_transform(manifold, centerA, rotationA);
		if (faceQueryB.Separation > faceQueryA.Separation + FP.Half * B3Config.LinearSlop) {
			var localManifold = new Manifold();
			s_face(heB, centerAInB, invLocalRotationB, heA, faceQueryB.FaceIndex, ref localManifold);
			s_flip(ref localManifold, localCenterB, localRotationB);
			manifold = localManifold;
		} else {
			s_face(heA, localCenterB, localRotationB, heB, faceQueryA.FaceIndex, ref manifold);
		}
		if (edgeQuery.EdgeA < 0)
			return s_transform(manifold, centerA, rotationA);
		var clippedFaceSeparation = manifold.MinSeparation();
		var relEdgeTolerance = FP.FromRatio(90, 100);
		var absTolerance = FP.Half * B3Config.LinearSlop;
		if (manifold.PointCount == 0 || edgeQuery.Separation > relEdgeTolerance * clippedFaceSeparation + absTolerance) {
			var edgeA = Hull.Edges[edgeQuery.EdgeA];
			var pA = Hull.LocalCorner(heA, edgeA.V0);
			var qA = Hull.LocalCorner(heA, edgeA.V1);
			var edgeB = Hull.Edges[edgeQuery.EdgeB];
			var pB = localCenterB + localRotationB * Hull.LocalCorner(heB, edgeB.V0);
			var qB = localCenterB + localRotationB * Hull.LocalCorner(heB, edgeB.V1);
			var edgeManifold = new Manifold();
			if (s_edge(pA, qA - pA, pB, qB - pB, edgeQuery.EdgeA, edgeQuery.EdgeB, ref edgeManifold))
				manifold = edgeManifold;
		}
		return s_transform(manifold, centerA, rotationA);
	}

	private static (FP Separation, int FaceIndex) QueryFace(FVector3 heA, FVector3 centerB, FQuaternion rotationB, FVector3 heB) {
		var best = (Separation: FP.MinValue, FaceIndex: 0);
		for (var f = 0; f < 6; f++) {
			var normal = Hull.Faces[f].Normal;
			var offset = FVector3.Dot(FVector3.AbsComponents(normal), heA);
			var localDirection = FQuaternion.Inverse(rotationB) * -normal;
			var supportLocal = Hull.SupportLocal(heB, localDirection);
			var supportWorld = centerB + rotationB * supportLocal;
			var separation = FVector3.Dot(normal, supportWorld) - offset;
			if (separation > best.Separation)
				best = (separation, f);
		}
		return best;
	}

	internal static (FP Separation, int EdgeA, int EdgeB) QueryEdge(FVector3 heA, FVector3 centerB, FQuaternion rotationB, FVector3 heB) {
		var best = (Separation: FP.MinValue, EdgeA: -1, EdgeB: -1);
		for (var j = 0; j < Hull.Edges.Length; j++) {
			var edgeB = Hull.Edges[j];
			var pB = centerB + rotationB * Hull.LocalCorner(heB, edgeB.V0);
			var qB = centerB + rotationB * Hull.LocalCorner(heB, edgeB.V1);
			var eB = qB - pB;
			var uB = rotationB * Hull.Faces[edgeB.FaceA].Normal;
			var vB = rotationB * Hull.Faces[edgeB.FaceB].Normal;
			for (var i = 0; i < Hull.Edges.Length; i++) {
				var edgeA = Hull.Edges[i];
				var pA = Hull.LocalCorner(heA, edgeA.V0);
				var qA = Hull.LocalCorner(heA, edgeA.V1);
				var eA = qA - pA;
				var uA = Hull.Faces[edgeA.FaceA].Normal;
				var vA = Hull.Faces[edgeA.FaceB].Normal;
				if (!IsMinkowskiFace(uA, vA, eA, uB, vB, eB))
					continue;
				if (!TryEdgeAxis(heA, centerB, rotationB, heB, eA, eB, out var separation))
					continue;
				if (separation > best.Separation)
					best = (separation, i, j);
			}
		}
		return best;
	}

	private static bool IsMinkowskiFace(FVector3 uA, FVector3 vA, FVector3 eA, FVector3 uB, FVector3 vB, FVector3 eB) {
		var normEA = FVector3.NormalizeSafe(eA);
		var normEB = FVector3.NormalizeSafe(eB);
		var cba = FVector3.Dot(uB, normEA);
		var dba = FVector3.Dot(vB, normEA);
		var adc = -FVector3.Dot(uA, normEB);
		var bdc = -FVector3.Dot(vA, normEB);
		var epsilon = FP.FromRatio(1, 100);
		return cba * dba < -epsilon && adc * bdc < -epsilon && cba * bdc > epsilon;
	}

	private static bool TryEdgeAxis(FVector3 heA, FVector3 centerB, FQuaternion rotationB, FVector3 heB, FVector3 eA, FVector3 eB, out FP separation) {
		var u = FVector3.Cross(FVector3.NormalizeSafe(eA), FVector3.NormalizeSafe(eB));
		var lengthSqr = FVector3.LengthSqr(u);
		var toleranceSqr = FP.FromRatio(25, 1000000);
		if (lengthSqr < toleranceSqr || lengthSqr < FP.CalculationsEpsilonSqr) {
			separation = FP.Zero;
			return false;
		}
		var n = u / FP.Sqrt(lengthSqr);
		if (FVector3.Dot(n, centerB) < FP.Zero)
			n = -n;
		var offsetA = FVector3.Dot(FVector3.AbsComponents(n), heA);
		var supportLocal = Hull.SupportLocal(heB, FQuaternion.Inverse(rotationB) * -n);
		var supportWorld = centerB + rotationB * supportLocal;
		separation = FVector3.Dot(n, supportWorld) - offsetA;
		return true;
	}
}
