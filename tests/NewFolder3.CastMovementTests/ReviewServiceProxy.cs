using System.Reflection;

public class ReviewServiceProxy : DispatchProxy
{
    private Func<MethodInfo, object?[], object?> invoke = null!;

    internal static object Create(Type type, Func<MethodInfo, object?[], object?> invoke)
    {
        var proxy = (ReviewServiceProxy)Create(type, typeof(ReviewServiceProxy));
        proxy.invoke = invoke;
        return proxy;
    }

    internal static T Create<T>(Func<MethodInfo, object?[], object?> invoke) where T : class =>
        (T)Create(typeof(T), invoke);

    protected override object? Invoke(MethodInfo? method, object?[]? args) => invoke(method!, args ?? []);
}
