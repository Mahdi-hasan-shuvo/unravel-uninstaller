using System.Text.Json;
using System.Text.Json.Serialization;
using Unravel.Core.Leftovers;

namespace Unravel.Core.Settings;

public enum ThemeMode { System, Light, Dark }
public enum ProgramViewMode { Details, Tiles, List }

public sealed class ExcludeRule
{
    public string Pattern { get; set; } = "";
    public bool IncludeSubkeys { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

public sealed class PatternRule
{
    public string Pattern { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class UninstallerSettings
{
    public bool ShowSystemComponents { get; set; }
    public bool ShowUpdates { get; set; }
    public bool UseInstallDateValue { get; set; }
    public bool FastLoading { get; set; }
    public bool OnlyRunBuiltInUninstaller { get; set; }
    public bool CreateRestorePoint { get; set; } = true;
    public bool BackupRegistryBeforeDelete { get; set; } = true;
    public bool OpenWizardMaximized { get; set; }
    public bool CheckAllLeftoversByDefault { get; set; }
    public bool StopRunningExecutables { get; set; } = true;
    public bool DeleteFilesToRecycleBin { get; set; } = true;
    public ScanMode DefaultScanMode { get; set; } = ScanMode.Moderate;
    public List<ExcludeRule> RegistryExcludes { get; set; } = DefaultRegistryExcludes();
    public List<ExcludeRule> FolderExcludes { get; set; } = [];

    public static List<ExcludeRule> DefaultRegistryExcludes() =>
    [
        new() { Pattern = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\*", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_LOCAL_MACHINE\SOFTWARE\Clients", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_LOCAL_MACHINE\SOFTWARE\RegisteredApplications", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_CURRENT_USER\Software\Microsoft", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_CURRENT_USER\Software\Classes", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_CURRENT_USER\Software\Policies", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_CLASSES_ROOT\CLSID", IncludeSubkeys = false },
        new() { Pattern = @"HKEY_CLASSES_ROOT\Installer", IncludeSubkeys = false },
    ];
}

public sealed class JunkSettings
{
    public bool DeleteToRecycleBin { get; set; } = true;
    public bool IgnoreRecentlyAccessed { get; set; } = true;
    public List<string> Drives { get; set; } = [];
    public List<PatternRule> Patterns { get; set; } = DefaultPatterns();
    public List<PatternRule> Excludes { get; set; } = DefaultExcludes();

    public static List<PatternRule> DefaultPatterns() =>
    [
        new() { Pattern = "*.tmp", Description = "Temporary file" },
        new() { Pattern = "*.temp", Description = "Temporary file" },
        new() { Pattern = "*._mp", Description = "Temporary file" },
        new() { Pattern = "*.$$$", Description = "Temporary file" },
        new() { Pattern = "*.---", Description = "Temporary file" },
        new() { Pattern = "~*.*", Description = "Temporary file left by an editor" },
        new() { Pattern = "*.~*", Description = "Temporary backup file" },
        new() { Pattern = "*.gid", Description = "Windows Help index" },
        new() { Pattern = "*.chk", Description = "Check Disk file fragment" },
        new() { Pattern = "*.dmp", Description = "Memory dump" },
        new() { Pattern = "*.err", Description = "Error log" },
        new() { Pattern = "thumbs.db", Description = "Folder thumbnail cache" },
        new() { Pattern = "*.old", Description = "Old version of a file", Enabled = false },
        new() { Pattern = "*.bak", Description = "Backup file", Enabled = false },
        new() { Pattern = "*.log", Description = "Log file", Enabled = false },
    ];

    public static List<PatternRule> DefaultExcludes() =>
    [
        new() { Pattern = @"?:\Windows\*", Description = "Windows folder" },
        new() { Pattern = @"?:\Program Files\*", Description = "Installed programs" },
        new() { Pattern = @"?:\Program Files (x86)\*", Description = "Installed programs" },
        new() { Pattern = @"?:\ProgramData\Microsoft\*", Description = "Windows data" },
        new() { Pattern = @"?:\$Recycle.Bin\*", Description = "Recycle Bin" },
        new() { Pattern = @"?:\System Volume Information\*", Description = "System restore data" },
        new() { Pattern = @"*\AppData\Local\Packages\*", Description = "Windows app data" },
        new() { Pattern = @"*\AppData\Local\Microsoft\WindowsApps\*", Description = "Windows app aliases" },
        new() { Pattern = @"*\node_modules\*", Description = "Developer packages" },
        new() { Pattern = @"*\.git\*", Description = "Source control data" },
    ];
}

public sealed class HunterSettings
{
    public bool AlwaysOnTop { get; set; } = true;
    public int Size { get; set; } = 1; // 0 small, 1 medium, 2 large
    public int TransparencyPercent { get; set; } = 50;
    public bool StartWithWindows { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
}

public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public bool SkipInformationMessages { get; set; }
    public bool SkipWarningMessages { get; set; }
    public bool CompactRows { get; set; }
    public bool ShowDetailsPane { get; set; } = true;
    public ProgramViewMode ProgramView { get; set; } = ProgramViewMode.Details;
    public List<string> HiddenProgramColumns { get; set; } = ["Comment", "UninstallString", "RegistryKey", "Website", "InstallLocation", "Installer"];
    public UninstallerSettings Uninstaller { get; set; } = new();
    public JunkSettings Junk { get; set; } = new();
    public HunterSettings Hunter { get; set; } = new();
    public Dictionary<string, int> DisabledServiceStartTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Loads and saves settings as JSON under %LOCALAPPDATA%\Unravel (or a supplied folder for tests).</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Folder { get; }
    public string FilePath => Path.Combine(Folder, "settings.json");
    public AppSettings Current { get; private set; } = new();
    public string? LoadError { get; private set; }

    public SettingsStore(string? folder = null)
    {
        Folder = folder ?? DefaultFolder;
    }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unravel");

    public AppSettings Load()
    {
        LoadError = null;
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
            else Current = new AppSettings();
        }
        catch (Exception e)
        {
            // Corrupt settings must never prevent startup: keep a copy and fall back to defaults.
            LoadError = e.Message;
            try { File.Copy(FilePath, FilePath + ".corrupt", overwrite: true); } catch { }
            Current = new AppSettings();
        }
        return Current;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
