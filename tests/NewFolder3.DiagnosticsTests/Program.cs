NewFolder3.FsdDiagnosticsTests.Run();
NewFolder3.SaveSlotResetTests.Run();
NewFolder3.FarmingRecoveryTests.Run();
NewFolder3.FsdHoardPickupTests.Run();
Console.WriteLine("Save reset: full/empty/disabled/partial lists, deterministic selection and pinned identity checks passed.");
Console.WriteLine("FSD diagnostics: progress, combat, liveness, pagination, bounds and concurrency checks passed.");
Console.WriteLine($"Core build MVID: {typeof(DeepDungeon.Fsd.Core.FsdDiagnosticJournal).Module.ModuleVersionId}");
