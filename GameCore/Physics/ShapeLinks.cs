using System;
using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

/// <summary>
/// Link from a shape entity to the body entity that owns it. Top-level (not nested in
/// <c>Core&lt;TWorld&gt;</c>) — see <see cref="Shape"/>'s remarks: nested link types silently fail
/// to auto-register.
/// </summary>
/// <remarks>
/// Explicit <see cref="ILinkConfig{T}"/> Guid: the default (<c>Utils.GuidFromAQN</c>) hashes
/// <c>typeof(World&lt;TWorld&gt;.Link&lt;BodyOwner&gt;).FullName</c>, which embeds the closed
/// <c>TWorld</c> argument (e.g. "ServerWorld" vs "ClientWorld") — so the same relation gets a
/// *different* Guid on each side of a client/server boundary, and W.Serializer.LoadWorldSnapshot
/// can never match the pools up, silently dropping the relation data on a fresh world (e.g. a
/// client applying a full sync). A fixed, TWorld-independent Guid fixes this.
/// </remarks>
public struct BodyOwner : ILinkType, ILinkConfig<BodyOwner> {
	public void OnAdd<TW>(World<TW>.Entity self, EntityGID link) where TW : struct, IWorldType {
		link.TryAddLinkItem<TW, Shapes>(self);
	}

	public void OnDelete<TW>(World<TW>.Entity self, EntityGID link, HookReason reason) where TW : struct, IWorldType {
		link.TryDeleteLinkItem<TW, Shapes>(self);
	}

	public ComponentTypeConfig<World<TWorld>.Link<BodyOwner>> Config<TWorld>() where TWorld : struct, IWorldType =>
		new(guid: new Guid("8f1a1e2a-2b3c-4d5e-9f10-000000000003"));
}

/// <summary>Link from a body entity to all shape entities it owns.</summary>
/// <remarks>See <see cref="BodyOwner"/>'s remarks on the explicit Guid.</remarks>
public struct Shapes : ILinksType, ILinksConfig<Shapes> {
	public ComponentTypeConfig<World<TWorld>.Links<Shapes>> Config<TWorld>() where TWorld : struct, IWorldType =>
		new(guid: new Guid("8f1a1e2a-2b3c-4d5e-9f10-000000000004"));
}
