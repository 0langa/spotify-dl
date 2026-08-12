using System.Runtime.InteropServices;
using System.Text;

namespace PlaylistDl.App.Services;

/// <summary>One stored secret pair.</summary>
public sealed record SpotifyCredentials(string ClientId, string ClientSecret);

/// <summary>
/// Stores optional Spotify API credentials in Windows Credential Manager.
/// </summary>
/// <remarks>
/// Secrets never reach settings.json or the run log: only the fact that credentials
/// exist is kept in settings, and the values live in the credential vault of the
/// signed-in Windows account.
/// </remarks>
public sealed class CredentialStore
{
    private const string DefaultTarget = "PlaylistDL:SpotifyApi";
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    private readonly string _target;

    public CredentialStore(string? target = null) => _target = target ?? DefaultTarget;

    public bool Save(SpotifyCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var secret = Encoding.Unicode.GetBytes(credentials.ClientSecret);
        var secretPointer = Marshal.AllocHGlobal(secret.Length);
        try
        {
            Marshal.Copy(secret, 0, secretPointer, secret.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = _target,
                UserName = credentials.ClientId,
                CredentialBlob = secretPointer,
                CredentialBlobSize = secret.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            return CredWriteW(ref credential, 0);
        }
        finally
        {
            // The buffer holds raw bytes, not a null-terminated string, so it is zeroed
            // by its own length instead of by a string helper that scans for a terminator.
            for (var index = 0; index < secret.Length; index++)
            {
                Marshal.WriteByte(secretPointer, index, 0);
            }

            Marshal.FreeHGlobal(secretPointer);
            Array.Clear(secret);
        }
    }

    public SpotifyCredentials? Load()
    {
        if (!CredReadW(_target, CRED_TYPE_GENERIC, 0, out var handle))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(handle);
            var clientId = credential.UserName ?? string.Empty;
            var secret = credential.CredentialBlobSize > 0
                ? Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2)
                : null;
            return string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secret)
                ? null
                : new SpotifyCredentials(clientId, secret);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public bool Delete()
    {
        if (CredDeleteW(_target, CRED_TYPE_GENERIC, 0))
        {
            return true;
        }

        // Nothing stored is a successful outcome for the caller.
        return Marshal.GetLastWin32Error() == ERROR_NOT_FOUND;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW([In] ref CREDENTIAL credential, [In] uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out nint credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree([In] nint buffer);
}
