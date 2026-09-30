namespace SekisanProAI.Models;

public sealed class LocalReferenceStatus
{
    public int RuleFiles { get; set; }
    public int RuleEntries { get; set; }
    public int GrzFiles { get; set; }
    public string LastImportedAt { get; set; } = "";
    public string SourceFile { get; set; } = "";
}