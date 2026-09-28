using PhysicsSmokeTest;

var enforce = args.Contains("--enforce", StringComparer.Ordinal);
var physicsOnly = args.Contains("--physics", StringComparer.Ordinal);
var navigationOnly = args.Contains("--navigation", StringComparer.Ordinal);
if (physicsOnly && navigationOnly) {
	Console.Error.WriteLine("Choose at most one of --physics and --navigation.");
	return 2;
}
var passed = PhysicsSmokeTest.Program.RunBenchmarks(enforce, runPhysics: !navigationOnly, runNavigation: !physicsOnly);
if (enforce && !passed) {
	Console.Error.WriteLine("PHYSICS OR NAVIGATION BUDGET EXCEEDED");
	return 1;
}

return 0;
