public static class Logger
{
    private static readonly object logLock = new object();
    private static readonly string logPath = "server_log.txt";

    static Logger()
    {
        try
        {
            using (var stream = File.Open(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                using (var writer = new StreamWriter(stream))
                {
                    writer.WriteLine($"--- Log zapocet: {DateTime.Now} ---");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Neuspesan reset log fajla: {ex.Message}");
        }
    }

    public static void Log(string message)
    {
        string logLine = $"[{DateTime.Now:HH:mm:ss}] [Nit {Thread.CurrentThread.ManagedThreadId}] {message}";
        
        lock (logLock)
        {
            Console.WriteLine(logLine);

            try 
            {
                File.AppendAllLines(logPath, new[] { logLine });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Greška pri upisu u fajl: {ex.Message}");
            }
        }
    }
}