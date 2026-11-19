using DeepDungeon.Fsd.Core;

namespace NewFolder3;

internal static class EntryMenuChoiceTests
{
    public static void Run()
    {
        const string reset = "重置攻略进度";
        string[] beforeClear = ["进入朝圣路", "查看攻略记录", reset, "取消"];
        string[] afterClear = ["进入朝圣路", "通关后解锁选项", "查看攻略记录", reset, "取消"];

        Check(EntryMenuChoice.FindUnique(beforeClear, reset) == 2,
            "Before floor 100 is cleared, reset must follow its label rather than the former fixed index 3");
        Check(EntryMenuChoice.FindUnique(afterClear, reset) == 3,
            "An additional unlocked option must not change which action is selected");
        Check(EntryMenuChoice.FindUnique([reset, "进入朝圣路", "取消"], reset) == 0,
            "Reset must still be found when it becomes the first menu option");
        Check(EntryMenuChoice.FindUnique(["进入朝圣路", "取消"], reset) == -1,
            "A missing reset option must not fall back to an ordinal");
        Check(EntryMenuChoice.FindUnique([reset, "进入朝圣路", reset], reset) == -1,
            "Duplicate reset labels must not authorize an ambiguous action");
        Check(EntryMenuChoice.FindUnique(["重置攻略进度说明", "取消"], reset) == -1,
            "A partial text match must not select a different action");
        Check(EntryMenuChoice.FindUnique([], reset) == -1 &&
            EntryMenuChoice.FindUnique([""], "") == -1,
            "Incomplete menus or unavailable localized text must not select an action");
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }
}
