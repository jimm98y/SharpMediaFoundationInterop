using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// Work on the rows of a picture, shared out by <see cref="ParallelRows.For"/>. One object is made once and used for
    /// every frame: what a frame needs goes in its fields before the call.
    /// </summary>
    internal abstract class RowWork
    {
        internal int _from, _to, _chunk, _chunks;
        internal int _next;     // the next chunk to take
        internal int _left;     // the chunks not done yet
        internal int _helpers;  // the pool's threads at work on it
        internal Exception _error;
        internal readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);

        /// <summary>
        /// The rows from <paramref name="from"/> up to <paramref name="to"/>, not including it: on any thread, beside other
        /// rows of the same call on others.
        /// </summary>
        protected internal abstract void Run(int from, int to);

        internal void RunChunks()
        {
            int i;
            while ((i = Interlocked.Increment(ref _next) - 1) < _chunks)
            {
                int start = _from + i * _chunk;
                try
                {
                    Run(start, Math.Min(_to, start + _chunk));
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref _error, ex, null);
                }
                finally
                {
                    if (Interlocked.Decrement(ref _left) == 0)
                        _done.Set();
                }
            }
        }
    }

    /// <summary>
    /// The rows of a picture shared out among the cores, allocating nothing: Parallel.For makes tasks, delegates and the
    /// state of each worker at each call, which a video's frames, converted many times a second, make garbage of. The rows
    /// are cut into chunks, taken in turn by the caller and the threads of a pool of its own, which wait for work while
    /// there is none. Calls from several threads at once - two videos converted side by side - share the pool.
    /// </summary>
    internal static class ParallelRows
    {
        /// <summary>The pool's threads: one fewer than the cores, the caller being the last.</summary>
        private static readonly int Workers = Math.Max(0, Math.Min(Environment.ProcessorCount - 1, 31));

        /// <summary>How many chunks of a call each thread gets, roughly: a few, so that one slow thread holds back little.</summary>
        private const int ChunksPerThread = 3;

        private static readonly object Lock = new object();
        private static readonly SemaphoreSlim Wake = new SemaphoreSlim(0);
        private static RowWork[] _jobs = new RowWork[4];
        private static int _jobCount;
        private static bool _started;

        /// <summary>
        /// Runs <paramref name="work"/> over the rows from <paramref name="from"/> up to <paramref name="to"/>, not including
        /// it, on the pool's threads and the caller's; returns once every row is done. An exception of any row is thrown
        /// here, after the others are done.
        /// </summary>
        public static void For(int from, int to, RowWork work)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));
            int rows = to - from;
            if (rows <= 0)
                return;
            if (Workers == 0 || rows == 1)
            {
                work.Run(from, to);
                return;
            }
            EnsureStarted();

            int chunk = (rows + (Workers + 1) * ChunksPerThread - 1) / ((Workers + 1) * ChunksPerThread);
            int chunks = (rows + chunk - 1) / chunk;
            work._from = from;
            work._to = to;
            work._chunk = chunk;
            work._chunks = chunks;
            work._next = 0;
            work._left = chunks;
            work._error = null;
            work._done.Reset();

            lock (Lock)
            {
                if (_jobCount == _jobs.Length)
                    Array.Resize(ref _jobs, _jobs.Length * 2);
                _jobs[_jobCount++] = work;
            }
            // as many woken as there is work for, but never more waiting than there are threads
            int wake = Math.Min(Workers, chunks - 1) - Wake.CurrentCount;
            if (wake > 0)
                Wake.Release(wake);

            work.RunChunks();
            work._done.Wait();

            lock (Lock)
            {
                for (int i = 0; i < _jobCount; i++)
                {
                    if (ReferenceEquals(_jobs[i], work))
                    {
                        _jobs[i] = _jobs[--_jobCount];
                        _jobs[_jobCount] = null;
                        break;
                    }
                }
            }
            // a thread that took it before it was taken off finds no chunk left: once it is out, the object is the caller's
            var spin = new SpinWait();
            while (Volatile.Read(ref work._helpers) != 0)
                spin.SpinOnce();

            var error = work._error;
            if (error != null)
            {
                work._error = null;
                ExceptionDispatchInfo.Capture(error).Throw();
            }
        }

        private static void EnsureStarted()
        {
            if (Volatile.Read(ref _started))
                return;
            lock (Lock)
            {
                if (_started)
                    return;
                for (int i = 0; i < Workers; i++)
                    new Thread(WorkerLoop) { IsBackground = true, Name = $"ParallelRows {i + 1}" }.Start();
                Volatile.Write(ref _started, true);
            }
        }

        private static void WorkerLoop()
        {
            while (true)
            {
                Wake.Wait();
                RowWork work = null;
                lock (Lock)
                {
                    for (int i = 0; i < _jobCount; i++)
                    {
                        var job = _jobs[i];
                        if (Volatile.Read(ref job._next) < job._chunks)
                        {
                            work = job;
                            Interlocked.Increment(ref work._helpers);
                            break;
                        }
                    }
                }
                if (work == null)
                    continue; // done by others before this thread woke
                try
                {
                    work.RunChunks();
                }
                finally
                {
                    Interlocked.Decrement(ref work._helpers);
                }
            }
        }
    }
}
