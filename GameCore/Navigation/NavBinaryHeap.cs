// Derived from xpTURN Klotho 0.14.1 (FPNavMeshBinaryHeap.cs), Apache-2.0.
// Modified for Space: Fixed64 math, class instead of mutable struct, explicit index tie-breaking.
using Fixed64;

namespace Space.GameCore;

/// <summary>
/// Pre-allocated binary min-heap of triangle indices keyed by f-score. Equal scores pop in
/// ascending triangle-index order, so the result never depends on insertion history.
/// </summary>
internal sealed class NavBinaryHeap {
	private readonly int[] _heap;
	private readonly FP[] _scores;
	private readonly int[] _positions;
	private int _count;

	public NavBinaryHeap(int capacity) {
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
		_heap = new int[capacity];
		_scores = new FP[capacity];
		_positions = new int[capacity];
		Array.Fill(_positions, -1);
	}

	public int Count => _count;

	public void Clear() {
		for (var i = 0; i < _count; i++) {
			_positions[_heap[i]] = -1;
		}
		_count = 0;
	}

	public void Push(int triangle, FP score) {
		if ((uint)triangle >= (uint)_positions.Length) {
			throw new ArgumentOutOfRangeException(nameof(triangle));
		}
		if (_positions[triangle] >= 0) {
			throw new InvalidOperationException($"Triangle {triangle} is already in the heap.");
		}
		if (_count == _heap.Length) {
			throw new InvalidOperationException("The heap is full.");
		}
		var index = _count++;
		_heap[index] = triangle;
		_scores[index] = score;
		_positions[triangle] = index;
		BubbleUp(index);
	}

	public int Pop() {
		if (_count == 0) {
			throw new InvalidOperationException("The heap is empty.");
		}
		var top = _heap[0];
		_positions[top] = -1;
		_count--;
		if (_count > 0) {
			_heap[0] = _heap[_count];
			_scores[0] = _scores[_count];
			_positions[_heap[0]] = 0;
			BubbleDown(0);
		}
		return top;
	}

	public bool Contains(int triangle) {
		ArgumentOutOfRangeException.ThrowIfNegative(triangle);
		if (triangle >= _positions.Length) {
			throw new ArgumentOutOfRangeException(nameof(triangle));
		}
		return _positions[triangle] >= 0;
	}

	public void UpdateKey(int triangle, FP score) {
		if ((uint)triangle >= (uint)_positions.Length) {
			throw new ArgumentOutOfRangeException(nameof(triangle));
		}
		var index = _positions[triangle];
		if (index < 0) {
			throw new InvalidOperationException($"Triangle {triangle} is not in the heap.");
		}
		var previous = _scores[index];
		_scores[index] = score;
		if (score < previous) {
			BubbleUp(index);
		} else if (score > previous) {
			BubbleDown(index);
		}
	}

	private bool Less(int a, int b) {
		return _scores[a] < _scores[b] || (_scores[a] == _scores[b] && _heap[a] < _heap[b]);
	}

	private void BubbleUp(int index) {
		while (index > 0) {
			var parent = (index - 1) / 2;
			if (!Less(index, parent)) {
				break;
			}
			Swap(index, parent);
			index = parent;
		}
	}

	private void BubbleDown(int index) {
		while (true) {
			var left = index * 2 + 1;
			var right = left + 1;
			var smallest = index;
			if (left < _count && Less(left, smallest)) {
				smallest = left;
			}
			if (right < _count && Less(right, smallest)) {
				smallest = right;
			}
			if (smallest == index) {
				return;
			}
			Swap(index, smallest);
			index = smallest;
		}
	}

	private void Swap(int a, int b) {
		(_heap[a], _heap[b]) = (_heap[b], _heap[a]);
		(_scores[a], _scores[b]) = (_scores[b], _scores[a]);
		_positions[_heap[a]] = a;
		_positions[_heap[b]] = b;
	}
}
