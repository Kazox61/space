using System.Numerics;
using Fixed64;

namespace Space.GameCore;

/// <summary>
/// Hides rollback corrections of players. A view draws <c>corrected position + offset</c>; the
/// offset starts at the correction, so the player stays where they were just drawn, and then fades
/// to zero so they glide to where they really are. Presentation only; nothing flows back into the
/// simulation.
/// <para>
/// Jumps are cut, not smoothed: a teleport (<see cref="PositionCorrection.Teleported"/>) or a total
/// offset longer than <see cref="CutDistance"/> resets the offset, so the player snaps to the corrected
/// position instead of sliding across the map.
/// </para>
/// </summary>
public sealed class CorrectionSmoother {
	/// <summary>Offsets longer than this are dropped instead of smoothed.</summary>
	public const float CutDistance = 2f;
	/// <summary>Time for an offset to shrink to half its length.</summary>
	public const float HalfLifeSeconds = 0.08f;
	private const float RestDistance = 0.001f;

	private readonly Dictionary<ushort, Vector3> _offsets = new();
	private readonly List<ushort> _channels = new();

	public int ActiveCount => _offsets.Count;

	public Vector3 Offset(ushort channel) => _offsets.TryGetValue(channel, out var offset) ? offset : Vector3.Zero;

	public void Apply(List<PositionCorrection> corrections) {
		foreach (var correction in corrections) {
			if (correction.Teleported) {
				_offsets.Remove(correction.Channel);
				continue;
			}

			var offset = Offset(correction.Channel) + ToVector(correction.Error);
			if (offset.Length() > CutDistance) {
				_offsets.Remove(correction.Channel);
			} else {
				_offsets[correction.Channel] = offset;
			}
		}
	}

	/// <summary>Fades every offset by one frame of <paramref name="deltaSeconds"/>.</summary>
	public void Advance(float deltaSeconds) {
		var keep = MathF.Pow(0.5f, deltaSeconds / HalfLifeSeconds);
		_channels.AddRange(_offsets.Keys);
		foreach (var channel in _channels) {
			var offset = _offsets[channel] * keep;
			if (offset.Length() < RestDistance) {
				_offsets.Remove(channel);
			} else {
				_offsets[channel] = offset;
			}
		}
		_channels.Clear();
	}

	public void Clear() {
		_offsets.Clear();
	}

	private static Vector3 ToVector(FVector3 value) => new(value.X.ToFloat(), value.Y.ToFloat(), value.Z.ToFloat());
}
