using Fixed64;
using NUnit.Framework;

namespace Space.GameCore.Tests;

[TestFixture]
public sealed class NavBinaryHeapTests {
	[Test]
	public void PopsInScoreOrder() {
		var heap = new NavBinaryHeap(8);
		int[] scores = [5, 1, 7, 3, 2, 6, 0, 4];
		for (var i = 0; i < scores.Length; i++) {
			heap.Push(i, scores[i].ToFP());
		}

		var popped = new List<int>();
		while (heap.Count > 0) {
			popped.Add(heap.Pop());
		}

		Assert.That(popped, Is.EqualTo(new[] { 6, 1, 4, 3, 7, 0, 5, 2 }));
	}

	[Test]
	public void EqualScoresPopByAscendingTriangleIndex() {
		var heap = new NavBinaryHeap(8);
		foreach (var triangle in new[] { 7, 3, 5, 0, 6 }) {
			heap.Push(triangle, FP.One);
		}

		var popped = new List<int>();
		while (heap.Count > 0) {
			popped.Add(heap.Pop());
		}

		Assert.That(popped, Is.EqualTo(new[] { 0, 3, 5, 6, 7 }));
	}

	[Test]
	public void DecreaseKeyReordersAndClearAllowsReuse() {
		var heap = new NavBinaryHeap(4);
		heap.Push(0, 3.ToFP());
		heap.Push(1, 2.ToFP());
		heap.Push(2, 5.ToFP());
		heap.UpdateKey(2, FP.One);

		Assert.That(heap.Pop(), Is.EqualTo(2));
		Assert.That(heap.Contains(2), Is.False);
		Assert.That(heap.Contains(0), Is.True);

		heap.Clear();
		Assert.That(heap.Count, Is.Zero);
		Assert.That(heap.Contains(0), Is.False);

		heap.Push(3, FP.Zero);
		Assert.That(heap.Pop(), Is.EqualTo(3));
	}

	[Test]
	public void InvalidOperationsFailAtTheHeapBoundary() {
		var heap = new NavBinaryHeap(1);

		Assert.That(() => heap.Pop(), Throws.InvalidOperationException.With.Message.Contains("empty"));
		Assert.That(() => heap.UpdateKey(0, FP.Zero), Throws.InvalidOperationException.With.Message.Contains("not in"));
		heap.Push(0, FP.One);
		Assert.Multiple(() => {
			Assert.That(() => heap.Push(0, FP.One), Throws.InvalidOperationException.With.Message.Contains("already"));
			Assert.That(() => heap.Push(1, FP.One), Throws.InstanceOf<ArgumentOutOfRangeException>());
		});
	}

	[Test]
	public void KeyIncreaseReordersDown() {
		var heap = new NavBinaryHeap(3);
		heap.Push(0, FP.One);
		heap.Push(1, FP.Two);
		heap.Push(2, 3.ToFP());

		heap.UpdateKey(0, 4.ToFP());

		Assert.That(new[] { heap.Pop(), heap.Pop(), heap.Pop() }, Is.EqualTo(new[] { 1, 2, 0 }));
	}
}
