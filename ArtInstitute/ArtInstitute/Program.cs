using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SistemskoProjekat;

class Program
{
    private static readonly ArtCache cache = new ArtCache(10, TimeSpan.FromMinutes(30));
    private static readonly HttpClient httpClient = new HttpClient();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> queryLocks = new();
    private static readonly BlockingCollection<HttpListenerContext> requestQueue = new();

    private static volatile bool isRunning = true;
    private static HttpListener? listener;
    private const int maxParallelRequests = 16;

    static void Main(string[] args)
    {
        httpClient.DefaultRequestHeaders.Add("User-Agent", "SistemskoProjekat");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Logger.Log("Gasenje servera inicirano");
            isRunning = false;

            if (!requestQueue.IsAddingCompleted)
            {
                requestQueue.CompleteAdding();
            }

            listener?.Abort();
        };

        listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:8080/");

        Task[] workers = StartWorkers(maxParallelRequests);

        try
        {
            listener.Start();

            Logger.Log("Server pokrenut");

            while (isRunning)
            {
                var context = listener.GetContext();

                if (!isRunning)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    context.Response.Close();
                    break;
                }

                try
                {
                    requestQueue.Add(context);
                }
                catch (InvalidOperationException)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    context.Response.Close();
                }
            }
        }
        catch (HttpListenerException)
        {
            Logger.Log("Listener zaustavljen");
        }
        catch (Exception ex)
        {
            if (isRunning)
            {
                Logger.Log(ex.Message);
            }
        }
        finally
        {
            if (!requestQueue.IsAddingCompleted)
            {
                requestQueue.CompleteAdding();
            }

            Task.WaitAll(workers);
            listener?.Close();
            httpClient.Dispose();
            cache.Shutdown();
            Logger.Shutdown();
        }
    }

    private static Task[] StartWorkers(int count)
    {
        var tasks = new Task[count];
        for (int i = 0; i < count; i++)
        {
            // Alternativa za Task.Run, mnogo bolje radi
            tasks[i] = Task.Factory.StartNew(async () =>
            {
                foreach (var context in requestQueue.GetConsumingEnumerable())
                {
                    try
                    {
                        await ProcessRequestAsync(context);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Greska obrade: {ex.Message}");
                        await Utils.SendResponseAsync(context, "{\"error\":\"Server greska\"}", HttpStatusCode.InternalServerError);
                    }
                    finally
                    {
                        context.Response.Close();
                    }
                }
            }, TaskCreationOptions.LongRunning);

            // tasks[i] = Task.Run(async () =>
            // {
            //     foreach (var context in requestQueue.GetConsumingEnumerable())
            //     {
            //         try
            //         {
            //             await ProcessRequestAsync(context);
            //         }
            //         catch (Exception ex)
            //         {
            //             Logger.Log($"Greska obrade: {ex.Message}");
            //             await Utils.SendResponseAsync(context, "{\"error\":\"Server greska\"}", HttpStatusCode.InternalServerError);
            //         }
            //         finally
            //         {
            //             context.Response.Close();
            //         }
            //     }
            // });
        }
        return tasks;
    }

    private static async Task ProcessRequestAsync(HttpListenerContext context)
    {
        var sw = Stopwatch.StartNew();

        string? query = context.Request.QueryString["q"];
        string? author = context.Request.QueryString["author"];

        bool hasQuery = !string.IsNullOrWhiteSpace(query);
        bool hasAuthor = !string.IsNullOrWhiteSpace(author);

        if (!hasQuery && !hasAuthor)
        {
            await Utils.SendResponseAsync(context, "{\"error\":\"Prosledi parametar\"}", HttpStatusCode.BadRequest);

            return;
        }

        string searchType = hasAuthor ? "author" : "q";
        string searchValue = Utils.NormalizeInput(hasAuthor ? author! : query!);

        string cacheKey = $"{searchType}:{searchValue}";

        if (!cache.TryGet(cacheKey, out string result))
        {
            var queryLock = queryLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));

            await queryLock.WaitAsync();
            try
            {
                if (!cache.TryGet(cacheKey, out result))
                {
                    var fetchTask = FetchFromApiAsync(searchType, searchValue);

                    result = await fetchTask.ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            cache.Add(cacheKey, t.Result);

                            Logger.Log($"ContinueWith kesiranje: {cacheKey}");

                            return t.Result;
                        }

                        return "{\"error\":\"API greska\"}";
                    }, TaskScheduler.Default);
                }
            }
            finally
            {
                queryLock.Release();
                if (queryLock.CurrentCount == 1)
                {
                    queryLocks.TryRemove(cacheKey, out _);
                }
            }
        }

        await Utils.SendResponseAsync(context, result, HttpStatusCode.OK);

        sw.Stop();

        Logger.Log($"Obrada gotova: {cacheKey}, " + $"{sw.Elapsed.TotalMilliseconds}ms");
    }


    private static async Task<string> FetchFromApiAsync(string searchType, string searchValue)
    {
        string url = Utils.BuildApiUrl(searchType, searchValue);

        try
        {
            return await httpClient.GetStringAsync(url);
        }
        catch (Exception ex)
        {
            Logger.Log($"API greska: {ex.Message}");

            return "{\"error\":\"API greska\"}";
        }
    }

}
