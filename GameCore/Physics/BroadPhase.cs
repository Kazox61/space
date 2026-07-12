using System;
using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticPack;
using Shenanicode.Rollback;
using Fixed32;
using Fixed;

namespace Space.GameCore;

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
				_movedProxies.Add(proxyKey);
			}

			return proxyKey;
		}

		public void DestroyProxy(int proxyKey) {
			UnpackProxyKey(proxyKey, out var nodeIndex, out var type);
			_trees[(int)type].DestroyProxy(nodeIndex);
			_movedProxies.Remove(proxyKey);
		}

		public void MoveProxy(int proxyKey, FAABB aabb) {
			UnpackProxyKey(proxyKey, out var nodeIndex, out var type);
			_trees[(int)type].MoveProxy(nodeIndex, aabb);
			_movedProxies.Add(proxyKey);
		}

		/// <summary>
		/// For every proxy that moved (or was created with <c>forcePairCreation</c>) since the last
		/// call, queries the other trees for newly-overlapping proxies and invokes
		/// <paramref name="onNewPair"/> once per genuinely new pair. Mirrors box3d's
		/// b3UpdateBroadPhasePairs. Only does coarse AABB + per-node category-bit filtering — full
		/// <see cref="Filter.ShouldCollide"/> (which also needs mask bits and group index from both
		/// shapes) is the caller's job once it has entity access to both shapes.
		/// </summary>
		public void UpdatePairs(Action<EntityGID, EntityGID> onNewPair) {
			foreach (var proxyKey in _movedProxies) {
				UnpackProxyKey(proxyKey, out var nodeIndex, out var type);
				var moverNode = _trees[(int)type].Nodes[nodeIndex];
				var moverGid = new EntityGID(moverNode.UserData);
				var fatAabb = moverNode.AABB;

				for (var otherType = 0; otherType < TypeCount; otherType++) {
					var capturedType = type;
					var capturedNodeIndex = nodeIndex;
					var capturedMoverGid = moverGid;

					_trees[otherType].Query(fatAabb, ulong.MaxValue, (otherNodeIndex, otherUserData, _) => {
						if (otherType == (int)capturedType && otherNodeIndex == capturedNodeIndex) {
							return true;
						}

						var otherGid = new EntityGID(otherUserData);
						var pairKey = capturedMoverGid.Raw < otherGid.Raw
							? (capturedMoverGid.Raw, otherGid.Raw)
							: (otherGid.Raw, capturedMoverGid.Raw);

						if (_pairSet.Add(pairKey)) {
							onNewPair(capturedMoverGid, otherGid);
						}

						return true;
					});
				}
			}

			_movedProxies.Clear();
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
			for (var type = 0; type < TypeCount; type++) {
				_trees[type].Query(aabb, ulong.MaxValue, static (_, userData, context) => {
					((List<EntityGID>)context).Add(new EntityGID(userData));
					return true;
				}, results);
			}
		}

		/// <summary>Removes a pair from the dedup set so it can be re-created later (call when a contact is destroyed).</summary>
		public void ForgetPair(EntityGID a, EntityGID b) {
			var pairKey = a.Raw < b.Raw ? (a.Raw, b.Raw) : (b.Raw, a.Raw);
			_pairSet.Remove(pairKey);
		}

		private static int PackProxyKey(int nodeIndex, BodyType type) => (nodeIndex << TypeBits) | (int)type;

		private static void UnpackProxyKey(int proxyKey, out int nodeIndex, out BodyType type) {
			nodeIndex = proxyKey >> TypeBits;
			type = (BodyType)(proxyKey & TypeMask);
		}

		// Rollback (GameWorldRollback) snapshots the whole world every tick, so BroadPhase's three
		// DynamicTrees and pair-dedup set must round-trip too -- otherwise Shape.ProxyKey (restored
		// as ordinary component data) would index into stale/corrupted tree state after a rollback.
		public Guid? Guid() => new("6f2f7f0a-6d0c-4f3e-9c2a-6e6b3d7c8a5b");

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
			var pairs = new (ulong, ulong)[_pairSet.Count];
			_pairSet.CopyTo(pairs);
			writer.WriteArrayUnmanaged(pairs);
			writer.WriteArrayUnmanaged(_movedProxies.ToArray());
		}

		public void Read(ref BinaryPackReader reader, byte version) {
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
