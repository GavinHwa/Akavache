﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using NLog;
using ReactiveUI;

#if WINRT
using System.Reactive.Threading.Tasks;
#else
using System.Security.Cryptography;
#endif

namespace Akavache
{
    static class Utility
    {
#if WINRT
        static readonly dynamic log = LogHost.Default;
#else
        static readonly Logger log = LogManager.GetCurrentClassLogger();
#endif

#if WINRT
        public static string GetMd5Hash(string input)
        {
            throw new NotImplementedException();
        }
#else
        public static string GetMd5Hash(string input)
        {
#if SILVERLIGHT
            using (var md5Hasher = new MD5Managed())
#else
            using (var md5Hasher = MD5.Create())
#endif
            {
                // Convert the input string to a byte array and compute the hash.
                var data = md5Hasher.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sBuilder = new StringBuilder();
                foreach (var item in data)
                {
                    sBuilder.Append(item.ToString("x2"));
                }
                return sBuilder.ToString();
            }
        }
#endif

#if !WINRT
        public static IObservable<FileStream> SafeOpenFileAsync(string path, FileMode mode, FileAccess access, FileShare share, IScheduler scheduler = null)
        {
            scheduler = scheduler ?? RxApp.TaskpoolScheduler;
            var ret = new AsyncSubject<Stream> ();

            Observable.Start(() =>
            {
                try
                {
                    var createModes = new[]
                    {
                        FileMode.Create,
                        FileMode.CreateNew,
                        FileMode.OpenOrCreate,
                    };

                    // NB: We do this (even though it's incorrect!) because
                    // throwing lots of 1st chance exceptions makes debugging
                    // obnoxious, as well as a bug in VS where it detects
                    // exceptions caught by Observable.Start as Unhandled.
                    if (!createModes.Contains(mode) && !File.Exists(path))
                    {
                        ret.OnError(new FileNotFoundException());
                        return;
                    }

#if SILVERLIGHT
                    Observable.Start(() => new FileStream(path, mode, access, share, 4096), scheduler).Subscribe(ret);
#elif MONO
                    Observable.Start (() => 
                    {
                        var ufi = new Mono.Unix.UnixFileInfo (path);
                        return ufi.Open (mode, access);
                    }, scheduler).Cast<Stream>().Subscribe(ret);
#else
                    Observable.Start(() => new FileStream(path, mode, access, share, 4096, true), scheduler).Subscribe(ret);
#endif
                }
                catch (Exception ex)
                {
                    ret.OnError(ex);
                }
            }, scheduler);

            return ret;
        }

        public static void CreateRecursive(this DirectoryInfo This)
        {
            This.FullName.Split(Path.DirectorySeparatorChar).Scan("", (acc, x) =>
            {
                var path = Path.Combine(acc, x);

                if (path[path.Length - 1] == Path.VolumeSeparatorChar)
                {
                    path += Path.DirectorySeparatorChar;
                }

                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                return (new DirectoryInfo(path)).FullName;
            });
        }
#endif

        public static TAcc Scan<T, TAcc>(this IEnumerable<T> This, TAcc initialValue, Func<TAcc, T, TAcc> accFunc)
        {
            TAcc acc = initialValue;

            foreach (var x in This)
            {
                acc = accFunc(acc, x);
            }

            return acc;
        }

        public static IObservable<T> LogErrors<T>(this IObservable<T> This, string message = null)
        {
            return Observable.Create<T>(subj =>
            {
                return This.Subscribe(subj.OnNext,
                    ex =>
                    {
                        var msg = message ?? "0x" + This.GetHashCode().ToString("x");
                        log.Info("{0} failed with {1}:\n{2}", msg, ex.Message, ex.ToString());
                        subj.OnError(ex);
                    }, subj.OnCompleted);
            });
        }

        public static IObservable<Unit> CopyToAsync(this Stream This, Stream destination, IScheduler scheduler = null)
        {
#if WINRT
            return This.CopyToAsync(destination).ToObservable();
#else
            return Observable.Start(() =>
            {
                try
                {
                    This.CopyTo(destination);
                    This.Dispose();
                    destination.Dispose();
                }
                catch(Exception ex)
                {
                    log.Warn("CopyToAsync failed", ex);
                }
            }, scheduler ?? RxApp.TaskpoolScheduler);
#endif

#if FALSE
            var reader = Observable.FromAsyncPattern<byte[], int, int, int>(This.BeginRead, This.EndRead);
            var writer = Observable.FromAsyncPattern<byte[], int, int>(destination.BeginWrite, destination.EndWrite);

            //var bufs = new ThreadLocal<byte[]>(() => new byte[4096]);
            var bufs = new Lazy<byte[]>(() => new byte[4096]);

            var readStream = Observable.Defer(() => reader(bufs.Value, 0, 4096))
                .Repeat()
                .TakeWhile(x => x > 0);

            var ret = readStream
                .Select(x => writer(bufs.Value, 0, x))
                .Concat()
                .Aggregate(Unit.Default, (acc, _) => Unit.Default)
                .Finally(() => { This.Dispose(); destination.Dispose(); })
                .Multicast(new ReplaySubject<Unit>());

            ret.Connect();
            return ret;
#endif
        }

        public static void Retry(this Action block, int retries = 3)
        {
            while (true)
            {
                try
                {
                    block();
                    return;
                }
                catch (Exception)
                {
                    retries--;
                    if (retries == 0)
                    {
                        throw;
                    }
#if !WINRT
                    Thread.Sleep(10);
#endif
                }
            }
        }

        public static T Retry<T>(this Func<T> block, int retries = 3)
        {
            while (true)
            {
                try
                {
                    T ret = block();
                    return ret;
                }
                catch (Exception)
                {
                    retries--;
                    if (retries == 0)
                    {
                        throw;
                    }
#if !WINRT
                    Thread.Sleep(10);
#endif
                }
            }
        }
    }
}
