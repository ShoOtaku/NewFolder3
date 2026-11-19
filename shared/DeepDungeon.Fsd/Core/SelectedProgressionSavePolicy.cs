namespace DeepDungeon.Fsd.Core;

public static class SelectedProgressionSavePolicy
{
    public static bool TrySelect(IReadOnlyList<SaveSlotSnapshot> slots, int selectedSlot, int maxFloor,
        out SaveSlotSnapshot source, out string error)
    {
        source = default;
        error = string.Empty;
        if (selectedSlot is < 0 or > 1)
        {
            error = "死靈術士存檔槽位無效。";
            return false;
        }

        int matches = 0;
        foreach (var slot in slots)
        {
            if (slot.Index != selectedSlot) continue;
            source = slot;
            matches++;
        }
        if (matches != 1)
            error = $"存檔 {selectedSlot + 1} 尚未完整載入或資料不唯一；不改選其他槽位。";
        else if (!source.Enterable)
            error = $"存檔 {selectedSlot + 1} 目前不可入場；保留存檔，不改選其他槽位。";
        else if (!source.Empty && (source.StartFloor < 1 || source.StartFloor > maxFloor - 9 || source.StartFloor % 10 != 1))
            error = $"存檔 {selectedSlot + 1} 的進度無法可靠識別；保留存檔。";
        return error.Length == 0;
    }
}
