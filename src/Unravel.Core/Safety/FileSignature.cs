using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Unravel.Core.Safety;

public sealed record SignatureInfo(bool IsSigned, bool IsTrusted, string? Signer)
{
    public bool IsMicrosoft => IsTrusted && Signer is not null && Signer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
    public static readonly SignatureInfo Unsigned = new(false, false, null);
}

/// <summary>Authenticode checks with WinVerifyTrust (embedded signatures and the Windows catalog).</summary>
public static class FileSignature
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, SignatureInfo Info)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static SignatureInfo Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return SignatureInfo.Unsigned;
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); } catch { return SignatureInfo.Unsigned; }
        if (Cache.TryGetValue(path, out var c) && c.Stamp == stamp) return c.Info;
        var info = Compute(path);
        Cache[path] = (stamp, info);
        return info;
    }

    private static SignatureInfo Compute(string path)
    {
        string? signer = null;
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile reads the embedded signer without network access
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            signer = cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch { /* no embedded signature – it may still be catalog-signed */ }
        var trusted = VerifyEmbedded(path);
        if (!trusted && signer is null && IsCatalogSigned(path))
            return new SignatureInfo(true, true, "Microsoft Windows (catalog)");
        return signer is null && !trusted ? SignatureInfo.Unsigned : new SignatureInfo(true, trusted, signer);
    }

    // ---- WinVerifyTrust (embedded signature) ----
    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    private static bool VerifyEmbedded(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,              // WTD_UI_NONE
                fdwRevocationChecks = 0,     // WTD_REVOKE_NONE (no network)
                dwUnionChoice = 1,           // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 1,           // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x00001000,    // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            int rc = WinVerifyTrust(IntPtr.Zero, ActionGenericVerifyV2, ref data);
            data.dwStateAction = 2;          // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ActionGenericVerifyV2, ref data);
            return rc == 0;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    // ---- Catalog lookup (most Windows system files are signed through catalogs) ----
    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin, IntPtr pgSubsystem, [MarshalAs(UnmanagedType.LPWStr)] string? pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);
    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, IntPtr hFile, ref uint pcbHash, byte[]? pbHash, uint dwFlags);
    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, uint cbHash, uint dwFlags, IntPtr phPrevCatInfo);
    [DllImport("wintrust.dll")]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);
    [DllImport("wintrust.dll")]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    private static bool IsCatalogSigned(string path)
    {
        if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var h = fs.SafeFileHandle.DangerousGetHandle();
            uint size = 0;
            CryptCATAdminCalcHashFromFileHandle2(admin, h, ref size, null, 0);
            if (size == 0) return false;
            var hash = new byte[size];
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, h, ref size, hash, 0)) return false;
            var cat = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, IntPtr.Zero);
            if (cat == IntPtr.Zero) return false;
            CryptCATAdminReleaseCatalogContext(admin, cat, 0);
            return true;
        }
        catch { return false; }
        finally { CryptCATAdminReleaseContext(admin, 0); }
    }
}
