using System;
using System.Collections.Generic;
using System.IO;
using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticPack;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

/// <summary>Structural statistics for one broad-phase dynamic tree (see <c>BroadPhase.GetTreeStats</c>).</summary>
public struct BroadPhaseTreeStats {
	public BodyType Type;
	public int Proxies;
	public int Nodes;
	public int Height;
	public double AreaRatio;

	/// <summary>Height of a perfectly balanced tree over <see cref="Proxies"/> leaves.</summary>
	public readonly int BalancedHeight => Proxies <= 1 ? 0 : (int)Math.Ceiling(Math.Log2(Proxies));

	/// <summary>
	/// True when the tree is more than twice as deep as a balanced one (plus slack for tiny trees).
	/// The rotating insertion keeps real trees well under this; exceeding it means queries degrade.
	/// </summary>
	public readonly bool IsDegraded => Height > 2 * BalancedHeight + 4;

	public override readonly string ToString() =>
		$"{Type}: proxies={Proxies} nodes={Nodes} height={Height} (balanced {BalancedHeight}) areaRatio={AreaRatio:F2}{(IsDegraded ? " DEGRADED" : "")}";
}

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Spatial index for shape proxies, backed by one <see cref="DynamicTree"/> per <see cref="BodyType"/>
	/// (mirrors box3d's b3BroadPhase, which keeps a separate tree per body type). Not an ECS structure —
	/// a plain resource wrapping proxy lifecycle and pair generation.
	/// </summary>
	public class BroadPhase : IResource {
		private const int TypeCount = 3;
		private const int TypeBits = 2;
		private const int TypeMask = (1 << TypeBits) - 1;

		private readonly DynamicTree[] _trees = { new(), new(), new() };
		// Not readonly: Read() below repopulates these via StaticPack's `ref` overloads.
		private List<int> _movedProxies = new();
		private HashSet<(ulong, ulong)> _pairSet = new();

		public int CreateProxy(BodyType type, FAABB aabb, ulong categoryBits, EntityGID shapeGid, bool forcePairCreation) {
			var nodeIndex = _trees[(int)type].CreateProxy(aabb, categoryBits, shapeGid.Raw);
			var proxyKey = PackProxyKey(nodeIndex, type);

			if (forcePairCreation) {
				BufferMove(proxyKey);
			}

			return proxyKey;
		}

		public void DestroyProxy(int proxyKey) {
			UnpackProxyKey(proxyKey, out var nodeIndex, out var type);
			_trees[(int)type].DestroyProxy(nodeIndex);
			for (var i = _movedProxies.Count - 1; i >= 0; i--) {
				if (_movedProxies[i] == proxyKey) {
					_movedProxies.RemoveAt(i);
				}
			}
		}

		public void MoveProxy(int proxyKey, FAABB aabb) {
			UnpackProxyKey(proxyKey, out var nodeIndex, out var type);
			_trees[(int)type].MoveProxy(nodeIndex, aabb);
			BufferMove(proxyKey);
		}

		private void BufferMove(int proxyKey) {
			if (!_movedProxies.Contains(proxyKey)) {
				_movedProxies.Add(proxyKey);
			}
		}

		/// <summary>
		/// For every proxy that moved (or was created with <c>forcePairCreation</c>) since the last
		/// call, queries the other trees for newly-overlapping proxies and invokes
		/// <paramref name="tryAcceptPair"/> once per genuinely new pair. Mirrors box3d's
		/// b3UpdateBroadPhasePairs. Only does coarse AABB + per-node category-bit filtering — full
		/// <see cref="Filter.ShouldCollide"/> (which also needs mask bits and group index from both
		/// shapes) is the caller's job once it has entity access to both shapes.
		/// </summary>
		public void UpdatePairs(Func<EntityGID, EntityGID, bool> tryAcceptPair) {
			_pairCallback = tryAcceptPair;
			try {
				foreach (var proxyKey in _movedProxies) {
					UnpackProxyKey(proxyKey, out var nodeIndex, out var type);
					var moverNode = _trees[(int)type].Nodes[nodeIndex];
					_pairMoverGid = new EntityGID(moverNode.UserData);
					_pairMoverNode = nodeIndex;
					_pairMoverType = (int)type;

					for (var otherType = 0; otherType < TypeCount; otherType++) {
						_pairQueryType = otherType;
						_trees[otherType].Query(moverNode.AABB, ulong.MaxValue, PairQueryCallback, this);
					}
				}
			} finally {
				_pairCallback = null;
			}

			_movedProxies.Clear();
		}

		// Pair-query state: the tree callback is a cached static delegate that reads the current mover
		// from these fields (via the context argument), so UpdatePairs allocates no closures per proxy.
		private static readonly DynamicTree.TreeQueryCallback PairQueryCallback = static (otherNodeIndex, otherUserData, context) =>
			((BroadPhase)context).AcceptPairCandidate(otherNodeIndex, otherUserData);
		private Func<EntityGID, EntityGID, bool>? _pairCallback;
		private EntityGID _pairMoverGid;
		private int _pairMoverNode;
		private int _pairMoverType;
		private int _pairQueryType;

		private bool AcceptPairCandidate(int otherNodeIndex, ulong otherUserData) {
			if (_pairQueryType == _pairMoverType && otherNodeIndex == _pairMoverNode) {
				return true;
			}

			var otherGid = new EntityGID(otherUserData);
			var pairKey = PairKey(_pairMoverGid, otherGid);
			if (_pairSet.Add(pairKey) && !_pairCallback!(_pairMoverGid, otherGid)) {
				_pairSet.Remove(pairKey);
			}

			return true;
		}

		/// <summary>
		/// Appends the GID of every shape whose proxy overlaps <paramref name="aabb"/>, across all
		/// body-type trees, into <paramref name="results"/> (caller clears it first if a fresh set is
		/// wanted). Unlike <see cref="UpdatePairs"/> this is a plain spatial query with no
		/// pair-dedup/move-tracking bookkeeping -- for one-off queries like <c>CharacterMover</c>'s
		/// cast/collide, not per-tick pair maintenance. Takes a caller-owned, reusable
		/// <see cref="List{EntityGID}"/> rather than a delegate specifically so hot callers (a mover
		/// queries the broad phase up to 10x/tick) can pass the same list back tick after tick: the
		/// callback below is `static` and <paramref name="results"/> flows through
		/// <see cref="DynamicTree.TreeQueryCallback"/>'s existing `object context` parameter as a plain
		/// reference (no boxing, since it's already a reference type), so this allocates nothing per call.
		/// </summary>
		public void Query(FAABB aabb, List<EntityGID> results) {
			Query(aabb, ulong.MaxValue, results);
		}

		/// <summary>Mask-pruned variant of <see cref="Query(FAABB,List{EntityGID})"/>.</summary>
		public void Query(FAABB aabb, ulong maskBits, List<EntityGID> results) {
			for (var type = 0; type < TypeCount; type++) {
				_trees[type].Query(aabb, maskBits, static (_, userData, context) => {
					((List<EntityGID>)context).Add(new EntityGID(userData));
					return true;
				}, results);
			}
		}

		/// <summary>Appends every live proxy. Used by relative-motion CCD before target swept trees exist.</summary>
		internal void CollectProxies(List<EntityGID> results) {
			for (var treeIndex = 0; treeIndex < TypeCount; treeIndex++) {
				var tree = _trees[treeIndex];
				for (var nodeIndex = 0; nodeIndex < tree.NodesCapacity; nodeIndex++) {
					ref readonly var node = ref tree.Nodes[nodeIndex];
					if ((node.Flags & (DynamicTree.AllocatedNode | DynamicTree.LeafNode)) == (DynamicTree.AllocatedNode | DynamicTree.LeafNode)) {
						results.Add(new EntityGID(node.UserData));
					}
				}
			}
		}

		/// <summary>
		/// Per-candidate ray-cast callback, invoked once per broad-phase leaf whose fat AABB the ray
		/// still might cross. <paramref name="currentMaxFraction"/> is the closest fraction found so
		/// far (shared across all three trees). Must return box3d's b3CastResultFcn 5-way contract:
		/// 0 to stop the entire cast immediately; a value in (0, currentMaxFraction] to clip further
		/// traversal to that fraction; a negative value to ignore this candidate; anything &gt;=
		/// <paramref name="currentMaxFraction"/> to keep going without clipping (report-all-hits style).
		/// The precise shape-vs-ray test (and therefore any Shape/Body/entity resolution) is entirely
		/// the caller's job -- this class stays entity-resolution-free, matching <see cref="Query"/>.
		/// </summary>
		public delegate FP RayCastCallback(EntityGID shapeGid, FVector3 origin, FVector3 translation, FP currentMaxFraction);

		/// <summary>
		/// Casts a ray across all three body-type trees (static/kinematic/dynamic), threading one
		/// shrinking <paramref name="maxFraction"/> across all of them so a closer hit in an earlier
		/// tree prunes the later ones -- mirrors box3d's b3World_CastRay, which loops its own
		/// static/kinematic/dynamic trees in a single query for the same reason.
		/// </summary>
		public void CastRay(FVector3 origin, FVector3 translation, FP maxFraction, RayCastCallback callback) {
			CastRay(origin, translation, maxFraction, ulong.MaxValue, callback);
		}

		/// <summary>Mask-pruned variant of <see cref="CastRay(FVector3,FVector3,FP,RayCastCallback)"/>.</summary>
		public void CastRay(FVector3 origin, FVector3 translation, FP maxFraction, ulong maskBits, RayCastCallback callback) {
			var stopped = false;

			for (var type = 0; type < TypeCount && !stopped; type++) {
				var input = new DynamicTree.RayCastInput { Origin = origin, Direction = translation, MaxFraction = maxFraction };

				_trees[type].RayCast(input, maskBits, (ref DynamicTree.RayCastInput subInput, int _, ulong userData, object _) => {
					var value = callback(new EntityGID(userData), origin, translation, subInput.MaxFraction);

					if (value == FP.Zero) {
						stopped = true;
					} else if (value > FP.Zero && value <= maxFraction) {
						maxFraction = value;
					}

					return value;
				});
			}
		}

		/// <summary>Removes a pair from the dedup set so it can be re-created later (call when a contact is destroyed).</summary>
		public void ForgetPair(EntityGID a, EntityGID b) {
			var pairKey = PairKey(a, b);
			_pairSet.Remove(pairKey);
		}

		/// <summary>Removes every cached pair involving a shape, including rejected or historically stale pairs.</summary>
		public void ForgetPairsForShape(EntityGID shape) {
			_pairSet.RemoveWhere(pair => pair.Item1 == shape.Raw || pair.Item2 == shape.Raw);
		}

		/// <summary>Throws when a proxy, cached pair, or contact no longer resolves to consistent live ECS state.</summary>
		public void Validate() {
			var proxyCount = 0;
			for (var treeIndex = 0; treeIndex < TypeCount; treeIndex++) {
				var tree = _trees[treeIndex];
				for (var nodeIndex = 0; nodeIndex < tree.NodesCapacity; nodeIndex++) {
					ref readonly var node = ref tree.Nodes[nodeIndex];
					if ((node.Flags & (DynamicTree.AllocatedNode | DynamicTree.LeafNode)) != (DynamicTree.AllocatedNode | DynamicTree.LeafNode)) {
						continue;
					}

					proxyCount++;
					var gid = new EntityGID(node.UserData);
					if (!gid.TryUnpack<TWorld>(out var shapeEntity) || !shapeEntity.Has<Shape>()) {
						throw new InvalidOperationException($"Broad-phase proxy {treeIndex}:{nodeIndex} does not resolve to a live shape.");
					}

					ref readonly var shape = ref shapeEntity.Read<Shape>();
					if (shape.ProxyKey != PackProxyKey(nodeIndex, (BodyType)treeIndex)) {
						throw new InvalidOperationException($"Shape {gid.Raw} does not point back to broad-phase proxy {treeIndex}:{nodeIndex}.");
					}

					if (node.AABB.LowerBound != shape.FatAabb.LowerBound || node.AABB.UpperBound != shape.FatAabb.UpperBound) {
						throw new InvalidOperationException($"Shape {gid.Raw} has a stale broad-phase proxy AABB.");
					}

					if (!shapeEntity.Has<W.Link<BodyOwner>>()) {
						throw new InvalidOperationException($"Shape {gid.Raw} has a proxy but no body owner.");
					}

					ref readonly var owner = ref shapeEntity.Read<W.Link<BodyOwner>>();
					if (!owner.Value.TryUnpack<TWorld>(out var bodyEntity) || !bodyEntity.Has<Body>()) {
						throw new InvalidOperationException($"Shape {gid.Raw} has a proxy but no live body.");
					}

					if ((int)bodyEntity.Read<Body>().Type != treeIndex) {
						throw new InvalidOperationException($"Shape {gid.Raw} is stored in the wrong body-type tree.");
					}

					if (!BodyOperations.IsEnabled(bodyEntity.Read<Body>())) {
						throw new InvalidOperationException($"Shape {gid.Raw} has a proxy while its body is disabled.");
					}
				}
			}

			if (proxyCount != ProxyCount) {
				throw new InvalidOperationException($"Broad-phase proxy count is {ProxyCount}, but {proxyCount} live leaves were found.");
			}

			var contactPairs = new HashSet<(ulong, ulong)>();
			foreach (var contactEntity in W.Query<All<Contact>>().Entities()) {
				ref readonly var contact = ref contactEntity.Read<Contact>();
				if (!contactEntity.Has<W.Link<ShapeA>>() || contactEntity.Read<W.Link<ShapeA>>().Value != contact.ShapeA
					|| !contactEntity.Has<W.Link<ShapeB>>() || contactEntity.Read<W.Link<ShapeB>>().Value != contact.ShapeB) {
					throw new InvalidOperationException("Contact links do not match its stored broad-phase pair.");
				}

				var pair = PairKey(contact.ShapeA, contact.ShapeB);
				if (!_pairSet.Contains(pair)) {
					throw new InvalidOperationException($"Contact pair ({pair.Item1}, {pair.Item2}) is not cached by the broad phase.");
				}

				if (!contactPairs.Add(pair)) {
					throw new InvalidOperationException($"Contact pair ({pair.Item1}, {pair.Item2}) has duplicate contact entities.");
				}
			}

			foreach (var pair in _pairSet) {
				if (!TryValidatePairEndpoint(pair.Item1) || !TryValidatePairEndpoint(pair.Item2)) {
					throw new InvalidOperationException($"Cached pair ({pair.Item1}, {pair.Item2}) has a stale shape or proxy.");
				}

				if (!contactPairs.Contains(pair)) {
					throw new InvalidOperationException($"Cached pair ({pair.Item1}, {pair.Item2}) has no contact entity.");
				}
			}
		}

		/// <summary>
		/// Structural health of one body-type tree. A height far above log2(proxies) or a growing area
		/// ratio (summed internal-node perimeters over the root's) indicates a degraded tree whose
		/// queries visit many more nodes than necessary.
		/// </summary>
		public BroadPhaseTreeStats GetTreeStats(BodyType type) {
			var tree = _trees[(int)type];
			var stats = new BroadPhaseTreeStats { Type = type, Proxies = tree.ProxyCount, Nodes = tree.NodesCount };
			if (tree.Root == DynamicTree.NullIndex) {
				return stats;
			}

			stats.Height = tree.Nodes[tree.Root].Height;
			var rootPerimeter = Perimeter(tree.Nodes[tree.Root].AABB);
			var internalPerimeter = 0.0;
			for (var nodeIndex = 0; nodeIndex < tree.NodesCapacity; nodeIndex++) {
				ref readonly var node = ref tree.Nodes[nodeIndex];
				if ((node.Flags & DynamicTree.AllocatedNode) != 0 && (node.Flags & DynamicTree.LeafNode) == 0) {
					internalPerimeter += Perimeter(node.AABB);
				}
			}
			stats.AreaRatio = rootPerimeter > 0.0 ? internalPerimeter / rootPerimeter : 0.0;
			return stats;
		}

		private static double Perimeter(FAABB aabb) {
			var extent = aabb.UpperBound - aabb.LowerBound;
			return 2.0 * ((double)extent.X.RawValue + extent.Y.RawValue + extent.Z.RawValue) / 65536.0;
		}

		/// <summary>Total proxies across all three body-type trees (Phase 0 diagnostics counter).</summary>
		public int ProxyCount {
			get {
				var total = 0;
				foreach (var tree in _trees) {
					total += tree.ProxyCount;
				}
				return total;
			}
		}

		/// <summary>Broad-phase pairs currently reserved in the dedup set (Phase 0 diagnostics counter).</summary>
		public int CachedPairCount => _pairSet.Count;

		/// <summary>
		/// Proxies created or moved since the last <see cref="UpdatePairs"/> call (Phase 0 diagnostics
		/// counter; expected to settle at zero plus any proxy moved later in the same update).
		/// </summary>
		public int MovedProxyCount => _movedProxies.Count;

		private static int PackProxyKey(int nodeIndex, BodyType type) => (nodeIndex << TypeBits) | (int)type;

		private static (ulong, ulong) PairKey(EntityGID a, EntityGID b) => a.Raw < b.Raw ? (a.Raw, b.Raw) : (b.Raw, a.Raw);

		private bool TryValidatePairEndpoint(ulong raw) {
			var gid = new EntityGID(raw);
			if (!gid.TryUnpack<TWorld>(out var entity) || !entity.Has<Shape>()) {
				return false;
			}

			var proxyKey = entity.Read<Shape>().ProxyKey;
			if (proxyKey == Shape.NullProxyKey) {
				return false;
			}

			UnpackProxyKey(proxyKey, out var nodeIndex, out var type);
			if ((int)type >= TypeCount) {
				return false;
			}

			var tree = _trees[(int)type];
			if (nodeIndex < 0 || nodeIndex >= tree.NodesCapacity) {
				return false;
			}

			ref readonly var node = ref tree.Nodes[nodeIndex];
			return (node.Flags & (DynamicTree.AllocatedNode | DynamicTree.LeafNode)) == (DynamicTree.AllocatedNode | DynamicTree.LeafNode)
				&& node.UserData == raw;
		}

		private static void UnpackProxyKey(int proxyKey, out int nodeIndex, out BodyType type) {
			nodeIndex = proxyKey >> TypeBits;
			type = (BodyType)(proxyKey & TypeMask);
		}

		// Rollback (GameWorldRollback) snapshots the whole world every tick, so BroadPhase's three
		// DynamicTrees and pair-dedup set must round-trip too -- otherwise Shape.ProxyKey (restored
		// as ordinary component data) would index into stale/corrupted tree state after a rollback.
		public Guid? Guid() => new("6f2f7f0a-6d0c-4f3e-9c2a-6e6b3d7c8a5b");
		public byte Version() => 1;

		public void Write(ref BinaryPackWriter writer) {
			foreach (var tree in _trees) {
				writer.WriteInt(tree.NodesCapacity);
				writer.WriteInt(tree.NodesCount);
				writer.WriteInt(tree.FreeList);
				writer.WriteInt(tree.Root);
				writer.WriteInt(tree.ProxyCount);
				writer.WriteArrayUnmanaged(tree.Nodes);
			}

			// WriteHashSet/WriteList need a registered packer for the element type -- (ulong,ulong)
			// isn't one. The *Unmanaged array methods do a raw memory copy for any blittable type
			// instead (and track their own length), so round-trip both collections through plain arrays.
			// Canonical order: HashSet enumeration order depends on each set's own insertion/removal
			// history, but a world restored from a snapshot re-inserts pairs in array order -- so an
			// unsorted round-trip can make the same logical pair set serialize to different bytes in a
			// live world vs a restored one, breaking state-hash comparison across rollback replay.
			// Sorting decouples the serialized bytes from that history.
			var pairs = new (ulong, ulong)[_pairSet.Count];
			_pairSet.CopyTo(pairs);
			Array.Sort(pairs);
			writer.WriteArrayUnmanaged(pairs);
			writer.WriteArrayUnmanaged(_movedProxies.ToArray());
		}

		public void Read(ref BinaryPackReader reader, byte version) {
			if (version != Version()) {
				throw new InvalidDataException($"Unsupported BroadPhase snapshot version {version}.");
			}
			foreach (var tree in _trees) {
				var nodesCapacity = reader.ReadInt();
				var nodesCount = reader.ReadInt();
				var freeList = reader.ReadInt();
				var root = reader.ReadInt();
				var proxyCount = reader.ReadInt();
				var nodes = reader.ReadArrayUnmanaged<DynamicTree.TreeNode>();
				tree.RestoreState(nodes, nodesCapacity, nodesCount, freeList, root, proxyCount);
			}

			var pairs = reader.ReadArrayUnmanaged<(ulong, ulong)>();
			_pairSet.Clear();
			foreach (var pair in pairs) {
				_pairSet.Add(pair);
			}

			var moved = reader.ReadArrayUnmanaged<int>();
			_movedProxies.Clear();
			_movedProxies.AddRange(moved);
		}
	}
}
