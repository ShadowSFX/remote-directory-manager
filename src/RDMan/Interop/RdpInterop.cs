using System.Runtime.InteropServices;

namespace RDMan.Interop;

/// <summary>
/// IMsRdpClientNonScriptable re-declared with real arrays for SendKeys. The type-library import exposes
/// SendKeys(int, ref bool, ref int), which only marshals the first key — useless for Ctrl+Alt+Del.
/// GUID and vtable order verified against Interop.MSTSCLib (IMsTscNonScriptable methods first).
/// </summary>
[ComImport, Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpClientNonScriptableKeys
{
    void SetClearTextPassword([MarshalAs(UnmanagedType.BStr)] string value);
    void SetPortablePassword([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetPortablePassword();
    void SetPortableSalt([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetPortableSalt();
    void SetBinaryPassword([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetBinaryPassword();
    void SetBinarySalt([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetBinarySalt();
    void ResetPassword();
    void NotifyRedirectDeviceChange(UIntPtr wParam, IntPtr lParam);
    void SendKeys(int numKeys,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.VariantBool, SizeParamIndex = 0)] bool[] keyUp,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] int[] keyData);
}

/// <summary>Reads only the *user name* of credentials Windows saved for a computer (never the secret).</summary>
internal static class WindowsCredentialLookup
{
    private const int CRED_TYPE_DOMAIN_PASSWORD = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string? filter, int flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    /// <summary>
    /// The user name Remote Desktop Connection saved for <paramref name="host"/> ("TERMSRV/host"), or the
    /// domain credential saved for the host itself. Null when Windows has none.
    /// </summary>
    public static string? FindUser(string host)
    {
        foreach (var target in new[] { "TERMSRV/" + host, host })
        {
            if (!CredRead(target, CRED_TYPE_DOMAIN_PASSWORD, 0, out var ptr)) continue;
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                if (!string.IsNullOrWhiteSpace(cred.UserName)) return cred.UserName;
            }
            finally
            {
                CredFree(ptr);
            }
        }
        return null;
    }

    /// <summary>Hosts Remote Desktop Connection saved credentials for (target names "TERMSRV/…"), with their user names.</summary>
    public static IReadOnlyList<(string Host, string? User)> SavedRemoteDesktopTargets()
    {
        var result = new List<(string, string?)>();
        if (!CredEnumerate("TERMSRV/*", 0, out var count, out var list)) return result;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CREDENTIAL>(item);
                var host = cred.TargetName?.StartsWith("TERMSRV/", StringComparison.OrdinalIgnoreCase) == true ? cred.TargetName[8..] : null;
                if (!string.IsNullOrWhiteSpace(host)) result.Add((host, cred.UserName));
            }
        }
        finally
        {
            CredFree(list);
        }
        return result;
    }
}
