namespace BDOLootTracker.Models;

public sealed class SessionSummary
{
    public long SessionId { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime EffectiveEndUtc { get; init; }
    public bool IsCompleted { get; init; }
    public string Region { get; init; } = string.Empty;
    public string CharacterName { get; init; } = string.Empty;
    public int? ClassType { get; init; }
    public string ClassName { get; init; } = string.Empty;
    public string Spec { get; init; } = string.Empty;
    public string SpotKey { get; init; } = string.Empty;
    public string SpotName { get; init; } = string.Empty;
    public decimal TotalSilver { get; init; }
    public ulong TotalTrash { get; init; }
    public DateTime? GarmothUploadedAtUtc { get; init; }
    public int GarmothUploadCount { get; init; }
    public int? DropRatePercent { get; init; }

    public TimeSpan Duration => EffectiveEndUtc > StartedAtUtc
        ? EffectiveEndUtc - StartedAtUtc
        : TimeSpan.Zero;

    public decimal SilverPerHour => Duration.TotalHours > 0.0001
        ? TotalSilver / (decimal)Duration.TotalHours
        : 0;

    public decimal TrashPerHour => Duration.TotalHours > 0.0001
        ? TotalTrash / (decimal)Duration.TotalHours
        : 0;

    public string DateText => StartedAtUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm");
    public string DurationText => $"{(int)Duration.TotalHours:00}:{Duration.Minutes:00}:{Duration.Seconds:00}";
    public string TotalSilverText => $"{TotalSilver:N0}";
    public string SilverPerHourText => $"{SilverPerHour:N0}";
    public string TrashPerHourText => TotalTrash == 0 ? "—" : $"{TrashPerHour:N0}";
    public string StatusText => IsCompleted ? "Completed" : "Interrupted / active";
    public bool IsUploadedToGarmoth => GarmothUploadCount > 0 || GarmothUploadedAtUtc != null;
    public string GarmothUploadStatusText => IsUploadedToGarmoth
        ? $"Uploaded to Garmoth {Math.Max(1, GarmothUploadCount)}x"
        : "Not uploaded to Garmoth";
    public string GarmothUploadedAtText => GarmothUploadedAtUtc == null
        ? string.Empty
        : GarmothUploadedAtUtc.Value.ToLocalTime().ToString("yyyy.MM.dd HH:mm");
    public string CharacterText => string.IsNullOrWhiteSpace(CharacterName) ? "—" : CharacterName;
    public string ClassSpecText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ClassName))
                return "Class: —";

            return string.IsNullOrWhiteSpace(Spec)
                ? ClassName
                : $"{ClassName} • {Spec}";
        }
    }
}
