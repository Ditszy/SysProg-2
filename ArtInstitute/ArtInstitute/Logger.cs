using System.Collections.Concurrent;

public static class Logger
{
    private static readonly BlockingCollection<string> logQueue = new();
    private static readonly string logPath = "server_log.txt";
    private static readonly Thread loggerThread;
    private static volatile bool running = true;


    static Logger()
    {
        try
        {
            File.WriteAllText(logPath, $"--- Log zapocet: {DateTime.Now} ---\n");
            loggerThread = new Thread(ProcessLogs);
            loggerThread.IsBackground = true;
            loggerThread.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Neuspesan reset log fajla: {ex.Message}");
        }
    }

    public static void Log(string message)
    {
        string logLine = $"[{DateTime.Now:HH:mm:ss}] " + $"[Nit {Thread.CurrentThread.ManagedThreadId}] " + message;
        logQueue.Add(logLine);
    }
    private static void ProcessLogs()
    {
        using var writer = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        while (running || logQueue.Count > 0)
        {
            try
            {
                if (logQueue.TryTake(out string? log, 500))
                {
                    Console.WriteLine(log);
                    writer.WriteLine(log);
                    writer.Flush();
                }
            }
            catch (Exception ex) { Console.WriteLine($"Logger greska: {ex.Message}"); }
        }
    }
    public static void Shutdown()
    {
        running = false; loggerThread.Join();
    }
}