using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BDOLootTracker.Models;

public sealed class CharacterClassOption : INotifyPropertyChanged
{
    private string? _iconPath;

    public int ClassType { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? IconUrl { get; init; }
    public int SortOrder { get; init; }
    public IReadOnlyList<string> Specs { get; init; } = Array.Empty<string>();

    public string? IconPath
    {
        get => _iconPath;
        set
        {
            if (string.Equals(_iconPath, value, StringComparison.OrdinalIgnoreCase))
                return;

            _iconPath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPath)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIcon)));
        }
    }

    public bool HasIcon => !string.IsNullOrWhiteSpace(IconPath);

    public string Initials
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
                return "?";

            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2
                ? $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant()
                : Name[..Math.Min(2, Name.Length)].ToUpperInvariant();
        }
    }

    public static CharacterClassOption None { get; } = new()
    {
        ClassType = -1,
        Name = "Not set",
        SortOrder = -1,
        Specs = Array.Empty<string>()
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}
