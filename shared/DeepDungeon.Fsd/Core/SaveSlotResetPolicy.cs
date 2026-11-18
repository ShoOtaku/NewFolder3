namespace DeepDungeon.Fsd.Core;

/// <summary>Pins one occupied slot before a fixed-floorset run creates a new save.</summary>
public static class SaveSlotResetPolicy
{
    public static bool TrySelect(IReadOnlyList<SaveSlotSnapshot> slots, int preferredSlot,
        out SaveSlotSnapshot source, out string error)
    {
        source = default;
        error = "存檔列表不完整或槽位無法唯一識別；不重置存檔。";
        if (slots.Count != 2 || preferredSlot is < 0 or > 1 ||
            slots.Any(slot => slot.Index is < 0 or > 1) || slots.Select(slot => slot.Index).Distinct().Count() != 2)
            return false;
        if (slots.Any(slot => slot.Empty))
        {
            error = "已有空槽；等待空槽可入場，不重置其他存檔。";
            return false;
        }
        var selected = slots.Single(slot => slot.Index == preferredSlot);
        if (selected.StartFloor < 1 || string.IsNullOrWhiteSpace(selected.Progress) || string.IsNullOrWhiteSpace(selected.Identity))
        {
            error = "目標存檔內容尚未可靠識別；不重置存檔。";
            return false;
        }
        source = selected;
        error = "";
        return true;
    }

    public static string? Validate(IReadOnlyList<SaveSlotSnapshot> slots, SaveSlotSnapshot expected)
    {
        var matches = slots.Where(slot => slot.Index == expected.Index).ToArray();
        if (matches.Length != 1 || expected.Empty || string.IsNullOrWhiteSpace(expected.Identity))
            return "重置目標無法唯一識別；停止重置。";
        var actual = matches[0];
        // Entry and reset lists can expose different enabled states for the same row.
        if (actual.Empty || actual.StartFloor != expected.StartFloor || actual.Progress != expected.Progress || actual.Identity != expected.Identity)
            return "重置目標存檔內容已變更；停止重置，不改選其他槽位。";
        return null;
    }
}
