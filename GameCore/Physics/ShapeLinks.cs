using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

/// <summary>
/// Link from a shape entity to the body entity that owns it. Top-level (not nested in
/// <c>Core&lt;TWorld&gt;</c>) — see <see cref="Shape"/>'s remarks: nested link types silently fail
/// to auto-register.
/// </summary>
public struct BodyOwner : ILinkType {
	public void OnAdd<TW>(World<TW>.Entity self, EntityGID link) where TW : struct, IWorldType {
		link.TryAddLinkItem<TW, Shapes>(self);
	}

	public void OnDelete<TW>(World<TW>.Entity self, EntityGID link, HookReason reason) where TW : struct, IWorldType {
		link.TryDeleteLinkItem<TW, Shapes>(self);
	}
}

/// <summary>Link from a body entity to all shape entities it owns.</summary>
public struct Shapes : ILinksType { }
