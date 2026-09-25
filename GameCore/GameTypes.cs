using System.Reflection;
using System.Runtime.CompilerServices;
using FFS.Libraries.StaticEcs;

namespace Space.GameCore;

/// <summary>
/// Explicit registration of every ECS type in this assembly for a given world.
/// <para>
/// <c>RegisterAll</c> discovers types by reflection and instantiates
/// <c>World&lt;TWorld&gt;.Components&lt;T&gt;</c> through <c>MakeGenericType</c>. Under NativeAOT (the iOS
/// export) that only resolves instantiations the compiler already generated from static code. A world
/// that is only ever filled through the serializer (the client's <c>GameWorldPrev</c>) has none, so
/// registration throws <c>NotSupportedException: ... is missing native code or metadata</c>. Listing the
/// types here makes each <c>World&lt;TWorld&gt;.Components&lt;T&gt;</c> a static reference for whichever
/// <typeparamref name="TWorld"/> this is called with, so the same list serves every world.
/// </para>
/// <para>
/// Must be called between <c>World&lt;TWorld&gt;.Create()</c> and <c>Initialize()</c>. Debug desktop builds
/// verify the list against a reflection scan of the assembly, so a forgotten type fails fast in the editor
/// instead of on device.
/// </para>
/// </summary>
public static class GameTypes {
	public static void Register<TWorld>() where TWorld : struct, IWorldType {
		World<TWorld>.Types()
			.Component<Body>()
			.Component<Contact>()
			.Component<Health>()
			.Component<Lifetime>()
			.Component<LootDrop>()
			.Component<Mover>()
			.Component<PatrolRail>()
			.Component<PendingRespawn>()
			.Component<PendingShot>()
			.Component<PlayerInfo>()
			.Component<ProjectileOrigin>()
			.Component<RailSlot>()
			.Component<Shape>()
			.Component<Transform>()
			.Component<ViewId>()
			.Tag<IsProjectile>()
			.Tag<OutOfPhysicsBounds>()
			.Links<Shapes>()
			.Link<BodyOwner>()
			.Link<ShapeA>()
			.Link<ShapeB>()
			.Link<Shooter>()
			.Event<ContactBeginTouchEvent>()
			.Event<ContactEndTouchEvent>()
			.Event<ContactHitEvent>()
			.Event<ContinuousHitEvent>()
			.Event<SensorBeginTouchEvent>()
			.Event<SensorEndTouchEvent>()
			.Event<DamageEvent>()
			.Event<DeadEvent>()
			.EntityType<Crate>()
			.EntityType<Dummy>()
			.EntityType<Player>()
			.EntityType<Projectile>();

#if DEBUG
		VerifyCoversAssembly();
#endif
	}

	// Mirrors the list above; only consulted by the debug-time coverage check.
	private static readonly HashSet<Type> s_registered = [
		typeof(Body), typeof(Contact), typeof(Health), typeof(Lifetime), typeof(LootDrop), typeof(Mover), typeof(PatrolRail), typeof(PendingRespawn), typeof(PendingShot),
		typeof(PlayerInfo), typeof(ProjectileOrigin), typeof(RailSlot), typeof(Shape), typeof(Transform), typeof(ViewId),
		typeof(IsProjectile), typeof(OutOfPhysicsBounds),
		typeof(Shapes), typeof(BodyOwner), typeof(ShapeA), typeof(ShapeB), typeof(Shooter),
		typeof(ContactBeginTouchEvent), typeof(ContactEndTouchEvent), typeof(ContactHitEvent), typeof(ContinuousHitEvent),
		typeof(SensorBeginTouchEvent), typeof(SensorEndTouchEvent), typeof(DamageEvent), typeof(DeadEvent),
		typeof(Crate), typeof(Dummy), typeof(Player), typeof(Projectile),
	];

	/// <summary>
	/// Reflection scan with the same type filter as <c>AutoRegistration.RegisterAll</c>; throws naming any
	/// ECS type in this assembly that <see cref="Register{TWorld}"/> does not list. Skipped under AOT.
	/// </summary>
	private static void VerifyCoversAssembly() {
		if (!RuntimeFeature.IsDynamicCodeSupported) {
			return;
		}

		var missing = new List<string>();
		foreach (var type in typeof(GameTypes).Assembly.GetTypes()) {
			if (!type.IsValueType || type.IsAbstract || type.IsGenericTypeDefinition) {
				continue;
			}

			var isEcsType = typeof(IComponent).IsAssignableFrom(type)
							|| typeof(ITag).IsAssignableFrom(type)
							|| typeof(IMultiComponent).IsAssignableFrom(type)
							|| typeof(ILinkType).IsAssignableFrom(type)
							|| typeof(IEvent).IsAssignableFrom(type)
							|| typeof(IEntityType).IsAssignableFrom(type);
			if (isEcsType && !s_registered.Contains(type)) {
				missing.Add(type.FullName ?? type.Name);
			}
		}

		if (missing.Count > 0) {
			var names = string.Join(", ", missing);
			throw new InvalidOperationException($"GameTypes.Register is missing ECS types declared in GameCore: {names}. Add them to GameTypes.Register and GameTypes.s_registered.");
		}
	}
}
