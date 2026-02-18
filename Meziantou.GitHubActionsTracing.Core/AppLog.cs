namespace Meziantou.GitHubActionsTracing;

public static class AppLog
{
    public static void Section(string message)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {message} ===");
    }

    public static void Info(string message)
    {
        Console.WriteLine($"[INFO] {message}");
    }

    public static void Warning(string message)
    {
        Console.WriteLine($"[WARN] {message}");
    }
}
