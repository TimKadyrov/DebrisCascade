using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SpaceWars.Core;

/// <summary>
/// Reads a saved credential (username + password) from the Windows Credential Manager via
/// CredRead. Only <b>generic</b> credentials expose the password blob to applications; domain
/// credentials do not (Windows withholds the password), so those cannot be used here.
/// The password is only returned to the immediate caller and never logged or persisted.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsCredential
{
    private const int CRED_TYPE_GENERIC = 1;

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    /// <summary>Try to read a generic credential by target name. Returns false if absent or password-less.</summary>
    public static bool TryRead(string target, out string user, out string password)
    {
        user = ""; password = "";
        if (!OperatingSystem.IsWindows()) return false;
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr ptr)) return false;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (c.UserName != IntPtr.Zero) user = Marshal.PtrToStringUni(c.UserName) ?? "";
            if (c.CredentialBlob != IntPtr.Zero && c.CredentialBlobSize > 0)
                password = Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2) ?? "";
            return password.Length > 0;
        }
        finally { CredFree(ptr); }
    }
}
