using System.Text;

namespace Synestra.Worker;

internal sealed class WorkerOptions
{
    public const string SupportedType = "test.bounded-sum.v1";
    public string ApiBaseAddress { get; set; } = "";
    public string StateDirectory { get; set; } = "";
    public string Name { get; set; } = "synestra-worker";

    public static bool IsValid(WorkerOptions options)
    {
        if (!Uri.TryCreate(options.ApiBaseAddress, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(options.StateDirectory) || !Path.IsPathFullyQualified(options.StateDirectory)
            || string.IsNullOrWhiteSpace(options.Name) || options.Name.Length > 200 || options.Name.Contains('\0'))
            return false;
        try
        {
            _ = new UTF8Encoding(false, true).GetByteCount(options.Name);
            _ = Path.GetFullPath(options.StateDirectory);
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}
