using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

public static class GameInterpolationSetup {
	public static void CreateAndInitialize() {
		WP.Create(GameWorldSetup.WorldConfig);
		WP.Types().RegisterAll(typeof(CoreRoot).Assembly);
		WP.Initialize();

		S.SetInterpolationReceiver(new GameInterpolationReceiver());
	}

	public static void Destroy() {
		WP.Destroy();
	}
}
