using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unravel.Core.Safety;
using Unravel.Core.Util;

namespace Unravel.Core.Leftovers;

public enum ScanMode { BuiltInOnly, Safe, Moderate, Advanced }

public enum LeftoverKind { Container, RegistryKey, RegistryValue, Folder, File }

/// <summary>
/// One node in a leftover result tree. Only nodes with <see cref="IsCreated"/> (shown bold) are ever deleted;
/// other nodes exist to show context and to make "check the whole branch" selection easy.
/// </summary>
public sealed class LeftoverNode : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _isCreated;
    private bool _isExcluded;
    private bool _isExpanded = true;
    private string? _note;
    private SafetyAssessment _safety = SafetyAssessment.Unknown;

    public LeftoverNode(LeftoverKind kind, string displayName)
    {
        Kind = kind;
        DisplayName = displayName;
    }

    public LeftoverKind Kind { get; }
    public string DisplayName { get; }
    public RegPath? Key { get; init; }
    public string? ValueName { get; init; }
    public string? ValueData { get; init; }
    public string? FsPath { get; init; }
    public long Size { get; set; }
    public string? Reason { get; set; }
    public LeftoverNode? Parent { get; private set; }
    public ObservableCollection<LeftoverNode> Children { get; } = new();

    /// <summary>Full location used for display, export and exclusion matching.</summary>
    public string Location => Kind switch
    {
        LeftoverKind.RegistryKey => Key!.FullPath,
        LeftoverKind.RegistryValue => Key!.FullPath + "\\" + (string.IsNullOrEmpty(ValueName) ? "(Default)" : ValueName),
        LeftoverKind.Folder or LeftoverKind.File => FsPath!,
        _ => DisplayName
    };

    public bool IsCreated { get => _isCreated; set => Set(ref _isCreated, value); }
    public bool IsExcluded { get => _isExcluded; set => Set(ref _isExcluded, value); }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public string? Note { get => _note; set => Set(ref _note, value); }
    public FindingCategory Category { get; set; }

    /// <summary>Confidence and risks from the safety engine. Blocked (system-critical) nodes can never be checked.</summary>
    public SafetyAssessment Safety
    {
        get => _safety;
        set
        {
            Set(ref _safety, value);
            OnPropertyChanged(nameof(ConfidenceText));
            if (value.IsBlocked && _isChecked) { _isChecked = false; OnPropertyChanged(nameof(IsChecked)); }
        }
    }

    public string ConfidenceText => Kind is LeftoverKind.Container || !IsCreated && Safety == SafetyAssessment.Unknown ? "" : Safety.ConfidenceText;

    /// <summary>Checking a node checks its whole branch, like the original's tree.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            if (value && _safety.IsBlocked) { OnPropertyChanged(); return; } // refuse, and tell the UI to revert
            _isChecked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WillDelete));
            foreach (var c in Children) c.IsChecked = value;
        }
    }

    /// <summary>Display state for styling: "excluded", "created" or "context".</summary>
    public string State => IsExcluded ? "excluded" : IsCreated ? "created" : "context";

    /// <summary>Will this node actually be removed if Delete is pressed now?</summary>
    public bool WillDelete => IsChecked && IsCreated && !IsExcluded && !Safety.IsBlocked;

    public LeftoverNode Add(LeftoverNode child)
    {
        child.Parent = this;
        Children.Add(child);
        return child;
    }

    public IEnumerable<LeftoverNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    public IEnumerable<LeftoverNode> SelfAndDescendants() => new[] { this }.Concat(Descendants());

    public bool IsDescendantOf(LeftoverNode other)
    {
        for (var p = Parent; p is not null; p = p.Parent) if (ReferenceEquals(p, other)) return true;
        return false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(n);
        if (n is nameof(IsCreated) or nameof(IsExcluded)) { OnPropertyChanged(nameof(WillDelete)); OnPropertyChanged(nameof(State)); }
    }

    public override string ToString() => Location;
}

/// <summary>A single raw finding before it is placed into a tree.</summary>
public sealed record Finding(LeftoverKind Kind, string Reason, bool Created, RegPath? Key = null, string? ValueName = null, string? ValueData = null, string? FsPath = null)
{
    public string Location => Kind switch
    {
        LeftoverKind.RegistryKey => Key!.FullPath,
        LeftoverKind.RegistryValue => Key!.FullPath + "\\" + (string.IsNullOrEmpty(ValueName) ? "(Default)" : ValueName),
        _ => FsPath ?? ""
    };
}

public sealed class ScanResult
{
    public required LeftoverNode RegistryRoot { get; init; }
    public required ObservableCollection<LeftoverNode> Files { get; init; }
    public List<Finding> RawFindings { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public TimeSpan Duration { get; set; }

    public IEnumerable<LeftoverNode> RegistryLeftovers => RegistryRoot.Descendants().Where(n => n.IsCreated);
    public int RegistryCount => RegistryLeftovers.Count();
    public int FileCount => Files.Count(f => f.IsCreated);
    public long FilesSize => Files.Where(f => f.IsCreated).Sum(f => f.Size);
    public bool IsEmpty => RegistryCount == 0 && FileCount == 0;
}

