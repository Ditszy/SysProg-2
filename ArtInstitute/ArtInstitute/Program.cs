using System.Net;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Diagnostics;

namespace SistemskoProjekat;

class Program
{
    private static readonly ArtCache cache = new ArtCache(10,TimeSpan.FromMinutes(30));
    private static readonly HttpClient httpClient = new HttpClient();
    private static readonly ConcurrentDictionary<string, object> queryLocks = new();
    private static readonly object dispatchLock = new object();

    private static volatile bool isRunning = true;
    private static HttpListener? listener;
    private static int inFlightRequests;
    private const int maxParallelRequests = 16;

    static void Main(string[] args)
    {
        httpClient.DefaultRequestHeaders.Add("User-Agent", "SistemskoProjekat");

        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true;
            Logger.Log("Gasenje servera inicirano");
            isRunning = false;

            listener?.Abort();
        };

        ThreadPool.SetMinThreads(maxParallelRequests, maxParallelRequests);

        listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:8080/");
        
        try {
            listener.Start();
            Logger.Log("Server pokrenut na http://localhost:8080/");

            while (isRunning)
            {
                var context = listener.GetContext();
                WaitForFreeSlot();
                if (!isRunning)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    context.Response.Close();
                    break;
                }

                ThreadPool.QueueUserWorkItem(state => ProcessQueuedRequest((HttpListenerContext)state!), context);
            }
        }
        catch (HttpListenerException) {
            Logger.Log("Listener zaustavljen");
        }
        catch (Exception ex) {
            if (isRunning) Logger.Log($"Greska: {ex.Message}");
        }
        finally {
            lock (dispatchLock)
            {
                while (inFlightRequests > 0)
                {
                    Monitor.Wait(dispatchLock);
                }
            }
            
            listener?.Close();
            Logger.Log("Sistem ugasen");
        }
    }

    private static void WaitForFreeSlot()
    {
        lock (dispatchLock)
        {
            while (inFlightRequests >= maxParallelRequests && isRunning)
            {
                Monitor.Wait(dispatchLock);
            }

            if (isRunning)
                inFlightRequests++;
        }
    }

    private static void ProcessQueuedRequest(HttpListenerContext context)
    {
        try
        {
            ProcessRequest(context);
        }
        catch (Exception ex)
        {
            Logger.Log($"Greska u obradi: {ex.Message}");
        }
        finally
        {
            context.Response.Close();

            lock (dispatchLock)
            {
                inFlightRequests--;
                Monitor.Pulse(dispatchLock);
            }
        }
    }

    private static void ProcessRequest(HttpListenerContext context)
    {
        var sw = Stopwatch.StartNew();
        string query = context.Request.QueryString["q"];
        string author = context.Request.QueryString["author"];

        bool hasQuery = !string.IsNullOrWhiteSpace(query);
        bool hasAuthor = !string.IsNullOrWhiteSpace(author);
        
        if (!hasQuery && !hasAuthor)
        {
            SendResponse(context, "{\"error\": \"Prosledi parametar\"}", HttpStatusCode.BadRequest);
            return;
        }

        string searchType = hasAuthor ? "author" : "q";
        string searchValue = NormalizeInput(hasAuthor ? author : query);
        string cacheKey = $"{searchType}:{searchValue}";

        if (!cache.TryGet(cacheKey, out string result))
        {
            var queryLock = queryLocks.GetOrAdd(cacheKey, _ => new object());
            lock (queryLock)
            {
                if (!cache.TryGet(cacheKey, out result))
                {
                    result = FetchFromApi(searchType, searchValue);
                    cache.Add(cacheKey, result);
                    sw.Stop();
                    Logger.Log($"API poziv ({searchType}): {searchValue}, Vreme: {sw.Elapsed.TotalMilliseconds}ms");
                }
                else
                {
                    sw.Stop();
                    Logger.Log($"Stampedo ({searchType}): {searchValue}, Vreme: {sw.Elapsed.TotalMilliseconds}ms");
                }

                queryLocks.TryRemove(cacheKey, out _);
            }
        }
        else{

            sw.Stop();
            Logger.Log($"Cache hit ({searchType}): {searchValue}, Vreme: {sw.Elapsed.TotalMilliseconds}ms");
        }
        

        if (IsEmptyArtworkResult(result))
        {
            SendResponse(context, "{\"error\": \"Umetnicka dela nisu pronadjena\"}", HttpStatusCode.NotFound);
            return;
        }

        SendResponse(context, result, HttpStatusCode.OK);
    }

    private static string FetchFromApi(string searchType, string searchValue)
    {
        string url = BuildApiUrl(searchType, searchValue);
        try
        {
            return httpClient.GetStringAsync(url).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.Log($"Greska pri mreznom pozivu: {ex.Message}");
            return "{\"error\": \"API greska\"}";
        }
    }

    private static string BuildApiUrl(string searchType, string searchValue)
    {
        string encodedValue = Uri.EscapeDataString(searchValue);
        if (searchType == "author")
        {
            return $"https://api.artic.edu/api/v1/artworks/search?query[term][artist_title]={encodedValue}";
        }

        return $"https://api.artic.edu/api/v1/artworks/search?q={encodedValue}";
    }

    private static string NormalizeInput(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static bool IsEmptyArtworkResult(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("data", out var dataElement))
            {
                return false;
            }

            return dataElement.ValueKind == JsonValueKind.Array && dataElement.GetArrayLength() == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void SendResponse(HttpListenerContext context, string body, HttpStatusCode status)
    {
        try
        {
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            using var writer = new StreamWriter(context.Response.OutputStream);
            writer.Write(body);
        }
        catch (Exception ex)
        {
            Logger.Log($"Greska pri slanju odgovora: {ex.Message}");
        }
    }
}
