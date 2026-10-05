using System.Reflection;
using Fixed;
using Fixed32;

namespace Space.GameCore.Tests;

/// <summary>
/// Test-only transcription of b3CollideHulls' reuse rules. Uses unchanged clipping/contact builders,
/// but independently evaluates the cached feature, including the port's existing fixed-point guards
/// and full-box-support correction. Never calls production cache evaluation or full SAT.
/// </summary>
internal static class BoxSatCacheReference {
	internal enum Rejection { None, Feature, Clip, Segments, Drift }
	private delegate bool FaceBuilder(FVector3 a, FVector3 p, FQuaternion q, FVector3 b, int index, ref Manifold m, out FP separation);
	private delegate bool EdgeBuilder(FVector3 a, FVector3 ea, FVector3 b, FVector3 eb, int ia, int ib, ref Manifold m);
	private static readonly FaceBuilder s_face = typeof(Manifold).GetMethod("BuildBoxFaceContactCore", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<FaceBuilder>();
	private static readonly EdgeBuilder s_edge = typeof(Manifold).GetMethod("BuildBoxEdgeContact", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<EdgeBuilder>();

	internal static BoxSatResult Collide(in Shape a, FWorldTransform xfA, in Shape b, FWorldTransform xfB,
		in BoxSatCache cache, out Manifold m, out Rejection rejection) {
		var xf = FWorldTransform.InvMul(xfA, xfB);
		var inverseA = FQuaternion.Inverse(a.HullShape.Rotation);
		var center = inverseA * (FTransform.TransformPoint(xf, b.HullShape.Center) - a.HullShape.Center);
		var rotation = inverseA * (xf.Rotation * b.HullShape.Rotation);
		var result = Evaluate(a.HullShape.HalfExtents, center, rotation, b.HullShape.HalfExtents, cache, out m, out rejection);
		m.Normal = a.HullShape.Rotation * m.Normal;
		for (var i = 0; i < m.PointCount; i++) {
			var point = m.GetPoint(i);
			point.Point = a.HullShape.Center + a.HullShape.Rotation * point.Point;
			m.SetPoint(i, point);
		}
		return result;
	}

	internal static BoxSatResult Evaluate(FVector3 a, FVector3 p, FQuaternion q, FVector3 b, in BoxSatCache cache,
		out Manifold m, out Rejection rejection) {
		m = default;
		rejection = Rejection.Feature;
		FP separation;
		if (cache.Axis is BoxSatAxis.FaceA or BoxSatAxis.FaceB) {
			var originalP = p;
			var originalQ = q;
			if (cache.Axis == BoxSatAxis.FaceB) {
				(a, b) = (b, a);
				q = FQuaternion.Inverse(q);
				p = -(q * p);
			}
			var index = cache.Axis == BoxSatAxis.FaceA ? cache.IndexA : cache.IndexB;
			if ((uint)index >= 6)
				return BoxSatResult.FullSearch;
			var n = Hull.Faces[index].Normal;
			var support = p + q * Hull.SupportLocal(b, FQuaternion.Inverse(q) * -n);
			separation = FVector3.Dot(n, support) - FVector3.Dot(FVector3.AbsComponents(n), a);
			if (separation >= B3Config.SpeculativeDistance)
				return BoxSatResult.SeparationHit;
			if (!s_face(a, p, q, b, index, ref m, out separation)) { rejection = Rejection.Clip; return BoxSatResult.FullSearch; }
			if (FP.Abs(separation - cache.Separation) >= B3Config.LinearSlop) { rejection = Rejection.Drift; return BoxSatResult.FullSearch; }
			if (cache.Axis == BoxSatAxis.FaceB) {
				m.Normal = -(originalQ * m.Normal);
				for (var i = 0; i < m.PointCount; i++) {
					var point = m.GetPoint(i);
					point.Point = originalP + originalQ * point.Point;
					var id = point.FeatureId;
					point.FeatureId = ((1u - ((id >> 8) & 255)) << 24) | ((id & 255) << 16)
						| ((1u - (id >> 24)) << 8) | ((id >> 16) & 255);
					m.SetPoint(i, point);
				}
			}
			rejection = Rejection.None;
			return BoxSatResult.FaceHit;
		}
		if (cache.Axis != BoxSatAxis.EdgePair || (uint)cache.IndexA >= 12 || (uint)cache.IndexB >= 12)
			return BoxSatResult.FullSearch;
		var ea = Hull.Edges[cache.IndexA];
		var eb = Hull.Edges[cache.IndexB];
		var pa = Hull.LocalCorner(a, ea.V0);
		var qa = Hull.LocalCorner(a, ea.V1);
		var pb = p + q * Hull.LocalCorner(b, eb.V0);
		var qb = p + q * Hull.LocalCorner(b, eb.V1);
		var da = FVector3.NormalizeSafe(qa - pa);
		var db = FVector3.NormalizeSafe(qb - pb);
		var cba = FVector3.Dot(q * Hull.Faces[eb.FaceA].Normal, da);
		var dba = FVector3.Dot(q * Hull.Faces[eb.FaceB].Normal, da);
		var adc = -FVector3.Dot(Hull.Faces[ea.FaceA].Normal, db);
		var bdc = -FVector3.Dot(Hull.Faces[ea.FaceB].Normal, db);
		var epsilon = FP.FromRatio(1, 100);
		if (!(cba * dba < -epsilon && adc * bdc < -epsilon && cba * bdc > epsilon))
			return BoxSatResult.FullSearch;
		var axis = FVector3.Cross(da, db);
		var length = FVector3.LengthSqr(axis);
		if (length < FP.FromRatio(25, 1000000) || length < FP.CalculationsEpsilonSqr)
			return BoxSatResult.FullSearch;
		axis /= FP.Sqrt(length);
		if (FVector3.Dot(axis, p) < FP.Zero)
			axis = -axis;
		var supportB = p + q * Hull.SupportLocal(b, FQuaternion.Inverse(q) * -axis);
		separation = FVector3.Dot(axis, supportB) - FVector3.Dot(FVector3.AbsComponents(axis), a);
		if (separation > B3Config.SpeculativeDistance)
			return BoxSatResult.SeparationHit;
		if (!s_edge(pa, qa - pa, pb, qb - pb, cache.IndexA, cache.IndexB, ref m)) { rejection = Rejection.Segments; return BoxSatResult.FullSearch; }
		if (FP.Abs(m.Point0.Separation - cache.Separation) >= B3Config.LinearSlop) { rejection = Rejection.Drift; return BoxSatResult.FullSearch; }
		rejection = Rejection.None;
		return BoxSatResult.EdgeHit;
	}
}
