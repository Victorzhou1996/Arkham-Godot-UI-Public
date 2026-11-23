using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Arkham.NativeHost;

// Current-Windows-user DPAPI only. No plaintext fallback, credential-manager
// entry, system certificate installation or administrator elevation.
public static class LanSecrets
{
    [StructLayout(LayoutKind.Sequential)]struct Blob{public int Length;public IntPtr Data;}
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]static extern bool CryptProtectData(ref Blob data,string description,IntPtr entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]static extern bool CryptUnprotectData(ref Blob data,out IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr memory);
    static byte[] Transform(byte[] bytes,bool protect)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Windows LAN secrets require current-user DPAPI");
        if(bytes.Length is <1 or >1048576)throw new InvalidDataException("LAN secret size invalid");
        var input=new Blob{Length=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};Blob output=default;IntPtr description=IntPtr.Zero;
        try{
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            bool success=protect?CryptProtectData(ref input,"Arkham native local LAN identity",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):CryptUnprotectData(ref input,out description,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
            if(!success)throw new Win32Exception(Marshal.GetLastWin32Error(),"LAN secret cannot be protected/restored for this Windows user");
            if(output.Length is <1 or >1048576)throw new InvalidDataException("Invalid protected LAN secret");var result=new byte[output.Length];Marshal.Copy(output.Data,result,0,result.Length);return result;
        }finally{
            Marshal.Copy(new byte[input.Length],0,input.Data,input.Length);Marshal.FreeHGlobal(input.Data);
            if(output.Data!=IntPtr.Zero){if(!protect&&output.Length>0&&output.Length<=1048576)Marshal.Copy(new byte[output.Length],0,output.Data,output.Length);LocalFree(output.Data);}
            if(description!=IntPtr.Zero)LocalFree(description);
        }
    }
    public static byte[] Protect(byte[] plaintext)=>Transform(plaintext,true);
    public static byte[] Unprotect(byte[] protectedBytes)=>Transform(protectedBytes,false);
    public static string NewToken()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string TokenHash(string token)
    {
        if(token.Length!=64||token.Any(c=>c is not(>='0' and <='9') and not(>='a' and <='f')))throw new InvalidDataException("Invalid LAN reconnect token");
        return Convert.ToHexString(SHA256.HashData(Convert.FromHexString(token))).ToLowerInvariant();
    }
}
