using Microsoft.Win32;
using Unravel.Core.Native;

namespace Unravel.Core.Util;

/// <summary>A fully qualified registry location, e.g. HKEY_LOCAL_MACHINE\SOFTWARE\Foo, with an explicit 32/64-bit view.</summary>
public sealed record RegPath(RegistryHive Hive, string SubPath, RegistryView View = RegistryView.Registry64)
{
    public string HiveName => Hive switch
    {
        RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.Users => "HKEY_USERS",
        RegistryHive.CurrentConfig => "HKEY_CURRENT_CONFIG",
        _ => Hive.ToString()
    };

    /// <summary>Path as shown to the user and accepted by regedit. 32-bit HKLM software paths are rendered with WOW6432Node.</summary>
    public string FullPath
    {
        get
        {
            var sub = SubPath.Trim('\\');
            if (View == RegistryView.Registry32 && Hive == RegistryHive.LocalMachine &&
                (sub.Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase) || sub.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase)) &&
                !sub.StartsWith("SOFTWARE\\WOW6432Node", StringComparison.OrdinalIgnoreCase))
                sub = sub.Length == 8 ? "SOFTWARE\\WOW6432Node" : "SOFTWARE\\WOW6432Node\\" + sub.Substring("SOFTWARE\\".Length);
            return sub.Length == 0 ? HiveName : HiveName + "\\" + sub;
        }
    }

    public string Name => SubPath.TrimEnd('\\').Split('\\').Last();

    public RegPath Child(string name) => this with { SubPath = SubPath.Length == 0 ? name : SubPath.TrimEnd('\\') + "\\" + name };

    public RegPath? Parent
    {
        get
        {
            var trimmed = SubPath.TrimEnd('\\');
            if (trimmed.Length == 0) return null;
            var i = trimmed.LastIndexOf('\\');
            return this with { SubPath = i < 0 ? "" : trimmed.Substring(0, i) };
        }
    }

    /// <summary>Number of path segments below the hive.</summary>
    public int Depth => SubPath.Trim('\\').Length == 0 ? 0 : SubPath.Trim('\\').Split('\\').Length;

    public override string ToString() => FullPath;

    /// <summary>Same key regardless of how the view was expressed (Registry32 view vs explicit WOW6432Node).</summary>
    public bool SameKeyAs(RegPath other) => string.Equals(FullPath, other.FullPath, StringComparison.OrdinalIgnoreCase);

    public bool IsUnder(RegPath ancestor) =>
        FullPath.StartsWith(ancestor.FullPath + "\\", StringComparison.OrdinalIgnoreCase);

    public static RegPath Parse(string full)
    {
        if (!TryParse(full, out var p)) throw new FormatException($"Not a registry path: {full}");
        return p!;
    }

    public static bool TryParse(string full, out RegPath? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(full)) return false;
        var s = full.Trim().TrimEnd('\\');
        if (s.StartsWith("Computer\\", StringComparison.OrdinalIgnoreCase)) s = s.Substring(9);
        var idx = s.IndexOf('\\');
        var hiveText = idx < 0 ? s : s.Substring(0, idx);
        var rest = idx < 0 ? "" : s.Substring(idx + 1);
        RegistryHive hive;
        switch (hiveText.ToUpperInvariant())
        {
            case "HKEY_CLASSES_ROOT": case "HKCR": hive = RegistryHive.ClassesRoot; break;
            case "HKEY_CURRENT_USER": case "HKCU": hive = RegistryHive.CurrentUser; break;
            case "HKEY_LOCAL_MACHINE": case "HKLM": hive = RegistryHive.LocalMachine; break;
            case "HKEY_USERS": case "HKU": hive = RegistryHive.Users; break;
            case "HKEY_CURRENT_CONFIG": case "HKCC": hive = RegistryHive.CurrentConfig; break;
            default: return false;
        }
        path = new RegPath(hive, rest);
        return true;
    }

    public RegistryKey? Open(bool writable = false)
    {
        try
        {
            var root = RegistryKey.OpenBaseKey(Hive, View);
            if (SubPath.Trim('\\').Length == 0) return root;
            var k = root.OpenSubKey(SubPath.Trim('\\'), writable);
            if (k is null) root.Dispose();
            return k;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public bool Exists()
    {
        using var k = Open();
        return k is not null;
    }

    public DateTime? LastWriteTime()
    {
        using var k = Open();
        return k is null ? null : RegistryUtil.GetLastWriteTime(k);
    }
}

public static class RegistryUtil
{
    public static DateTime? GetLastWriteTime(RegistryKey key)
    {
        try
        {
            int rc = NativeMethods.RegQueryInfoKey(key.Handle, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out long ft);
            return rc == 0 ? DateTime.FromFileTime(ft) : null;
        }
        catch { return null; }
    }

    public static string? GetString(RegistryKey key, string name)
    {
        try
        {
            var v = key.GetValue(name, null);
            return v switch
            {
                null => null,
                string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
                string[] a => string.Join(" ", a),
                _ => v.ToString()
            };
        }
        catch { return null; }
    }

    public static int? GetInt(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out var p) => p,
                byte[] b when b.Length >= 4 => BitConverter.ToInt32(b, 0),
                _ => null
            };
        }
        catch { return null; }
    }

    public static string FormatValue(object? value, RegistryValueKind kind) => value switch
    {
        null => "",
        byte[] b => BitConverter.ToString(b.Take(48).ToArray()).Replace('-', ' ') + (b.Length > 48 ? " …" : ""),
        string[] a => string.Join(" | ", a),
        int i when kind == RegistryValueKind.DWord => $"0x{i:x8} ({(uint)i})",
        long l => $"0x{l:x16} ({(ulong)l})",
        _ => value.ToString() ?? ""
    };

    /// <summary>Deletes a key tree. Returns true if the key no longer exists afterwards.</summary>
    public static bool DeleteKeyTree(RegPath path, out string? error)
    {
        error = null;
        var parent = path.Parent;
        if (parent is null || path.Depth == 0) { error = "Refusing to delete a registry hive."; return false; }
        try
        {
            using var pk = parent.Open(writable: true);
            if (pk is null)
            {
                if (!path.Exists()) return true;
                error = "Access denied (administrator rights may be required).";
                return false;
            }
            pk.DeleteSubKeyTree(path.Name, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = "Access denied (administrator rights may be required).";
            return false;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    public static bool DeleteValue(RegPath key, string valueName, out string? error)
    {
        error = null;
        try
        {
            using var k = key.Open(writable: true);
            if (k is null)
            {
                if (!key.Exists()) return true;
                error = "Access denied (administrator rights may be required).";
                return false;
            }
            k.DeleteValue(valueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = "Access denied (administrator rights may be required).";
            return false;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    /// <summary>Deletes every value of a key (keeps the key and subkeys). Returns number deleted.</summary>
    public static int ClearValues(RegPath key, Func<string, bool>? filter = null)
    {
        using var k = key.Open(writable: true);
        if (k is null) return 0;
        int n = 0;
        foreach (var name in k.GetValueNames())
        {
            if (filter is not null && !filter(name)) continue;
            try { k.DeleteValue(name, false); n++; } catch { }
        }
        return n;
    }
}
