#Requires -Version 7.6
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This experiment requires Windows.' }

# Exposes the Windows SQLite engine without adding a package or production dependency.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class RenameSqlite {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Callback(IntPtr arg, int count, IntPtr values, IntPtr names);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_open([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, Callback callback, IntPtr arg, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern void sqlite3_free(IntPtr value);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr sqlite3_libversion();
    public static string Version() => Marshal.PtrToStringUTF8(sqlite3_libversion());
    // Every call opens and closes a fresh native connection; failed transactions roll back on close.
    public static List<Dictionary<string,string>> Query(string path, string sql) {
        IntPtr db; if(sqlite3_open(path, out db)!=0) throw new Exception("SQLite open failed");
        var rows=new List<Dictionary<string,string>>();
        Callback callback=(arg,count,values,names)=> {
            var row=new Dictionary<string,string>();
            for(int i=0;i<count;i++) row[Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(names,i*IntPtr.Size))]=Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(values,i*IntPtr.Size));
            rows.Add(row); return 0;
        };
        try {
            IntPtr error; int rc=sqlite3_exec(db,"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;",null,IntPtr.Zero,out error);
            if(rc!=0) { string detail=Marshal.PtrToStringUTF8(error); sqlite3_free(error); throw new Exception(detail); }
            rc=sqlite3_exec(db,sql,callback,IntPtr.Zero,out error);
            if(rc!=0) { string detail=Marshal.PtrToStringUTF8(error); sqlite3_free(error); throw new Exception(detail); }
            return rows;
        } finally { sqlite3_close(db); GC.KeepAlive(callback); }
    }
}
'@
