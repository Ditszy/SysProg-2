using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

public class ArtCache
{
    private class CacheEntry
    {
        public string Value { get; set; }
        public LinkedListNode<string> Node { get; }
        public DateTime CreatedAt { get; }

        public CacheEntry(string value, LinkedListNode<string> node)
        {
            Value = value;
            Node = node;
            CreatedAt = DateTime.Now;
        }
    }

    private readonly int maxSize;
    private readonly TimeSpan maxLifeTime;
    private readonly Dictionary<string, CacheEntry> data;
    private readonly LinkedList<string> lruList;
    private readonly object lockObj = new object();
    private readonly Thread cleanupThread;
    private volatile bool running = true;

    public ArtCache(int maxSize, TimeSpan maxLifeTime)
    {
        this.maxSize = maxSize;
        this.maxLifeTime = maxLifeTime;
        data = new Dictionary<string, CacheEntry>();
        lruList = new LinkedList<string>();

        cleanupThread = new Thread(BackgroundCleanup);
        cleanupThread.IsBackground = true;
        cleanupThread.Start();
    }

    public bool TryGet(string key, out string value)
    {
        if (data.TryGetValue(key, out var entry))
        {
            lock (lockObj)
            {
                if (lruList.First != entry.Node) //provera da li je na pocetku
                {
                    lruList.Remove(entry.Node);
                    lruList.AddFirst(entry.Node);
                }
            }
            value = entry.Value;
            return true;
        }
        value = null;
        return false;
    }

    public void Add(string key, string value)
    {
        lock (lockObj)
        {
            if (data.TryGetValue(key, out var existingEntry))
            {
                existingEntry.Value = value;
                lruList.Remove(existingEntry.Node);
                lruList.AddFirst(existingEntry.Node);
                return;
            }

            if (data.Count >= maxSize)
            {
                var oldestKey = lruList.Last.Value;
                if (data.TryGetValue(oldestKey, out var toRemove))
                {
                    RemoveEntry(oldestKey, toRemove);
                }
            }

            var newNode = new LinkedListNode<string>(key);
            lruList.AddFirst(newNode);
            data[key] = new CacheEntry(value, newNode);
        }
    }

    private void RemoveEntry(string key, CacheEntry entry)
    {
        lruList.Remove(entry.Node);
        data.Remove(key);
    }

    private void BackgroundCleanup()
    {
        while (running)
        {
            Thread.Sleep(TimeSpan.FromMinutes(5));

            lock (lockObj)
            {
                DateTime now = DateTime.Now;

                var keysToRemove = data
                    .Where(pair => now - pair.Value.CreatedAt > maxLifeTime)
                    .Select(pair => pair.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    if (data.TryGetValue(key, out var entry))
                    {
                        RemoveEntry(key, entry);
                    }
                }

                if (keysToRemove.Count > 0)
                {
                    Logger.Log($"Cleanup thread obrisao " + $"{keysToRemove.Count} elemenata");
                }
            }
        }
    }

    public void Shutdown()
    {
        running = false;
    }

}
