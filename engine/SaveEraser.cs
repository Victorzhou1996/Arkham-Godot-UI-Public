using System.Runtime.InteropServices;
using System.Text;

namespace Gathering;

// Deleting a save for good (Yunke 11-20: "这个界面里删除就是真删除了，不要留本地文件"). The game store inside the native host has
// no delete, and its database keeps every saved state immutable with triggers (states_no_delete, …). So this talks to the
// bundled sqlite3.dll directly and, in one transaction: takes the triggers down, deletes every row of that game from every
// table that has a game_id (and its row in games), then puts the triggers back exactly as they were. Any error rolls the whole
// thing back, the triggers included. The game's own files beside the database (setups/<id>…) are deleted afterwards.
public static class SaveEraser
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int OpenV2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Close(IntPtr db);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Exec(IntPtr db, byte[] sql, IntPtr cb, IntPtr arg, out IntPtr err);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PrepareV2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Step(IntPtr stmt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ColumnText(IntPtr stmt, int col);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Finalize(IntPtr stmt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ErrMsg(IntPtr db);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Free(IntPtr p);

    const int SQLITE_OK = 0, SQLITE_ROW = 100, OPEN_READWRITE = 0x2, OPEN_FULLMUTEX = 0x10000;

    static T Fn<T>(IntPtr lib, string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
    static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    public static void Erase(string sqliteDll, string dbFile, string dataDirectory, Guid game)
    {
        string id = game.ToString("D");   // a parsed GUID: safe to put in the SQL text
        var lib = NativeLibrary.Load(sqliteDll);
        var open = Fn<OpenV2>(lib, "sqlite3_open_v2"); var close = Fn<Close>(lib, "sqlite3_close_v2");
        var exec = Fn<Exec>(lib, "sqlite3_exec"); var prepare = Fn<PrepareV2>(lib, "sqlite3_prepare_v2");
        var step = Fn<Step>(lib, "sqlite3_step"); var text = Fn<ColumnText>(lib, "sqlite3_column_text"); var fin = Fn<Finalize>(lib, "sqlite3_finalize");
        var errmsg = Fn<ErrMsg>(lib, "sqlite3_errmsg"); var free = Fn<Free>(lib, "sqlite3_free");
        if (open(Z(dbFile), out var db, OPEN_READWRITE | OPEN_FULLMUTEX, IntPtr.Zero) != SQLITE_OK) throw new InvalidOperationException("打不开存档数据库");
        try
        {
            void Run(string sql)
            {
                if (exec(db, Z(sql), IntPtr.Zero, IntPtr.Zero, out var err) != SQLITE_OK)
                {
                    string msg = err != IntPtr.Zero ? Marshal.PtrToStringUTF8(err) ?? "" : Marshal.PtrToStringUTF8(errmsg(db)) ?? "";
                    if (err != IntPtr.Zero) free(err);
                    throw new InvalidOperationException("删除存档失败：" + msg);
                }
            }
            List<string[]> Query(string sql, int cols)
            {
                var rows = new List<string[]>();
                if (prepare(db, Z(sql), -1, out var st, IntPtr.Zero) != SQLITE_OK) throw new InvalidOperationException("删除存档失败：" + Marshal.PtrToStringUTF8(errmsg(db)));
                try { while (step(st) == SQLITE_ROW) rows.Add(Enumerable.Range(0, cols).Select(i => Marshal.PtrToStringUTF8(text(st, i)) ?? "").ToArray()); }
                finally { fin(st); }
                return rows;
            }
            Run("PRAGMA busy_timeout=5000");
            Run("BEGIN IMMEDIATE");
            try
            {
                var triggers = Query("SELECT name, sql FROM sqlite_master WHERE type='trigger' AND sql IS NOT NULL", 2);
                foreach (var t in triggers) Run("DROP TRIGGER \"" + t[0].Replace("\"", "\"\"") + "\"");
                var tables = Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'", 1).Select(r => r[0]).ToList();
                // rows that point at a state first (requests, history, undo), the game row, the states last
                foreach (var table in tables.OrderBy(t => t == "states" ? 2 : t == "games" ? 1 : 0))
                {
                    var cols = Query("PRAGMA table_info(\"" + table.Replace("\"", "\"\"") + "\")", 2).Select(r => r[1]).ToList();
                    string q = "\"" + table.Replace("\"", "\"\"") + "\"";
                    if (cols.Contains("game_id")) Run("DELETE FROM " + q + " WHERE game_id='" + id + "'");
                    else if (table == "games" && cols.Contains("id")) Run("DELETE FROM " + q + " WHERE id='" + id + "'");
                }
                foreach (var t in triggers) Run(t[1]);
                Run("COMMIT");
            }
            catch { try { Run("ROLLBACK"); } catch { } throw; }
            // the freed pages still hold the old data until the file is rebuilt: compact it, so nothing of the game is left in it
            // (a busy database is left as it is — the rows are gone either way)
            try { Run("VACUUM"); } catch (Exception e) { Console.WriteLine("save erase: vacuum skipped: " + e.Message); }
        }
        finally { close(db); }
        // the game's own files: setups/<id>.json, -story, -upgrade-N, -xp-carry-N, -trauma …
        var setups = Path.Combine(dataDirectory, "setups");
        if (Directory.Exists(setups))
            foreach (var f in Directory.GetFiles(setups, id + "*")) File.Delete(f);
    }
}
