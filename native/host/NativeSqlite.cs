using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Arkham.NativeHost;

public sealed class SqliteFailure : IOException
{
    public int Code {get;}
    public SqliteFailure(int code,string message):base($"SQLite {code}: {message}"){Code=code;}
}

/// <summary>Small prepared-statement binding, not a database service.</summary>
public sealed class NativeSqlite : IDisposable
{
    const string Library="arkham_sqlite3";
    // Reviewed save copies can be nested across several rule identities. The
    // default win32 VFS has a 1040-byte pathname limit, even for \\?\ paths.
    // This built-in VFS uses the same database/locking format with long paths.
    const string WindowsVfs="win32-longpath";
    static string? Vfs=>OperatingSystem.IsWindows()?WindowsVfs:null;
    static readonly object LoadGate=new();static IntPtr loaded;static string? loadedPath;static Exception? loadFailure;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr LoadLibraryExW(string path,IntPtr file,uint flags);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern IntPtr sqlite3_libversion();
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern IntPtr sqlite3_vfs_find([MarshalAs(UnmanagedType.LPUTF8Str)]string name);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string file,out IntPtr db,int flags,[MarshalAs(UnmanagedType.LPUTF8Str)]string vfs);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_system_errno(IntPtr db);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_close(IntPtr db);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_busy_timeout(IntPtr db,int ms);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_exec(IntPtr db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,IntPtr callback,IntPtr arg,out IntPtr error);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern void sqlite3_free(IntPtr value);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_prepare_v2(IntPtr db,byte[] sql,int bytes,out IntPtr statement,IntPtr tail);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_bind_int64(IntPtr s,int i,long v);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_bind_null(IntPtr s,int i);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_bind_blob(IntPtr s,int i,byte[] v,int n,IntPtr destructor);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_bind_text(IntPtr s,int i,byte[] v,int n,IntPtr destructor);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_step(IntPtr s);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_finalize(IntPtr s);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_column_count(IntPtr s);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_column_type(IntPtr s,int i);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern long sqlite3_column_int64(IntPtr s,int i);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern IntPtr sqlite3_column_blob(IntPtr s,int i);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_column_bytes(IntPtr s,int i);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_get_autocommit(IntPtr db);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern IntPtr sqlite3_backup_init(IntPtr dst,[MarshalAs(UnmanagedType.LPUTF8Str)]string dstName,IntPtr src,[MarshalAs(UnmanagedType.LPUTF8Str)]string srcName);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_backup_step(IntPtr backup,int pages);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_backup_finish(IntPtr backup);
    IntPtr db;bool disposed;
#if NATIVE_PROBE
    internal Action? BeforeRollback {get;set;}
#endif
    public static string Version=>Marshal.PtrToStringUTF8(sqlite3_libversion())!;
    public static void Load(string absolutePath)
    {
        bool apkLib=OperatingSystem.IsAndroid()&&absolutePath.Length>0&&!absolutePath.Contains('/');
        if(!apkLib&&(!Path.IsPathFullyQualified(absolutePath)||!File.Exists(absolutePath)))throw new ArgumentException("Existing absolute SQLite DLL required");
        if(!apkLib)absolutePath=Path.GetFullPath(absolutePath);
        lock(LoadGate){
            if(loadFailure is not null)throw new InvalidOperationException("SQLite initialization previously failed; process restart required",loadFailure);
            if(loaded!=IntPtr.Zero){if(!StringComparer.OrdinalIgnoreCase.Equals(loadedPath,absolutePath))throw new InvalidOperationException("Different SQLite library already loaded");return;}
            try{loaded=OperatingSystem.IsWindows()?LoadLibraryExW(absolutePath,IntPtr.Zero,0x100|0x800):NativeLibrary.Load(absolutePath);
            if(loaded==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            NativeLibrary.SetDllImportResolver(typeof(NativeSqlite).Assembly,(name,assembly,search)=>name==Library?loaded:IntPtr.Zero);
            loadedPath=absolutePath;
            if(Version!="3.54.1")throw new InvalidOperationException("Unexpected SQLite version");
            if(OperatingSystem.IsWindows()&&sqlite3_vfs_find(WindowsVfs)==IntPtr.Zero)throw new InvalidOperationException("Pinned SQLite long-path VFS unavailable");}
            catch(Exception error){loadFailure=error;throw;}
        }
    }
    internal static string NativeDatabasePath(string absoluteFile)
    {
        if(!Path.IsPathFullyQualified(absoluteFile)||absoluteFile.Contains('\0'))throw new ArgumentException("Absolute local database file required");
        string full=Path.GetFullPath(absoluteFile);
        if(!OperatingSystem.IsWindows()||full.StartsWith(@"\\?\",StringComparison.Ordinal)||full.StartsWith(@"\\.\",StringComparison.Ordinal))return full;
        // The VFS's large pathname buffer is not Windows long-path opt-in.
        // Use extended absolute paths at the native boundary, independently
        // of the machine's LongPathsEnabled setting and host EXE manifest.
        return full.StartsWith(@"\\",StringComparison.Ordinal)?@"\\?\UNC\"+full[2..]:@"\\?\"+full;
    }
    public NativeSqlite(string absoluteFile,bool readOnly=false,bool create=true)
    {
        if(loaded==IntPtr.Zero)throw new InvalidOperationException("Load pinned SQLite first");
        string nativeFile=NativeDatabasePath(absoluteFile);
        int code=sqlite3_open_v2(nativeFile,out db,(readOnly?1:2|(create?4:0))|0x10000,Vfs!);
        if(code!=0){
            string message=db==IntPtr.Zero?"Open failed":Marshal.PtrToStringUTF8(sqlite3_errmsg(db))!;
            int osError=db==IntPtr.Zero?0:sqlite3_system_errno(db);
            if(db!=IntPtr.Zero)sqlite3_close(db);db=IntPtr.Zero;
            throw new SqliteFailure(code,$"{message}; file={absoluteFile}; pathUtf8Bytes={Encoding.UTF8.GetByteCount(absoluteFile)}; parentExists={Directory.Exists(Path.GetDirectoryName(absoluteFile))}; windowsError={osError}; vfs={WindowsVfs}; nativePathExtended={nativeFile.StartsWith(@"\\?\",StringComparison.Ordinal)}");
        }
        try{Check(sqlite3_busy_timeout(db,1500));}catch{sqlite3_close(db);db=IntPtr.Zero;throw;}
    }
    void Check(int code){if(code!=0)throw new SqliteFailure(code,Marshal.PtrToStringUTF8(sqlite3_errmsg(db))??"Unknown error");}
    void Alive(){ObjectDisposedException.ThrowIf(disposed,this);}
    public void ExecuteScript(string trustedSql)
    {
        Alive();int code=sqlite3_exec(db,trustedSql,IntPtr.Zero,IntPtr.Zero,out var error);
        string? message=error==IntPtr.Zero?null:Marshal.PtrToStringUTF8(error);if(error!=IntPtr.Zero)sqlite3_free(error);
        if(code!=0)throw new SqliteFailure(code,message??"SQL failed");
    }
    public List<object?[]> Query(string sql,params object?[] values)
    {
        Alive();byte[] text=Encoding.UTF8.GetBytes(sql+"\0");Check(sqlite3_prepare_v2(db,text,text.Length,out var statement,IntPtr.Zero));
        if(statement==IntPtr.Zero)throw new ArgumentException("Empty SQL statement");
        try{
            for(int i=0;i<values.Length;i++){
                object? value=values[i];int code;
                if(value is null)code=sqlite3_bind_null(statement,i+1);
                else if(value is long l)code=sqlite3_bind_int64(statement,i+1,l);
                else if(value is int n)code=sqlite3_bind_int64(statement,i+1,n);
                else if(value is byte[] bytes)code=sqlite3_bind_blob(statement,i+1,bytes,bytes.Length,(IntPtr)(-1));
                else if(value is string s){byte[] utf8=Encoding.UTF8.GetBytes(s+"\0");code=sqlite3_bind_text(statement,i+1,utf8,utf8.Length-1,(IntPtr)(-1));}
                else throw new ArgumentException("Unsupported SQL parameter type");
                Check(code);
            }
            var rows=new List<object?[]>();int status;
            while((status=sqlite3_step(statement))==100){
                var row=new object?[sqlite3_column_count(statement)];
                for(int i=0;i<row.Length;i++){
                    int type=sqlite3_column_type(statement,i);
                    if(type==5)continue;if(type==1){row[i]=sqlite3_column_int64(statement,i);continue;}
                    if(type is not (3 or 4))throw new InvalidDataException("Unexpected SQLite column type");
                    int size=sqlite3_column_bytes(statement,i);byte[] data=new byte[size];if(size>0)Marshal.Copy(sqlite3_column_blob(statement,i),data,0,size);
                    row[i]=type==3?Encoding.UTF8.GetString(data):data;
                }rows.Add(row);
            }
            if(status!=101)Check(status);return rows;
        }finally{sqlite3_finalize(statement);}
    }
    public T Transaction<T>(Func<T> action)
    {
        long at=HostTiming.Timestamp();
        ExecuteScript("BEGIN IMMEDIATE");
        HostTiming.Observe("sqlite.begin",at);
        try{at=HostTiming.Timestamp();T value=action();HostTiming.Observe("sqlite.body",at);at=HostTiming.Timestamp();ExecuteScript("COMMIT");HostTiming.Observe("sqlite.commitFlush",at);return value;}
        catch(Exception operation){
            try{
#if NATIVE_PROBE
                BeforeRollback?.Invoke();
#endif
                if(sqlite3_get_autocommit(db)==0)ExecuteScript("ROLLBACK");}
            catch(Exception rollback){throw new AggregateException("Transaction and rollback failed; close and recover database",operation,rollback);}
            throw;
        }
    }
    public void BackupTo(string newAbsoluteFile)
    {
        Alive();if(!Path.IsPathFullyQualified(newAbsoluteFile)||File.Exists(newAbsoluteFile))throw new IOException("New absolute backup destination required");
        string temporary=newAbsoluteFile+".partial-"+Guid.NewGuid().ToString("N");bool owned=false;
        try{
            using(var reserve=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){owned=true;reserve.Flush(true);}
            using(var destination=new NativeSqlite(temporary)){
                destination.ExecuteScript("PRAGMA synchronous=FULL");
                var backup=sqlite3_backup_init(destination.db,"main",db,"main");if(backup==IntPtr.Zero)destination.Check(1);
                int step,finish;try{step=sqlite3_backup_step(backup,-1);}finally{finish=sqlite3_backup_finish(backup);}
                destination.Check(finish);if(step!=101)destination.Check(step);
                if((string)destination.Query("PRAGMA quick_check")[0][0]! !="ok")throw new InvalidDataException("Backup integrity failed");
            }
            File.Move(temporary,newAbsoluteFile,false);owned=false;
        }finally{if(owned)File.Delete(temporary);}
    }
    public void Dispose(){if(disposed)return;Check(sqlite3_close(db));disposed=true;db=IntPtr.Zero;}
}
