using System.Diagnostics;

namespace Arkham.NativeHost;

// Observational only. A request carries its sink onto the dedicated native
// thread; no global last-result timer can be confused with another request.
public static class HostTiming
{
    static readonly AsyncLocal<Action<string,double,long,long>?> active=new();
    public static Action<string,double,long,long>? Observer=>active.Value;
    public static IDisposable Capture(Action<string,double,long,long> observer)
    {
        var scope=new CaptureScope(active.Value);active.Value=observer;return scope;
    }
    sealed class CaptureScope(Action<string,double,long,long>? previous):IDisposable
    { public void Dispose()=>active.Value=previous; }
    public static long Timestamp()=>active.Value is null?0:Stopwatch.GetTimestamp();
    public static void Observe(string name,long start,long calls=1,long bytes=0)
    { if(start!=0)active.Value?.Invoke(name,Stopwatch.GetElapsedTime(start).TotalMilliseconds,calls,bytes); }
}
