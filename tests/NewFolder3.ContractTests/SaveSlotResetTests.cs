using DeepDungeon.Fsd.Core;

namespace NewFolder3;

internal static class SaveSlotResetTests
{
    public static void Run()
    {
        var first = new SaveSlotSnapshot(0, false, 21, "第21朝圣路", "job-a");
        var second = new SaveSlotSnapshot(1, false, 31, "第31朝圣路", "job-b");
        Check(SaveSlotResetPolicy.TrySelect([second, first], 0, out var selected, out _) && selected == first,
            "Full slots must deterministically pin the preferred slot, independent of row order");
        Check(SaveSlotResetPolicy.TrySelect([first, second], 1, out selected, out _) && selected == second,
            "A requested slot must never silently switch to the other save");
        var empty = first with { Empty = true, StartFloor = 0, Progress = "空" };
        Check(!SaveSlotResetPolicy.TrySelect([empty, second], 0, out _, out _), "An existing empty slot must prevent reset");
        Check(!SaveSlotResetPolicy.TrySelect([empty with { Enterable = false }, second], 0, out _, out _),
            "A disabled empty row must not cause deletion of the other save");
        Check(!SaveSlotResetPolicy.TrySelect([first], 0, out _, out _), "Partial lists must not authorize reset");
        Check(!SaveSlotResetPolicy.TrySelect([first, first], 0, out _, out _), "Duplicate indices must not authorize reset");
        Check(!SaveSlotResetPolicy.TrySelect([first with { Identity = "" }, second], 0, out _, out _),
            "Unidentified saves must not be reset");
        Check(SaveSlotResetPolicy.Validate([first with { Enterable = false }, second], first) == null,
            "Changing between entry and delete UI must preserve target identity despite row enablement");
        foreach (var changed in new[]
        {
            first with { Identity = "replacement" }, first with { Progress = "第31朝圣路", StartFloor = 31 },
            first with { Empty = true }, first with { Index = 1 }
        })
            Check(SaveSlotResetPolicy.Validate([changed, second], first) != null,
                "A changed or ambiguous target must stop reset instead of selecting another slot");
        Check(SaveSlotResetPolicy.Validate([first, second with { Identity = "other-save-changed" }], first) == null,
            "Only the pinned slot is authorized; the other slot is never selected");
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }
}
