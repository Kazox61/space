using PhysicsSmokeTest;

var enforce = args.Contains("--enforce", StringComparer.Ordinal);
var passed = PhysicsSmokeTest.Program.RunBenchmarks(enforce);
if (enforce && !passed) {
	Console.Error.WriteLine("PHYSICS OR NAVIGATION BUDGET EXCEEDED");
	return 1;
}

return 0;
