using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Paneless.Core;

/// <summary>Keeps unlock codes in the operating system's own protected storage.</summary>
public interface ISecretVault
{
    /// <summary>Shown in the auto-unlock dialog, e.g. "Windows data protection (DPAPI)".</summary>
    string Description { get; }
    bool IsAvailable { get; }
    /// <summary>Stores the code and returns what to keep in unlock-codes.json.</summary>
    Task<string> ProtectAsync(string sn, string code);
    Task<string?> RevealAsync(string sn, string token);
    Task ForgetAsync(string sn, string token);
}

public static class SecretVault
{
    public static ISecretVault ForThisOs() =>
        OperatingSystem.IsWindows() ? new WindowsDpapiVault() :
        OperatingSystem.IsMacOS() ? new MacKeychainVault() :
        new LinuxSecretServiceVault();
}

/// <summary>Windows DPAPI, readable only by this Windows account on this PC.
/// Stored in the same hex format as PowerShell's ConvertFrom-SecureString, so codes can also be checked from a script.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiVault : ISecretVault
{
    public string Description => Text.T("Windows data protection (DPAPI), readable only by your Windows account on this PC");
    public bool IsAvailable => true;

    public Task<string> ProtectAsync(string sn, string code)
    {
        var blob = ProtectedData.Protect(Encoding.Unicode.GetBytes(code), null, DataProtectionScope.CurrentUser);
        return Task.FromResult(Convert.ToHexString(blob).ToLowerInvariant());
    }

    public Task<string?> RevealAsync(string sn, string token)
    {
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromHexString(token), null, DataProtectionScope.CurrentUser);
            try { return Task.FromResult<string?>(Encoding.Unicode.GetString(plain)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch { return Task.FromResult<string?>(null); }
    }

    public Task ForgetAsync(string sn, string token) => Task.CompletedTask;
}

/// <summary>The macOS login keychain (service "Paneless", account = device serial).</summary>
public sealed class MacKeychainVault : ISecretVault
{
    const string Security = "/System/Library/Frameworks/Security.framework/Security";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const int ErrSecItemNotFound = -25300;
    static readonly byte[] Service = Encoding.UTF8.GetBytes("Paneless");

    [DllImport(Security)] static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLen, byte[] service, uint accountLen, byte[] account, uint passwordLen, byte[] password, IntPtr itemRef);
    [DllImport(Security)] static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLen, byte[] service, uint accountLen, byte[] account, out uint passwordLen, out IntPtr password, out IntPtr itemRef);
    [DllImport(Security)] static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);
    [DllImport(Security)] static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);
    [DllImport(Security)] static extern int SecKeychainItemDelete(IntPtr itemRef);
    [DllImport(CoreFoundation)] static extern void CFRelease(IntPtr cf);

    public string Description => Text.T("the macOS keychain");
    public bool IsAvailable => OperatingSystem.IsMacOS();

    public Task<string> ProtectAsync(string sn, string code)
    {
        var account = Encoding.UTF8.GetBytes(sn);
        var pw = Encoding.UTF8.GetBytes(code);
        try
        {
            var rc = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, out _, out var data, out var item);
            if (rc == 0)
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                rc = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)pw.Length, pw);
                CFRelease(item);
            }
            else if (rc == ErrSecItemNotFound)
            {
                rc = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, (uint)pw.Length, pw, IntPtr.Zero);
            }
            if (rc != 0) throw new InvalidOperationException(Text.F("The keychain refused to save the code (error {0}).", rc));
            return Task.FromResult("keychain");
        }
        finally { CryptographicOperations.ZeroMemory(pw); }
    }

    public Task<string?> RevealAsync(string sn, string token)
    {
        var account = Encoding.UTF8.GetBytes(sn);
        var rc = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, out var len, out var data, out var item);
        if (rc != 0) return Task.FromResult<string?>(null);
        try
        {
            var bytes = new byte[len];
            Marshal.Copy(data, bytes, 0, (int)len);
            try { return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { SecKeychainItemFreeContent(IntPtr.Zero, data); CFRelease(item); }
    }

    public Task ForgetAsync(string sn, string token)
    {
        var account = Encoding.UTF8.GetBytes(sn);
        if (SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account, out _, out var data, out var item) == 0)
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            SecKeychainItemDelete(item);
            CFRelease(item);
        }
        return Task.CompletedTask;
    }
}

/// <summary>The Linux Secret Service (GNOME Keyring / KWallet) through the secret-tool command.
/// The code goes in through stdin, never on a command line.</summary>
public sealed class LinuxSecretServiceVault : ISecretVault
{
    readonly string? _tool = new[] { "/usr/bin/secret-tool", "/usr/local/bin/secret-tool", "/bin/secret-tool" }.FirstOrDefault(File.Exists);

    public string Description => Text.T("your desktop's keyring (Secret Service)");
    public bool IsAvailable => _tool != null;

    public async Task<string> ProtectAsync(string sn, string code)
    {
        if (_tool == null) throw new InvalidOperationException(Text.T("secret-tool isn't installed (package libsecret-tools)."));
        var r = await ProcessRunner.RunAsync(_tool, ["store", "--label=Paneless unlock code", "service", "paneless", "account", sn],
            TimeSpan.FromSeconds(30), stdin: code);
        if (!r.Ok) throw new InvalidOperationException(Text.T("The keyring refused to save the code: ") + r.LastLine);
        return "secret-service";
    }

    public async Task<string?> RevealAsync(string sn, string token)
    {
        if (_tool == null) return null;
        var r = await ProcessRunner.RunAsync(_tool, ["lookup", "service", "paneless", "account", sn], TimeSpan.FromSeconds(30));
        return r.Ok && r.Output.Length > 0 ? r.Output : null;
    }

    public async Task ForgetAsync(string sn, string token)
    {
        if (_tool != null) await ProcessRunner.RunAsync(_tool, ["clear", "service", "paneless", "account", sn], TimeSpan.FromSeconds(30));
    }
}
