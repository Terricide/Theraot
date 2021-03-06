﻿#if LESSTHAN_NET40 || NETSTANDARD1_0

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Theraot.Collections;
using Theraot.Collections.ThreadSafe;

namespace System.Collections.Concurrent
{
    [Serializable]
    [ComVisible(false)]
    [DebuggerDisplay("Count = {" + nameof(Count) + "}")]
    public class ConcurrentBag<T> : IProducerConsumerCollection<T>, IReadOnlyCollection<T>
    {
        private ThreadSafeQueue<T> _wrapped;

        public ConcurrentBag()
        {
            _wrapped = new ThreadSafeQueue<T>();
        }

        public ConcurrentBag(IEnumerable<T> collection)
        {
            if (collection == null)
            {
                throw new ArgumentNullException(nameof(collection));
            }

            _wrapped = new ThreadSafeQueue<T>(collection);
        }

        public int Count => _wrapped.Count;

        public bool IsEmpty => Count == 0;

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => throw new NotSupportedException();

        public void Add(T item)
        {
            _wrapped.Add(item);
        }

        public void Clear()
        {
            _wrapped = new ThreadSafeQueue<T>();
        }

        public void CopyTo(T[] array, int index)
        {
            _wrapped.CopyTo(array, index);
        }

        void ICollection.CopyTo(Array array, int index)
        {
            Extensions.CanCopyTo(Count, array, index);
            _wrapped.DeprecatedCopyTo(array, index);
        }

        public IEnumerator<T> GetEnumerator()
        {
            return _wrapped.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public T[] ToArray()
        {
            return _wrapped.ToArray();
        }

        bool IProducerConsumerCollection<T>.TryAdd(T item)
        {
            Add(item);
            return true;
        }

        public bool TryPeek(out T result)
        {
            return _wrapped.TryPeek(out result);
        }

        public bool TryTake(out T item)
        {
            return _wrapped.TryTake(out item);
        }
    }
}

#endif