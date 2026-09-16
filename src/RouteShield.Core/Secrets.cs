using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RouteShield;

/// <summary>
/// Seals profile bodies and subscription URLs with the current user's DPAPI key, so a copied
/// settings.json is useless on another account or machine.
/// </summary>
public static class Secrets
{
    private const string Prefix = "dpapi:";

    public static string Protect(string text)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (!IsSupported)
        {
            throw Unsupported();
        }

        return Prefix + Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(text)));
    }

    public static string Unprotect(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (!IsSupported)
        {
            throw Unsupported();
        }

        var payload = text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? text[Prefix.Length..] : text;
        return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(payload)));
    }

    [SupportedOSPlatformGuard("windows")]
    private static bool IsSupported => OperatingSystem.IsWindows();

    private static PlatformNotSupportedException Unsupported() =>
        new("RouteShield seals secrets with Windows DPAPI.");
}

[SupportedOSPlatform("windows")]
internal static class Dpapi
{
    private const uint PromptForbidden = 1;

    public static byte[] Protect(byte[] plaintext)
    {
        var input = Allocate(plaintext);
        try
        {
            if (!CryptProtectData(ref input, "RouteShield", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, PromptForbidden, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Consume(output);
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }

    public static byte[] Unprotect(byte[] ciphertext)
    {
        var input = Allocate(ciphertext);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, PromptForbidden, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Consume(output);
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }

    private static Blob Allocate(byte[] bytes)
    {
        var blob = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }

    private static byte[] Consume(Blob blob)
    {
        try
        {
            var bytes = new byte[blob.Length];
            Marshal.Copy(blob.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            LocalFree(blob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
