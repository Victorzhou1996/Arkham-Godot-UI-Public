using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Arkham.NativeHost;

/// <summary>
/// One process-lifetime RTS and one non-render worker. No rule state is inferred
/// here. Callers own returned session IDs and must release them before closing.
/// Native failures/OOM are not made recoverable by this managed boundary.
/// </summary>
public sealed class NativeCoreWorker : IAsyncDisposable
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")]static extern bool GetThreadTimes(IntPtr thread,out long created,out long ended,out long kernel,out long user);
    [DllImport("kernel32.dll")]static extern uint GetCurrentProcessorNumber();
    static long ThreadCpuTicks()=>!OperatingSystem.IsWindows()?0:GetThreadTimes(new IntPtr(-2),out _,out _,out var kernel,out var user)?kernel+user:0;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CallFn(IntPtr data, ulong size, out ulong result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SizeFn(ulong result, out ulong size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CopyFn(ulong result, IntPtr data, ulong capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ReleaseFn(ulong result);
    sealed record Command(byte[]? Input, TaskCompletionSource<JsonElement> Completion,Action<string,double,long,long>? Observer);
    static int ownerClaimed;
    static readonly object RecordGate=new();
    readonly object admission = new();
    readonly BlockingCollection<Command> commands = new(128);
    readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Thread thread;
    readonly string dllPath,expectedSourceId;
    bool accepting=true;
    long queuedBytes;
    Task<bool>? closingTask;
    const long QueueByteBudget=128L*1024*1024;
#if NATIVE_PROBE
    internal Action<string>? FaultInjection {get;set;}
#endif
    NoArgs init=null!,shutdown=null!;CallFn call=null!;SizeFn size=null!;CopyFn copy=null!;ReleaseFn release=null!;
    IntPtr module;
    public int WorkerThreadId {get;private set;}
    public Task Ready => initialized.Task;

    public NativeCoreWorker(string absoluteDll,string sourceId)
    {
        bool apkLib=OperatingSystem.IsAndroid()&&absoluteDll.Length>0&&!absoluteDll.Contains('/');
        if(!apkLib&&(!Path.IsPathFullyQualified(absoluteDll)||!File.Exists(absoluteDll)))throw new ArgumentException("Existing absolute DLL path required");
        if(!sourceId.StartsWith("arkham-core-sha256:",StringComparison.Ordinal)||sourceId.Length!=83)throw new ArgumentException("Full expected source ID required");
        // Never reset after a partially initialized/closed RTS. A fresh process
        // is required, rather than risking a second runtime or library unload.
        if(Interlocked.CompareExchange(ref ownerClaimed,1,0)!=0)throw new InvalidOperationException("Only one native core owner is allowed for the process lifetime");
        dllPath=apkLib?absoluteDll:Path.GetFullPath(absoluteDll);expectedSourceId=sourceId;
        thread=new Thread(Work){IsBackground=true,Name="Arkham rule core"};thread.Start();
    }
    T Export<T>(string name)where T:Delegate
    {var p=OperatingSystem.IsWindows()?GetProcAddress(module,name):(NativeLibrary.TryGetExport(module,name,out var e)?e:IntPtr.Zero);if(p==IntPtr.Zero)throw new EntryPointNotFoundException(name);return Marshal.GetDelegateForFunctionPointer<T>(p);}
    public Task<JsonElement> RequestAsync<T>(T request)
    {
        byte[] input=JsonSerializer.SerializeToUtf8Bytes(request);
        if(input.Length>64*1024*1024)throw new ArgumentException("Native request exceeds 64 MiB");
        return Enqueue(input);
    }
    Task<JsonElement> Enqueue(byte[]? input)
    {
        if(input is not null&&Environment.GetEnvironmentVariable("ARKHAM_CORE_RECORD") is {Length:>0} rec)lock(RecordGate)File.AppendAllText(rec,System.Text.Encoding.UTF8.GetString(input)+"\n");
        var command=new Command(input,new(TaskCreationOptions.RunContinuationsAsynchronously),HostTiming.Observer);
        lock(admission){
            if(!accepting)throw new ObjectDisposedException(nameof(NativeCoreWorker));
            if(queuedBytes+(input?.LongLength??0)>QueueByteBudget)throw new InvalidOperationException("Native queued-byte budget exceeded; no command was submitted");
            if(!commands.TryAdd(command))throw new InvalidOperationException("Native work queue is full; no command was submitted");
            queuedBytes+=input?.LongLength??0;
        }
        return command.Completion.Task;
    }
    public Task<bool> TryShutdownAsync()
    {
        lock(admission){
            if(closingTask is not null)return closingTask;
            if(!accepting)return WaitExited();
            var command=new Command(null,new(TaskCreationOptions.RunContinuationsAsynchronously),HostTiming.Observer);
            if(!commands.TryAdd(command))throw new InvalidOperationException("Native work queue is full; close was not submitted");
            // A close barrier rejects new work while all earlier work drains.
            accepting=false;closingTask=WaitClose(command.Completion.Task);return closingTask;
        }
    }
    async Task<bool> WaitExited(){await exited.Task.ConfigureAwait(false);return true;}
    async Task<bool> WaitClose(Task<JsonElement> completion){var value=await completion.ConfigureAwait(false);bool closed=value.GetProperty("closed").GetBoolean();if(closed)await exited.Task.ConfigureAwait(false);return closed;}
    public async ValueTask DisposeAsync()
    {
        if(!await TryShutdownAsync().ConfigureAwait(false))throw new InvalidOperationException("Native sessions remain live; release them before shutdown");
    }
    unsafe JsonElement Invoke(byte[] input,Action<string,double,long,long>? observer=null)
    {
        if(Environment.CurrentManagedThreadId!=WorkerThreadId)throw new InvalidOperationException("Rule call outside dedicated worker");
        bool trace=observer is not null||Environment.GetEnvironmentVariable("BASE_TRACE_TIMINGS")=="1";
        string? traceMethod=null;
        if(trace){using var request=JsonDocument.Parse(input);traceMethod=request.RootElement.GetProperty("method").GetString();}
        long start=trace?Stopwatch.GetTimestamp():0,called=0,copied=0,parsed=0;ulong outputBytes=0;
        long cpuBefore=trace?ThreadCpuTicks():0,cpuAfter=0;uint processorBefore=trace&&OperatingSystem.IsWindows()?GetCurrentProcessorNumber():0,processorAfter=0;
        ulong token;
        fixed(byte* p=input){int status=call((IntPtr)p,(ulong)input.Length,out token);if(status!=0||token==0)throw new InvalidOperationException($"Native transport rejected request: status {status}");}
        if(trace){called=Stopwatch.GetTimestamp();cpuAfter=ThreadCpuTicks();processorAfter=OperatingSystem.IsWindows()?GetCurrentProcessorNumber():0;}
        Exception? operationError=null;JsonElement result=default;
        try{
            int status=size(token,out ulong length);
            if(status!=0||length==0||length>64*1024*1024)throw new InvalidOperationException("Invalid native result size");
            outputBytes=length;
            byte[] output=new byte[checked((int)length)];
            fixed(byte* p=output)if(copy(token,(IntPtr)p,length)!=0)throw new InvalidOperationException("Native result copy failed");
            if(trace)copied=Stopwatch.GetTimestamp();
#if NATIVE_PROBE
            FaultInjection?.Invoke("copied");
#endif
            using var json=JsonDocument.Parse(output);result=json.RootElement.Clone();
            if(trace)parsed=Stopwatch.GetTimestamp();
        }catch(Exception error){operationError=error;}
        Exception? releaseError=null;
        try{if(release(token)!=0)throw new InvalidOperationException("Native result release failed");
#if NATIVE_PROBE
            FaultInjection?.Invoke("released");
#endif
        }catch(Exception error){releaseError=error;}
        if(operationError is not null&&releaseError is not null)throw new AggregateException("Native copy/parse and release both failed; process restart required",operationError,releaseError);
        if(operationError is not null)throw new InvalidOperationException("Native copy/parse failed; process restart required",operationError);
        if(releaseError is not null)throw new InvalidOperationException("Native result release failed; process restart required",releaseError);
        if(trace){long ended=Stopwatch.GetTimestamp();
            observer?.Invoke("native."+traceMethod+".call",Stopwatch.GetElapsedTime(start,called).TotalMilliseconds,1,(long)outputBytes);
            observer?.Invoke("native."+traceMethod+".copy",Stopwatch.GetElapsedTime(called,copied).TotalMilliseconds,1,(long)outputBytes);
            observer?.Invoke("native."+traceMethod+".parseClone",Stopwatch.GetElapsedTime(copied,parsed).TotalMilliseconds,1,(long)outputBytes);
            observer?.Invoke("native."+traceMethod+".release",Stopwatch.GetElapsedTime(parsed,ended).TotalMilliseconds,1,0);
            Console.Error.WriteLine("NATIVE_TIMING "+JsonSerializer.Serialize(new{method=traceMethod,inputBytes=input.Length,outputBytes,totalMs=Stopwatch.GetElapsedTime(start,ended).TotalMilliseconds,callMs=Stopwatch.GetElapsedTime(start,called).TotalMilliseconds,copyMs=Stopwatch.GetElapsedTime(called,copied).TotalMilliseconds,parseCloneMs=Stopwatch.GetElapsedTime(copied,parsed).TotalMilliseconds,releaseMs=Stopwatch.GetElapsedTime(parsed,ended).TotalMilliseconds,nativeThreadCpuMs=cpuBefore>0&&cpuAfter>=cpuBefore?(cpuAfter-cpuBefore)/10000.0:(double?)null,processorBefore,processorAfter}));}
        return result;
    }
    void Work()
    {
        WorkerThreadId=Environment.CurrentManagedThreadId;
        Exception? stopped=null;
        try{
            module=OperatingSystem.IsWindows()?LoadLibraryExW(dllPath,IntPtr.Zero,0x100|0x800):NativeLibrary.Load(dllPath);
            if(module==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            init=Export<NoArgs>("ah_core_init");shutdown=Export<NoArgs>("ah_core_shutdown");
            call=Export<CallFn>("ah_core_call");size=Export<SizeFn>("ah_core_result_size");copy=Export<CopyFn>("ah_core_result_copy");release=Export<ReleaseFn>("ah_core_result_release");
            if(init()!=0)throw new InvalidOperationException("Native runtime initialization failed");
            var info=Invoke(JsonSerializer.SerializeToUtf8Bytes(new{apiVersion=1,method="info"}));
            if(!info.GetProperty("ok").GetBoolean()||info.GetProperty("abiVersion").GetInt32()!=1||info.GetProperty("viewVersion").GetInt32()!=1||info.GetProperty("snapshotVersion").GetInt32()!=2||info.GetProperty("coreSourceId").GetString()!=expectedSourceId)throw new InvalidOperationException("Native version/source handshake mismatch; restart with a compatible core");
            initialized.TrySetResult();
            foreach(var command in commands.GetConsumingEnumerable()){
                lock(admission){queuedBytes-=command.Input?.LongLength??0;}
                try{
                    if(command.Input is not null){command.Completion.TrySetResult(Invoke(command.Input,command.Observer));continue;}
                    int status=shutdown();
                    if(status==6){lock(admission){accepting=true;closingTask=null;}command.Completion.TrySetResult(JsonSerializer.SerializeToElement(new{closed=false}));continue;}
                    if(status!=0)throw new InvalidOperationException($"Native shutdown failed: {status}");
                    lock(admission){accepting=false;commands.CompleteAdding();}
                    command.Completion.TrySetResult(JsonSerializer.SerializeToElement(new{closed=true}));break;
                }catch(Exception error){stopped=error;command.Completion.TrySetException(error);break;}
            }
        }catch(Exception error){stopped=error;initialized.TrySetException(error);}
        finally{
            lock(admission){accepting=false;commands.CompleteAdding();}
            while(commands.TryTake(out var pending))pending.Completion.TrySetException(stopped??new ObjectDisposedException(nameof(NativeCoreWorker)));
            GC.KeepAlive(module); // Never FreeLibrary; hs_exit does not permit RTS reinitialization.
            if(stopped is null)exited.TrySetResult();else exited.TrySetException(stopped);
        }
    }
}
