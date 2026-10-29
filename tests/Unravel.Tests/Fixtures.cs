using Microsoft.Win32;
using Unravel.Core.Util;

namespace Unravel.Tests;

/// <summary>An isolated registry area under HKCU\Software\UnravelTests\{guid}, removed on dispose.</summary>
public sealed class RegistrySandbox : IDisposable
{
    public RegPath Root { get; }

    public RegistrySandbox()
    {
        Root = new RegPath(RegistryHive.CurrentUser, @"Software\UnravelTests\" + Guid.NewGuid().ToString("N")[..12]);
        using var k = Registry.CurrentUser.CreateSubKey(Root.SubPath);
    }

    public RegPath Path(string relative) => Root.Child(relative);

    public RegPath CreateKey(string relative, params (string Name, object Value)[] values)
    {
        var p = Root.Child(relative);
        using var k = Registry.CurrentUser.CreateSubKey(p.SubPath);
        foreach (var (n, v) in values)
        {
            if (v is RegistryValueKind) continue;
            k.SetValue(n, v);
        }
        return p;
    }

    public void SetValue(string relative, string name, object value, RegistryValueKind kind = RegistryValueKind.Unknown)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Root.Child(relative).SubPath);
        if (kind == RegistryValueKind.Unknown) k.SetValue(name, value); else k.SetValue(name, value, kind);
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(Root.SubPath, false); } catch { }
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\UnravelTests", true);
            if (parent is not null && parent.SubKeyCount == 0) Registry.CurrentUser.DeleteSubKey(@"Software\UnravelTests", false);
        }
        catch { }
    }
}

/// <summary>A temporary folder tree, removed on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Root { get; }

    public TempDir()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unravel-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    public string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public string Dir(params string[] parts)
    {
        var p = Path(parts);
        Directory.CreateDirectory(p);
        return p;
    }

    public string File(string relative, string content = "data", DateTime? time = null)
    {
        var p = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        if (time is { } t)
        {
            System.IO.File.SetLastWriteTime(p, t);
            System.IO.File.SetLastAccessTime(p, t);
            System.IO.File.SetCreationTime(p, t);
        }
        return p;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch { }
    }
}
