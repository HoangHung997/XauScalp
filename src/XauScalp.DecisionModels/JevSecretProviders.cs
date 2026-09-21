using System.ComponentModel;
using System.Runtime.InteropServices;

namespace XauScalp.DecisionModels;

public sealed class WindowsCredentialManagerJevSecretProvider : IJevSecretProvider
{
    public ValueTask<JevSecret> GetSecretAsync(
        string secretReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(secretReference))
        {
            throw new ArgumentException(
                "Credential Manager target name is required.",
                nameof(secretReference));
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new JevSecretUnavailableException(
                "Windows Credential Manager is only available on Windows.");
        }

        if (!CredRead(
            secretReference,
            CredentialTypeGeneric,
            0,
            out IntPtr credentialPointer))
        {
            int error = Marshal.GetLastWin32Error();
            throw new JevSecretUnavailableException(
                $"Windows Credential Manager could not read target '{secretReference}'.",
                new Win32Exception(error));
        }

        try
        {
            Credential credential = Marshal.PtrToStructure<Credential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                throw new JevSecretUnavailableException(
                    $"Windows Credential Manager target '{secretReference}' contains no secret.");
            }

            string? secret = Marshal.PtrToStringUni(
                credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / sizeof(char)));

            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new JevSecretUnavailableException(
                    $"Windows Credential Manager target '{secretReference}' contains an empty secret.");
            }

            return ValueTask.FromResult(new JevSecret(secret.TrimEnd('\0')));
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private const uint CredentialTypeGeneric = 1;

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credentialPointer);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credentialPointer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
