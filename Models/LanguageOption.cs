namespace BDOLootTracker.Models;

public sealed class LanguageOption
{
    public string Code { get; init; } = "us";
    public string Name { get; init; } = "English";

    public override string ToString() => Name;

    public static IReadOnlyList<LanguageOption> All { get; } = new List<LanguageOption>
    {
        new() { Code = "us", Name = "English" },
        new() { Code = "de", Name = "German" },
        new() { Code = "fr", Name = "French" },
        new() { Code = "es", Name = "Spanish (EU)" },
        new() { Code = "pt", Name = "Portuguese" },
        new() { Code = "sp", Name = "Portuguese (RedFox)" },
        new() { Code = "ru", Name = "Russian" },
        new() { Code = "tr", Name = "Turkish" },
        new() { Code = "kr", Name = "Korean" },
        new() { Code = "jp", Name = "Japanese" },
        new() { Code = "th", Name = "Thai" },
        new() { Code = "tw", Name = "Traditional Chinese" },
        new() { Code = "cn", Name = "Simplified Chinese" },
        new() { Code = "id", Name = "Indonesian" }
    };
}
