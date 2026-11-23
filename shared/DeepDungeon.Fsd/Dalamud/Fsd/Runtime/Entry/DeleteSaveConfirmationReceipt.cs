using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry;

/// <summary>A single confirmation created by our slot request, never a later lookalike dialog.</summary>
internal sealed class DeleteSaveConfirmationReceipt
{
    private SaveSlotSnapshot? source;
    private nint dialog;
    private string prompt = "";

    internal bool IsArmed => source.HasValue;

    internal void Arm(SaveSlotSnapshot target, nint address, string text)
    {
        Invalidate();
        if (target.Empty || string.IsNullOrWhiteSpace(target.Identity) ||
            address == 0 || string.IsNullOrWhiteSpace(text))
            return;
        source = target;
        dialog = address;
        prompt = text;
    }

    internal bool TryConsume(SaveSlotSnapshot target, nint address, string text)
    {
        bool matches = source == target && dialog == address && prompt == text && IsArmed;
        Invalidate();
        return matches;
    }

    internal void Invalidate()
    {
        source = null;
        dialog = 0;
        prompt = "";
    }
}
