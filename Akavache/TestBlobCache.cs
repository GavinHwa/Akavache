﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace Akavache
{
    public class TestBlobCache : ISecureBlobCache
    {
        public TestBlobCache(IScheduler scheduler = null, IEnumerable<KeyValuePair<string, byte[]>> initialContents = null)
        {
            Scheduler = scheduler ?? System.Reactive.Concurrency.Scheduler.CurrentThread;
            foreach (var item in initialContents ?? Enumerable.Empty<KeyValuePair<string, byte[]>>())
            {
                cache[item.Key] = new Tuple<CacheIndexEntry, byte[]>(new CacheIndexEntry(Scheduler.Now, null), item.Value);
            }
        }

        internal TestBlobCache(Action disposer, IScheduler scheduler = null, IEnumerable<KeyValuePair<string, byte[]>> initialContents = null)
            : this(scheduler, initialContents)
        {
            inner = Disposable.Create(disposer);
        }

        public IScheduler Scheduler { get; protected set; }

        readonly IDisposable inner;
        bool disposed;
        Dictionary<string, Tuple<CacheIndexEntry, byte[]>> cache = new Dictionary<string, Tuple<CacheIndexEntry, byte[]>>();

        public void Insert(string key, byte[] data, DateTimeOffset? absoluteExpiration = new DateTimeOffset?())
        {
            if (disposed) throw new ObjectDisposedException("TestBlobCache");

            lock (cache)
            {
                cache[key] = new Tuple<CacheIndexEntry, byte[]>(new CacheIndexEntry(Scheduler.Now, absoluteExpiration), data);
            }
        }

        public IObservable<byte[]> GetAsync(string key)
        {
            if (disposed) throw new ObjectDisposedException("TestBlobCache");
            lock (cache)
            {
                if (!cache.ContainsKey(key))
                {
                    return Observable.Throw<byte[]>(new KeyNotFoundException());
                }

                var item = cache[key];
                if (item.Item1.ExpiresAt != null && Scheduler.Now > item.Item1.ExpiresAt.Value)
                {
                    cache.Remove(key);
                    return Observable.Throw<byte[]>(new KeyNotFoundException());
                }

                return Observable.Return(item.Item2, Scheduler);
            }
        }

        public IObservable<DateTimeOffset?> GetCreatedAt(string key)
        {
            lock (cache)
            {
                if (!cache.ContainsKey(key))
                {
                    return Observable.Return<DateTimeOffset?>(null);
                }

                return Observable.Return<DateTimeOffset?>(cache[key].Item1.CreatedAt);
            }
        }

        public IEnumerable<string> GetAllKeys()
        {
            if (disposed) throw new ObjectDisposedException("TestBlobCache");
            lock (cache)
            {
                return cache.Keys.ToArray();
            }
        }

        public void Invalidate(string key)
        {
            if (disposed) throw new ObjectDisposedException("TestBlobCache");
            lock (cache)
            {
                if (cache.ContainsKey(key))
                {
                    cache.Remove(key);
                }
            }
        }

        public void InvalidateAll()
        {
            if (disposed) throw new ObjectDisposedException("TestBlobCache");
            lock (cache)
            {
                cache.Clear();
            }
        }

        public void Dispose()
        {
            Scheduler = null;
            cache = null;
            if (inner != null)
            {
                inner.Dispose();
            }
            disposed = true;
        }

        static readonly object gate = 42;
        public static TestBlobCache OverrideGlobals(IScheduler scheduler = null, params KeyValuePair<string, byte[]>[] initialContents)
        {
            var local = BlobCache.LocalMachine;
            var user = BlobCache.UserAccount;
#if !SILVERLIGHT
            var sec = BlobCache.Secure;
#endif

            var resetBlobCache = new Action(() =>
            {
                BlobCache.LocalMachine = local;
#if !SILVERLIGHT
                BlobCache.Secure = sec;
#endif
                BlobCache.UserAccount = user;
                Monitor.Exit(gate);
            });

            var testCache = new TestBlobCache(resetBlobCache, scheduler, initialContents);
            BlobCache.LocalMachine = testCache;
#if !SILVERLIGHT
            BlobCache.Secure = testCache;
#endif
            BlobCache.UserAccount = testCache;

            Monitor.Enter(gate);
            return testCache;
        }

        public static TestBlobCache OverrideGlobals(IDictionary<string, byte[]> initialContents, IScheduler scheduler = null)
        {
            return OverrideGlobals(scheduler, initialContents.ToArray());
        }

        public static TestBlobCache OverrideGlobals(IDictionary<string, object> initialContents, IScheduler scheduler = null)
        {
            var initialSerializedContents = initialContents
		    .Select(item => new KeyValuePair<string, byte[]>(item.Key, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(item.Value))))
		    .ToArray();

            return OverrideGlobals(scheduler, initialSerializedContents);
        }
    }
}
