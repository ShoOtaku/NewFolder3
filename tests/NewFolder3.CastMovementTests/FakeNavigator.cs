using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

internal sealed class FakeNavigator : INavigator
{
    public int Starts { get; private set; }
    public int Cancels { get; private set; }
    public int Repaths { get; private set; }
    public bool Running { get; private set; }
    public bool PathfindAndMoveTo(Vector3 dest, bool alignCamera) { Starts++; Running = true; return true; }
    public bool RepathTo(Vector3 dest, bool alignCamera) { Repaths++; return true; }
    public void CancelAll() { Cancels++; Running = false; }
}
