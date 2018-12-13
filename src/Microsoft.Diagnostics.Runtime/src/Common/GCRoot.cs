﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Runtime.Desktop;

namespace Microsoft.Diagnostics.Runtime
{
    /// <summary>
    /// A delegate for reporting GCRoot progress.
    /// </summary>
    /// <param name="source">The GCRoot sending the event.</param>
    /// <param name="current">The total number of objects processed.</param>
    /// <param name="total">The total number of objects in the heap, if that number is known, otherwise -1.</param>
    public delegate void GCRootProgressEvent(GCRoot source, long current, long total);

    /// <summary>
    /// A helper class to find the GC rooting chain for a particular object.
    /// </summary>
    public class GCRoot
    {
        private static readonly Stack<ClrObject> s_emptyStack = new Stack<ClrObject>();
        private int _maxTasks;

        /// <summary>
        /// Since GCRoot can be long running, this event will provide periodic updates to how many objects the algorithm
        /// has processed.  Note that in the case where we search all objects and do not find a path, it's unlikely that
        /// the number of objects processed will ever reach the total number of objects on the heap.  That's because there
        /// will be garbage objects on the heap we can't reach.
        /// </summary>
        public event GCRootProgressEvent ProgressUpdate;

        /// <summary>
        /// Returns the heap that's associated with this GCRoot instance.
        /// </summary>
        public ClrHeap Heap { get; }

        /// <summary>
        /// Whether or not to allow GC root to search in parallel or not.  Note that GCRoot does not have to respect this
        /// flag.  Parallel searching of roots will only happen if a copy of the stack and heap were built using BuildCache,
        /// and if the entire heap was cached.  Note that ClrMD and underlying APIs do NOT support multithreading, so this
        /// is only used when we can ensure all relevant data is local memory and we do not need to touch the debuggee.
        /// </summary>
        public bool AllowParallelSearch { get; set; } = true;

        /// <summary>
        /// The maximum number of tasks allowed to run in parallel, if GCRoot does a parallel search.
        /// </summary>
        public int MaximumTasksAllowed
        {
            get => _maxTasks;
            set
            {
                if (_maxTasks < 0)
                    throw new InvalidOperationException($"{nameof(MaximumTasksAllowed)} cannot be less than 0!");

                _maxTasks = value;
            }
        }

        /// <summary>
        /// Returns true if all relevant heap and root data is locally cached in this process for fast GCRoot processing.
        /// </summary>
        public bool IsFullyCached => Heap.AreRootsCached;

        /// <summary>
        /// Creates a GCRoot helper object for the given heap.
        /// </summary>
        /// <param name="heap">The heap the object in question is on.</param>
        public GCRoot(ClrHeap heap)
        {
            Heap = heap ?? throw new ArgumentNullException(nameof(heap));
            _maxTasks = Environment.ProcessorCount * 2;
        }

        /// <summary>
        /// Enumerates GCRoots of a given object.  Similar to !gcroot.  Note this function only returns paths that are fully unique.
        /// </summary>
        /// <param name="target">The target object to search for GC rooting.</param>
        /// <param name="cancelToken">A cancellation token to stop enumeration.</param>
        /// <returns>An enumeration of all GC roots found for target.</returns>
        public IEnumerable<GCRootPath> EnumerateGCRoots(ulong target, CancellationToken cancelToken)
        {
            return EnumerateGCRoots(target, true, cancelToken);
        }

        /// <summary>
        /// Enumerates GCRoots of a given object.  Similar to !gcroot.
        /// </summary>
        /// <param name="target">The target object to search for GC rooting.</param>
        /// <param name="unique">Whether to only return fully unique paths.</param>
        /// <param name="cancelToken">A cancellation token to stop enumeration.</param>
        /// <returns>An enumeration of all GC roots found for target.</returns>
        public IEnumerable<GCRootPath> EnumerateGCRoots(ulong target, bool unique, CancellationToken cancelToken)
        {
            Heap.BuildDependentHandleMap(cancelToken);

            long totalObjects = Heap.TotalObjects;
            long lastObjectReported = 0;

            bool parallel = AllowParallelSearch && IsFullyCached && _maxTasks > 0;

            Dictionary<ulong, LinkedListNode<ClrObject>> knownEndPoints = new Dictionary<ulong, LinkedListNode<ClrObject>>();

            ObjectSet processedObjects = parallel 
                ? new ParallelObjectSet(Heap) 
                : new ObjectSet(Heap);

            Task<Tuple<LinkedList<ClrObject>, ClrRoot>>[] tasks = new Task<Tuple<LinkedList<ClrObject>, ClrRoot>>[_maxTasks];

            int initial = 0;

            foreach (ClrHandle handle in Heap.EnumerateStrongHandles())
            {
                Debug.Assert(handle.HandleType != HandleType.Dependent);
                Debug.Assert(handle.Object != 0);

                GCRootPath? gcRootPath = ProcessRoot(handle.Object, handle.Type, () => GetHandleRoot(handle));

                if (gcRootPath != null)
                    yield return gcRootPath.Value;
            }

            foreach (ClrRoot root in Heap.EnumerateStackRoots())
            {
                GCRootPath? gcRootPath = ProcessRoot(root.Object, root.Type, () => root);

                if (gcRootPath != null)
                    yield return gcRootPath.Value;
            }

            foreach (Tuple<LinkedList<ClrObject>, ClrRoot> result in WhenEach(tasks))
            {
                ReportObjectCount(processedObjects.Count, ref lastObjectReported, totalObjects);
                yield return new GCRootPath {Root = result.Item2, Path = result.Item1.ToArray()};
            }

            ReportObjectCount(totalObjects, ref lastObjectReported, totalObjects);

            yield break;

            GCRootPath? ProcessRoot(ulong rootRef, ClrType rootType, Func<ClrRoot> rootFunc)
            {
                if (processedObjects.Contains(rootRef))
                    return null;

                Debug.Assert(Heap.GetObjectType(rootRef) == rootType);

                var rootObject = ClrObject.Create(rootRef, rootType);

                GCRootPath? result = null;

                if (parallel)
                {
                    Task<Tuple<LinkedList<ClrObject>, ClrRoot>> task = PathToParallelAsync(processedObjects, knownEndPoints, target, unique, cancelToken, rootObject, rootFunc);
                    if (initial < tasks.Length)
                    {
                        tasks[initial++] = task;
                    }
                    else
                    {
                        int i = Task.WaitAny(tasks);
                        Task<Tuple<LinkedList<ClrObject>, ClrRoot>> completed = tasks[i];
                        tasks[i] = task;

                        if (completed.Result.Item1 != null)
                            result = new GCRootPath {Root = completed.Result.Item2, Path = completed.Result.Item1.ToArray()};
                    }
                }
                else
                {
                    LinkedList<ClrObject> path = PathTo(processedObjects, knownEndPoints, rootObject, target, unique, false, cancelToken).FirstOrDefault();
                    if (path != null)
                        result = new GCRootPath {Root = rootFunc(), Path = path.ToArray()};
                }

                ReportObjectCount(processedObjects.Count, ref lastObjectReported, totalObjects);

                return result;
            }
        }

        private void ReportObjectCount(long curr, ref long lastObjectReported, long totalObjects)
        {
            if (curr != lastObjectReported)
            {
                lastObjectReported = curr;
                ProgressUpdate?.Invoke(this, lastObjectReported, totalObjects);
            }
        }

        private static IEnumerable<Tuple<LinkedList<ClrObject>, ClrRoot>> WhenEach(Task<Tuple<LinkedList<ClrObject>, ClrRoot>>[] tasks)
        {
            List<Task<Tuple<LinkedList<ClrObject>, ClrRoot>>> taskList = tasks.Where(t => t != null).ToList();

            while (taskList.Count > 0)
            {
                Task<Tuple<LinkedList<ClrObject>, ClrRoot>> task = Task.WhenAny(taskList).Result;
                if (task.Result.Item1 != null)
                    yield return task.Result;

                bool removed = taskList.Remove(task);
                Debug.Assert(removed);
            }
        }

        /// <summary>
        /// Returns the path from the start object to the end object (or null if no such path exists).
        /// </summary>
        /// <param name="source">The initial object to start the search from.</param>
        /// <param name="target">The object we are searching for.</param>
        /// <param name="cancelToken">A cancellation token to stop searching.</param>
        /// <returns>A path from 'source' to 'target' if one exists, null if one does not.</returns>
        public LinkedList<ClrObject> FindSinglePath(ulong source, ulong target, CancellationToken cancelToken)
        {
            Heap.BuildDependentHandleMap(cancelToken);
            return PathTo(new ObjectSet(Heap), null, new ClrObject(source, Heap.GetObjectType(source)), target, false, false, cancelToken).FirstOrDefault();
        }

        /// <summary>
        /// Returns the path from the start object to the end object (or null if no such path exists).
        /// </summary>
        /// <param name="source">The initial object to start the search from.</param>
        /// <param name="target">The object we are searching for.</param>
        /// <param name="unique">Whether to only enumerate fully unique paths.</param>
        /// <param name="cancelToken">A cancellation token to stop enumeration.</param>
        /// <returns>A path from 'source' to 'target' if one exists, null if one does not.</returns>
        public IEnumerable<LinkedList<ClrObject>> EnumerateAllPaths(ulong source, ulong target, bool unique, CancellationToken cancelToken)
        {
            Heap.BuildDependentHandleMap(cancelToken);
            return PathTo(
                new ObjectSet(Heap),
                new Dictionary<ulong, LinkedListNode<ClrObject>>(),
                new ClrObject(source, Heap.GetObjectType(source)),
                target,
                unique,
                false,
                cancelToken);
        }

        /// <summary>
        /// Builds a cache of the GC heap and roots.  This will consume a LOT of memory, so when calling it you must wrap this in
        /// a try/catch for OutOfMemoryException.
        /// Note that this function allows you to choose whether we have exact thread callstacks or not.  Exact thread callstacks
        /// will essentially force ClrMD to walk the stack as a real GC would, but this can take 10s of minutes when the thread count gets
        /// into the 1000s.
        /// </summary>
        /// <param name="cancelToken">The cancellation token used to cancel the operation if it's taking too long.</param>
        public void BuildCache(CancellationToken cancelToken)
        {
            Heap.CacheRoots(cancelToken);
            Heap.CacheHeap(cancelToken);
        }

        /// <summary>
        /// Clears all caches, reclaiming most memory held by this GCRoot object.
        /// </summary>
        public void ClearCache()
        {
            Heap.ClearHeapCache();
            Heap.ClearRootCache();
        }

        private Task<Tuple<LinkedList<ClrObject>, ClrRoot>> PathToParallelAsync(
            ObjectSet seen,
            Dictionary<ulong, LinkedListNode<ClrObject>> knownEndPoints,
            ulong target,
            bool unique,
            CancellationToken cancelToken,
            ClrObject clrObject,
            Func<ClrRoot> rootFunc)
        {
            Debug.Assert(IsFullyCached);

            return Task.Run(
                () =>
                    {
                        LinkedList<ClrObject> path = PathTo(seen, knownEndPoints, clrObject, target, unique, true, cancelToken).FirstOrDefault();
                        return new Tuple<LinkedList<ClrObject>, ClrRoot>(path, path == null ? null : rootFunc());
                    },
                cancelToken);
        }

        private IEnumerable<LinkedList<ClrObject>> PathTo(
            ObjectSet seen,
            Dictionary<ulong, LinkedListNode<ClrObject>> knownEndPoints,
            ClrObject source,
            ulong target,
            bool unique,
            bool parallel,
            CancellationToken cancelToken)
        {
            seen.Add(source.Address);

            if (source.Type == null)
                yield break;

            LinkedList<PathEntry> path = new LinkedList<PathEntry>();

            if (source.Address == target)
            {
                path.AddLast(new PathEntry {Object = source});
                yield return GetResult(knownEndPoints, path, null, target);

                yield break;
            }

            path.AddLast(
                new PathEntry
                {
                    Object = source,
                    Todo = GetRefs(seen, knownEndPoints, source, target, unique, cancelToken, out bool foundTarget, out LinkedListNode<ClrObject> foundEnding)
                });

            // Did the 'start' object point directly to 'end'?  If so, early out.
            if (foundTarget)
            {
                path.AddLast(new PathEntry {Object = Heap.GetObject(target)});
                yield return GetResult(knownEndPoints, path, null, target);
            }
            else if (foundEnding != null)
            {
                yield return GetResult(knownEndPoints, path, foundEnding, target);
            }

            while (path.Count > 0)
            {
                cancelToken.ThrowIfCancellationRequested();

                TraceFullPath(null, path);
                PathEntry last = path.Last.Value;

                if (last.Todo.Count == 0)
                {
                    // We've exhausted all children and didn't find the target.  Remove this node
                    // and continue.
                    path.RemoveLast();
                }
                else
                {
                    // We loop here in case we encounter an object we've already processed (or if
                    // we can't get an object's type...inconsistent heap happens sometimes).
                    do
                    {
                        cancelToken.ThrowIfCancellationRequested();
                        ClrObject next = last.Todo.Pop();

                        // Now that we are in the process of adding 'next' to the path, don't ever consider
                        // this object in the future.
                        if (!seen.Add(next.Address))
                            continue;

                        // We should never reach the 'end' here, as we always check if we found the target
                        // value when adding refs below.
                        Debug.Assert(next.Address != target);

                        PathEntry nextPathEntry = new PathEntry
                        {
                            Object = next,
                            Todo = GetRefs(seen, knownEndPoints, next, target, unique, cancelToken, out foundTarget, out foundEnding)
                        };

                        path.AddLast(nextPathEntry);

                        // If we found the target object while enumerating refs of the current object, we are done.
                        if (foundTarget)
                        {
                            path.AddLast(new PathEntry {Object = Heap.GetObject(target)});
                            TraceFullPath("FoundTarget", path);

                            yield return GetResult(knownEndPoints, path, null, target);

                            path.RemoveLast();
                            path.RemoveLast();
                        }
                        else if (foundEnding != null)
                        {
                            TraceFullPath(path, foundEnding);
                            yield return GetResult(knownEndPoints, path, foundEnding, target);

                            path.RemoveLast();
                        }

                        // Now that we've added a new entry to 'path', break out of the do/while that's looping through Todo.
                        break;
                    } while (last.Todo.Count > 0);
                }
            }
        }

        private static Stack<ClrObject> GetRefs(
            ObjectSet seen,
            Dictionary<ulong, LinkedListNode<ClrObject>> knownEndPoints,
            ClrObject obj,
            ulong target,
            bool unique,
            CancellationToken cancelToken,
            out bool foundTarget,
            out LinkedListNode<ClrObject> foundEnding)
        {
            // These asserts slow debug down by a lot, but it's important to ensure consistency in retail.
            //Debug.Assert(obj.Type != null);
            //Debug.Assert(obj.Type == _heap.GetObjectType(obj.Address));

            Stack<ClrObject> result = null;

            bool found = false;
            LinkedListNode<ClrObject> ending = null;
            if (obj.ContainsPointers)
            {
                foreach (ClrObject reference in obj.EnumerateObjectReferences(true))
                {
                    cancelToken.ThrowIfCancellationRequested();
                    if (ending == null && knownEndPoints != null)
                    {
                        lock (knownEndPoints)
                        {
                            if (unique)
                            {
                                if (knownEndPoints.ContainsKey(reference.Address))
                                    continue;
                            }
                            else
                            {
                                knownEndPoints.TryGetValue(reference.Address, out ending);
                            }
                        }
                    }

                    if (!seen.Contains(reference.Address))
                    {
                        if (result is null)
                            result = new Stack<ClrObject>();

                        result.Push(reference);
                        if (reference.Address == target)
                            found = true;
                    }
                }
            }

            foundTarget = found;
            foundEnding = ending;

            return result ?? s_emptyStack;
        }

        private static LinkedList<ClrObject> GetResult(
            Dictionary<ulong, LinkedListNode<ClrObject>> knownEndPoints,
            LinkedList<PathEntry> path,
            LinkedListNode<ClrObject> ending,
            ulong target)
        {
            LinkedList<ClrObject> result = new LinkedList<ClrObject>(path.Select(p => p.Object).ToArray());

            for (; ending != null; ending = ending.Next)
                result.AddLast(ending.Value);

            if (knownEndPoints != null)
                lock (knownEndPoints)
                    for (LinkedListNode<ClrObject> node = result.First; node != null; node = node.Next)
                        if (node.Value.Address != target)
                            knownEndPoints[node.Value.Address] = node;

            return result;
        }

        private static ClrRoot GetHandleRoot(ClrHandle handle)
        {
            GCRootKind kind = GCRootKind.Strong;

            switch (handle.HandleType)
            {
                case HandleType.Pinned:
                    kind = GCRootKind.Pinning;
                    break;

                case HandleType.AsyncPinned:
                    kind = GCRootKind.AsyncPinning;
                    break;
            }

            return new HandleRoot(handle.Address, handle.Object, handle.Type, handle.HandleType, kind, handle.AppDomain);
        }

        internal static bool IsTooLarge(ulong obj, ClrType type, ClrSegment seg)
        {
            ulong size = type.GetSize(obj);
            if (!seg.IsLarge && size >= 85000)
                return true;

            return obj + size > seg.End;
        }

        [Conditional("GCROOTTRACE")]
        private static void TraceFullPath(LinkedList<PathEntry> path, LinkedListNode<ClrObject> foundEnding)
        {
            Debug.WriteLine($"FoundEnding: {string.Join(" ", path.Select(p => p.Object.ToString()))} {string.Join(" ", NodeToList(foundEnding))}");
        }

        private static List<string> NodeToList(LinkedListNode<ClrObject> tmp)
        {
            List<string> list = new List<string>();
            for (; tmp != null; tmp = tmp.Next)
                list.Add(tmp.Value.ToString());

            return list;
        }

        [Conditional("GCROOTTRACE")]
        private void TraceCurrent(ulong next, IEnumerable<ulong> refs)
        {
            Debug.WriteLine($"Considering: {new ClrObject(next, Heap.GetObjectType(next))} Refs:{string.Join(" ", refs)}");
        }

        [Conditional("GCROOTTRACE")]
        private static void TraceFullPath(string prefix, LinkedList<PathEntry> path)
        {
            if (!string.IsNullOrWhiteSpace(prefix))
                prefix += ": ";
            else
                prefix = "";

            Debug.WriteLine(prefix + string.Join(" ", path.Select(p => p.Object.ToString())));
        }

        private struct PathEntry
        {
            public ClrObject Object;
            public Stack<ClrObject> Todo;

            public override string ToString()
            {
                return Object.ToString();
            }
        }
    }
}