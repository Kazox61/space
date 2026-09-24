namespace Space.Client;

/// <summary>
/// Where the rendered frame falls between the two most recent simulated ticks.
/// <para>
/// Each client update ends by simulating one final tick, and <see cref="GameInterpolationReceiver"/>
/// copies the world into <see cref="WP"/> right before it. So <see cref="WP"/> holds the previous tick
/// and the game world the current one. Views blend from the first to the second by <see cref="Alpha"/>
/// (0 = previous tick, 1 = current), which draws the world one tick (~16 ms) behind the simulation
/// in exchange for smooth motion at any display rate.
/// </para>
/// </summary>
public static class RenderInterpolation {
	/// <summary>Set by <c>ClientGame</c> after every simulation update, read by the views later that frame.</summary>
	public static float Alpha { get; set; } = 1f;
}
