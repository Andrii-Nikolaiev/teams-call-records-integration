namespace TeamsCallRecords.Infrastructure.Config;

/// <summary>Section "Storage" (TRP 3.18.2).</summary>
public sealed class StorageOptions
{
    public string DataDirectory { get; set; } = "./data";
    public string DatabaseFileName { get; set; } = "tcri.db";

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory)) yield return "Storage:DataDirectory must not be empty";
        if (string.IsNullOrWhiteSpace(DatabaseFileName)) yield return "Storage:DatabaseFileName must not be empty";
        else if (DatabaseFileName.Contains('/') || DatabaseFileName.Contains('\\'))
            yield return "Storage:DatabaseFileName must not contain path separators";
    }
}
