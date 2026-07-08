using System;
using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>
/// Link from a contact entity to the first shape entity in the pair. No hooks — unidirectional.
/// Top-level (not nested in <c>Core&lt;TWorld&gt;</c>) — see <see cref="Shape"/>'s remarks.
/// </summary>
/// <remarks>
/// Explicit <see cref="ILinkConfig{T}"/> Guid: the default (<c>Utils.GuidFromAQN</c>) hashes
/// <c>typeof(World&lt;TWorld&gt;.Link&lt;ShapeA&gt;).FullName</c>, which embeds the closed <c>TWorld</c>
/// argument (e.g. "ServerWorld" vs "ClientWorld") — so the same relation gets a *different* Guid on
/// each side of a client/server boundary, and W.Serializer.LoadWorldSnapshot can never match the
/// pools up, silently dropping the relation data. A fixed, TWorld-independent Guid fixes this.
/// </remarks>
public struct ShapeA : ILinkType, ILinkConfig<ShapeA> {
	public ComponentTypeConfig<World<TWorld>.Link<ShapeA>> Config<TWorld>() where TWorld : struct, IWorldType =>
		new(guid: new Guid("8f1a1e2a-2b3c-4d5e-9f10-000000000001"));
}

/// <summary>Link from a contact entity to the second shape entity in the pair. No hooks — unidirectional.</summary>
/// <remarks>See <see cref="ShapeA"/>'s remarks on the explicit Guid.</remarks>
public struct ShapeB : ILinkType, ILinkConfig<ShapeB> {
	public ComponentTypeConfig<World<TWorld>.Link<ShapeB>> Config<TWorld>() where TWorld : struct, IWorldType =>
		new(guid: new Guid("8f1a1e2a-2b3c-4d5e-9f10-000000000002"));
}
