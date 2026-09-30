namespace SekisanProAI.Models;

public sealed class AiCheckResult
{
    public string Provider { get; set; } = "";
    public bool Success { get; set; }
    public string Result { get; set; } = "";
    public string Error { get; set; } = "";
}