namespace Agentic.AgentCore.Data;

public static class RepoPaths
{
    /// <summary>
    /// Walks up from the running binary to find the repo root (marked by the
    /// solution file) and returns its data/ directory. Override with the
    /// AGENT_DATA_DIR environment variable.
    /// </summary>
    public static string FindDataDir()
    {
        var overrideDir = Environment.GetEnvironmentVariable("AGENT_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            Directory.CreateDirectory(overrideDir);
            return overrideDir;
        }

        return Path.Combine(FindRepoRoot(), "data");
    }

    /// <summary>Nearest ancestor directory containing the solution file.</summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgenticPatterns.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "AgenticPatterns.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repo root (AgenticPatterns.slnx). Set AGENT_DATA_DIR to point at the data directory.");
    }

    public static string BusinessDbPath => Path.Combine(FindDataDir(), "business.db");
    public static string SalesCsvPath => Path.Combine(FindDataDir(), "sales.csv");
    public static string ReviewsCsvPath => Path.Combine(FindDataDir(), "reviews.csv");
    public static string InboxJsonlPath => Path.Combine(FindDataDir(), "inbox.jsonl");
}
