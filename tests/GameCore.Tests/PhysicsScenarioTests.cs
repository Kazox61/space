using NUnit.Framework;
using PhysicsSmokeTest;

namespace Space.GameCore.Tests;

[TestFixture]
[NonParallelizable]
public sealed class PhysicsScenarioTests {
	public static IEnumerable<TestCaseData> Cases() {
		foreach (var scenario in Program.Scenarios) {
			yield return new TestCaseData(scenario)
				.SetName(scenario.Name)
				.SetCategory(scenario.Category);
		}
	}

	[TestCaseSource(nameof(Cases))]
	public void ScenarioPasses(PhysicsScenario scenario) {
		Program.RunScenario(scenario);
	}
}
