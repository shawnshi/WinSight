using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;

namespace WinSight.MediaIndexing.Tests;

internal sealed class ActualApi
{
    internal readonly Assembly Infrastructure;
    internal readonly Assembly Core;
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal ActualApi(string path)
    {
        string directory = Path.GetDirectoryName(path)!;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string file = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
        };
        Infrastructure = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        Core = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("WinSight.Core"));
    }
    internal Type Type(string name) => Infrastructure.GetType("WinSight.Infrastructure." + name) ?? Core.GetType("WinSight.Core." + name, true)!;
    internal object New(string name, params object?[] args) => Unwrap(() => Activator.CreateInstance(Type(name), All, null, args, null)!)!;
    internal static object? Get(object target, string name) => target.GetType().GetProperty(name, All)!.GetValue(target);
    internal static T Get<T>(object target, string name) => (T)Get(target, name)!;
    internal static object? Call(object target, string method, params object?[] args) => Unwrap(() => target.GetType().GetMethods(All).Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(target, args));
    internal static object? Unwrap(Func<object?> f)
    {
        try { return f(); } catch (TargetInvocationException e) when (e.InnerException is not null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
    internal static async Task<object?> Await(object value)
    { var task = (Task)value; await task; return task.GetType().GetProperty("Result")?.GetValue(task); }
    internal object Client(string host, string workspace)
    {
        Directory.CreateDirectory(workspace); var options = New("Media.NativeMediaProcessOptions");
        foreach (var pair in new Dictionary<string, object> { ["HostExecutable"] = host, ["WorkspaceRoot"] = workspace, ["RequestTimeout"] = TimeSpan.FromSeconds(5), ["TerminationTimeout"] = TimeSpan.FromSeconds(3) }) options.GetType().GetProperty(pair.Key)!.SetValue(options, pair.Value);
        return New("Media.NativeMediaProcessClient", options);
    }
    internal object Source(string path, string kind) => Unwrap(() => Type("Media.NativeMediaSource").GetMethod("Acquire", All)!.Invoke(null, [path, kind]))!;
    internal object Sampler(object client, int budget = 5000) => New("Media.NativeMediaSamplingService", client, budget);
    internal async Task<object> Open(object sampler, object source, CancellationToken ct = default) => (await Await(Call(sampler, "OpenAsync", source, ct)!))!;
    internal static IAsyncEnumerable<object> Samples(object session, bool image, bool audio, CancellationToken ct = default)
    {
        var enumerable = Call(session, "ReadAsync", image, audio, ct)!;
        Type item = enumerable.GetType().GetInterfaces().Single(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>)).GetGenericArguments()[0];
        return (IAsyncEnumerable<object>)typeof(ActualApi).GetMethod(nameof(Bridge), All)!.MakeGenericMethod(item).Invoke(null, [enumerable, ct])!;
    }
    private static async IAsyncEnumerable<object> Bridge<T>(IAsyncEnumerable<T> source, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    { await foreach (var value in source.WithCancellation(ct)) yield return value!; }
    internal static ValueTask Dispose(object value) => ((IAsyncDisposable)value).DisposeAsync();
    internal string[] Missing()
    {
        var needed = new[] { "Media.NativeMediaSource", "Media.NativeMediaSamplingService", "Media.NativeMediaSamplingSession", "Media.NativeMediaSample" };
        var missing = needed.Where(n => Infrastructure.GetType("WinSight.Infrastructure." + n) is null).ToList();
        var service = Infrastructure.GetType("WinSight.Infrastructure.Media.NativeMediaSamplingService");
        if (service is not null && !Type("Indexing.IndexingService").GetConstructors(All).Any(c => c.GetParameters().Length == 4 && c.GetParameters()[3].ParameterType == service)) missing.Add("IndexingService internal four-argument constructor");
        return missing.ToArray();
    }
    internal void AssertShape()
    {
        foreach (string name in new[] { "NativeMediaSource", "NativeMediaSamplingService", "NativeMediaSamplingSession", "NativeMediaSample" })
        { var t = Type("Media." + name); Check.That(t.IsSealed && t.IsNotPublic, "Fixed internal sealed seam: " + name); }
        var source = Type("Media.NativeMediaSource"); Check.That(typeof(IDisposable).IsAssignableFrom(source), "Source IDisposable");
        Check.That(source.GetMethod("Acquire", All) is { IsAssembly: true, IsStatic: true } acquire && acquire.ReturnType == source && acquire.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string), typeof(string) }), "Acquire fixed signature");
        var service = Type("Media.NativeMediaSamplingService"); var ctor = service.GetConstructors(All).Single(c => c.GetParameters().Length == 2);
        Check.That(ctor.IsAssembly && ctor.GetParameters()[1].DefaultValue is int n && n == 5000, "Default attempt budget must remain 5000");
        Check.That((string?)service.GetField("Revision", All)?.GetRawConstantValue() == "native-media-sampling-v1", "Fixed revision");
        foreach (var name in new[] { "NativeMediaSamplingSession", "NativeMediaSample" }) Check.That(typeof(IAsyncDisposable).IsAssignableFrom(Type("Media." + name)), name + " IAsyncDisposable");
        foreach (var name in new[] { "Segment", "Input" }) Check.That(Type("Media.NativeMediaSample").GetProperty(name, All) is { GetMethod.IsAssembly: true, SetMethod: null }, "Immutable sample " + name);
        Check.That(Type("IndexSegment").GetProperty("Vector")!.PropertyType == typeof(float[]), "Core contract unexpectedly changed");
    }
}

// DispatchProxy implements the real loaded Core interfaces; it is not a production API shim.
public class TestProxy : DispatchProxy
{
    internal Func<MethodInfo, object?[], object?> Handler = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    internal static object Create(Type contract, Func<MethodInfo, object?[], object?> handler)
    { var proxy = (TestProxy)DispatchProxy.Create(contract, typeof(TestProxy)); proxy.Handler = handler; return proxy; }
    internal static object TaskOf(Type valueType, object? value) => typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(valueType).Invoke(null, [value])!;
    internal static object ArrayOf(Type type, IEnumerable<object> values)
    { var a = values.ToArray(); var array = Array.CreateInstance(type, a.Length); for (int i = 0; i < a.Length; i++) array.SetValue(a[i], i); return array; }
}
