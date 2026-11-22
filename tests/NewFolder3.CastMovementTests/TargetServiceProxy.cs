using System.Reflection;

public class TargetServiceProxy : DispatchProxy
{
    private Func<string, object?[], object?> invoke = null!;
    internal static T Create<T>(Func<string, object?[], object?> invoke) where T : class
    {
        var proxy = Create<T, TargetServiceProxy>();
        ((TargetServiceProxy)(object)proxy).invoke = invoke;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => invoke(method!.Name, args ?? []);
}
