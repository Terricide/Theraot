﻿#if FAT

using System;
using System.Collections;
using System.Collections.Generic;
using Theraot.Collections.Specialized;
using Theraot.Threading;
using Theraot.Threading.Needles;

namespace Theraot.Collections.ThreadSafe
{
    [global::System.Diagnostics.DebuggerNonUserCode]
    [System.Diagnostics.DebuggerDisplay("Count={Count}")]
    public class WeakDictionary<TKey, TValue, TNeedle> : IDictionary<TKey, TValue>
        where TKey : class
        where TNeedle : WeakNeedle<TKey>
    {
        private readonly KeyCollection<TKey, TValue> _keyCollection;
        private readonly IEqualityComparer<TKey> _keyComparer;
        private readonly ValueCollection<TKey, TValue> _valueCollection;
        private readonly SafeDictionary<TNeedle, TValue> _wrapped;
        private StructNeedle<WeakNeedle<EventHandler>> _eventHandler;

        public WeakDictionary()
            : this(null, true)
        {
            //Empty
        }

        public WeakDictionary(IEqualityComparer<TKey> comparer)
            : this(comparer, true)
        {
            //Empty
        }

        public WeakDictionary(bool autoRemoveDeadItems)
            : this(null, autoRemoveDeadItems)
        {
            //Empty
        }

        public WeakDictionary(IEqualityComparer<TKey> comparer, bool autoRemoveDeadItems)
        {
            _keyComparer = comparer ?? EqualityComparer<TKey>.Default;
            var needleComparer = new NeedleConversionEqualityComparer<TNeedle, TKey>(_keyComparer);
            _wrapped = new SafeDictionary<TNeedle, TValue>(needleComparer);
            if (autoRemoveDeadItems)
            {
                RegisterForAutoRemoveDeadItemsExtracted();
            }
            else
            {
                GC.SuppressFinalize(this);
            }
            _keyCollection = new KeyCollection<TKey, TValue>(this);
            _valueCollection = new ValueCollection<TKey, TValue>(this);
        }

        public WeakDictionary(IEqualityComparer<TKey> comparer, int initialProbing)
            : this(comparer, true, initialProbing)
        {
            //Empty
        }

        public WeakDictionary(bool autoRemoveDeadItems, int initialProbing)
            : this(null, autoRemoveDeadItems, initialProbing)
        {
            //Empty
        }

        public WeakDictionary(IEqualityComparer<TKey> comparer, bool autoRemoveDeadItems, int initialProbing)
        {
            _keyComparer = comparer ?? EqualityComparer<TKey>.Default;
            var needleComparer = new NeedleConversionEqualityComparer<TNeedle, TKey>(_keyComparer);
            _wrapped = new SafeDictionary<TNeedle, TValue>(needleComparer, initialProbing);
            if (autoRemoveDeadItems)
            {
                RegisterForAutoRemoveDeadItemsExtracted();
            }
            else
            {
                GC.SuppressFinalize(this);
            }
            _keyCollection = new KeyCollection<TKey, TValue>(this);
            _valueCollection = new ValueCollection<TKey, TValue>(this);
        }

        ~WeakDictionary()
        {
            UnRegisterForAutoRemoveDeadItemsExtracted();
        }

        public bool AutoRemoveDeadItems
        {
            get
            {
                return _eventHandler.IsAlive;
            }
            set
            {
                if (value)
                {
                    RegisterForAutoRemoveDeadItems();
                }
                else
                {
                    UnRegisterForAutoRemoveDeadItems();
                }
            }
        }

        public int Count
        {
            get
            {
                return _wrapped.Count;
            }
        }

        public IEqualityComparer<TKey> KeyComparer
        {
            get
            {
                return _keyComparer;
            }
        }

        public ICollection<TKey> Keys
        {
            get
            {
                return _keyCollection;
            }
        }

        public ICollection<TValue> Values
        {
            get
            {
                return _valueCollection;
            }
        }

        protected SafeDictionary<TNeedle, TValue> Wrapped
        {
            get
            {
                return _wrapped;
            }
        }

        [global::System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes", Justification = "Returns false")]
        bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly
        {
            get
            {
                return false;
            }
        }
        public TValue this[TKey key]
        {
            get
            {
                TValue value;
                if (TryGetValue(key, out value))
                {
                    return value;
                }
                throw new KeyNotFoundException();
            }
            set
            {
                Set(key, value);
            }
        }

        /// <summary>
        /// Adds the specified key and associated value.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <exception cref="System.ArgumentException">An item with the same key has already been added</exception>
        public void AddNew(TKey key, TValue value)
        {
            var needle = NeedleHelper.CreateNeedle<TKey, TNeedle>(key);
            try
            {
                _wrapped.AddNew(needle, input => !input.IsAlive, value);
            }
            catch (ArgumentException)
            {
                needle.Dispose();
                throw;
            }
        }

        public TValue AddOrUpdate(TKey key, Func<TKey, TValue> addValueFactory, Func<TKey, TValue, TValue> updateValueFactory)
        {
            // TODO AddOrUpdate
            throw new NotImplementedException();
        }

        public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
        {
            // TODO AddOrUpdate
            throw new NotImplementedException();
        }

        /// <summary>
        /// Removes all the elements.
        /// </summary>
        public void Clear()
        {
            var displaced = _wrapped.ClearEnumerable();
            foreach (var item in displaced)
            {
                item.Key.Dispose();
            }
        }

        /// <summary>
        /// Removes all the elements.
        /// </summary>
        public IEnumerable<KeyValuePair<TKey, TValue>> ClearEnumerable()
        {
            var displaced = _wrapped.ClearEnumerable();
            foreach (var item in displaced)
            {
                yield return new KeyValuePair<TKey, TValue>(item.Key.Value, item.Value);
                item.Key.Dispose();
            }
        }

        /// <summary>
        /// Determines whether the specified key is contained.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>
        ///   <c>true</c> if the specified key is contained; otherwise, <c>false</c>.
        /// </returns>
        public bool ContainsKey(TKey key)
        {
            return _wrapped.ContainsKey
                (
                    _keyComparer.GetHashCode(key),
                    input =>
                    {
                        TKey _key;
                        if (input.TryGetValue(out _key))
                        {
                            return _keyComparer.Equals(_key, key);
                        }
                        return false;
                    }
                );
        }

        /// <summary>
        /// Determines whether the specified key is contained.
        /// </summary>
        /// <param name="hashCode">The hash code to look for.</param>
        /// <param name="keyCheck">The key predicate.</param>
        /// <returns>
        ///   <c>true</c> if the specified key is contained; otherwise, <c>false</c>.
        /// </returns>
        public bool ContainsKey(int hashCode, Predicate<TKey> keyCheck)
        {
            return _wrapped.ContainsKey
                (
                    hashCode,
                    input =>
                    {
                        TKey _key;
                        if (input.TryGetValue(out _key))
                        {
                            return keyCheck(_key);
                        }
                        return false;
                    }
                );
        }

        /// <summary>
        /// Determines whether the specified key is contained.
        /// </summary>
        /// <param name="hashCode">The hash code to look for.</param>
        /// <param name="keyCheck">The key predicate.</param>
        /// <param name="valueCheck">The value predicate.</param>
        /// <returns>
        ///   <c>true</c> if the specified key is contained; otherwise, <c>false</c>.
        /// </returns>
        public bool ContainsKey(int hashCode, Predicate<TKey> keyCheck, Predicate<TValue> valueCheck)
        {
            return _wrapped.ContainsKey
                (
                    hashCode,
                    input =>
                    {
                        TKey _key;
                        if (input.TryGetValue(out _key))
                        {
                            return keyCheck(_key);
                        }
                        return false;
                    },
                    valueCheck
                );
        }

        /// <summary>
        /// Copies the items to a compatible one-dimensional array, starting at the specified index of the target array.
        /// </summary>
        /// <param name="array">The array.</param>
        /// <param name="arrayIndex">Index of the array.</param>
        /// <exception cref="System.ArgumentNullException">array</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">arrayIndex;Non-negative number is required.</exception>
        /// <exception cref="System.ArgumentException">array;The array can not contain the number of elements.</exception>
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            if (array == null)
            {
                throw new ArgumentNullException("array");
            }
            if (arrayIndex < 0)
            {
                throw new ArgumentOutOfRangeException("arrayIndex", "Non-negative number is required.");
            }
            if (_wrapped.Count > array.Length - arrayIndex)
            {
                throw new ArgumentException("The array can not contain the number of elements.", "array");
            }
            GetPairs().CopyTo(array, arrayIndex);
        }

        /// <summary>
        /// Returns an <see cref="System.Collections.Generic.IEnumerator{T}" /> that allows to iterate through the collection.
        /// </summary>
        /// <returns>
        /// An <see cref="System.Collections.Generic.IEnumerator{T}" /> object that can be used to iterate through the collection.
        /// </returns>
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            foreach (var pair in _wrapped)
            {
                TKey _key;
                if (pair.Key.TryGetValue(out _key))
                {
                    yield return new KeyValuePair<TKey, TValue>(_key, pair.Value);
                }
            }
        }

        public TValue GetOrAdd(TKey key, TValue value)
        {
            var needle = NeedleHelper.CreateNeedle<TKey, TNeedle>(key);
            return _wrapped.GetOrAdd(needle, input => !input.IsAlive, value);
        }

        public TValue GetOrAdd(TKey key, Func<TValue> valueFactory)
        {
            var needle = NeedleHelper.CreateNeedle<TKey, TNeedle>(key);
            return _wrapped.GetOrAdd(needle, input => !input.IsAlive, valueFactory);
        }

        /// <summary>
        /// Gets the pairs contained in this object.
        /// </summary>
        public IList<KeyValuePair<TKey, TValue>> GetPairs()
        {
            var result = new List<KeyValuePair<TKey, TValue>>(_wrapped.Count);
            foreach (var pair in _wrapped)
            {
                result.Add(new KeyValuePair<TKey, TValue>(pair.Key.Value, pair.Value));
            }
            return result;
        }

        /// <summary>
        /// Removes the specified key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>
        ///   <c>true</c> if the specified key was removed; otherwise, <c>false</c>.
        /// </returns>
        public bool Remove(TKey key)
        {
            TValue value;
            return _wrapped.Remove
                (
                    _keyComparer.GetHashCode(key),
                    input =>
                    {
                        TKey _key;
                        if (input.TryGetValue(out _key))
                        {
                            return _keyComparer.Equals(_key, key);
                        }
                        return false;
                    },
                    out value
                );
        }

        /// <summary>
        /// Removes the specified key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns>
        ///   <c>true</c> if the specified key was removed; otherwise, <c>false</c>.
        /// </returns>
        public bool Remove(TKey key, out TValue value)
        {
            return _wrapped.Remove
                (
                    _keyComparer.GetHashCode(key),
                    input =>
                    {
                        TKey _key;
                        if (input.TryGetValue(out _key))
                        {
                            return _keyComparer.Equals(_key, key);
                        }
                        return false;
                    },
                    out value
                );
        }

        /// <summary>
        /// Removes a key by hash code and a key predicate.
        /// </summary>
        /// <param name="hashCode">The hash code to look for.</param>
        /// <param name="keyCheck">The key predicate.</param>
        /// <param name="value">The value.</param>
        /// <returns>
        ///   <c>true</c> if the specified key was removed; otherwise, <c>false</c>.
        /// </returns>
        public bool Remove(int hashCode, Predicate<TKey> keyCheck, out TValue value)
        {
            return _wrapped.Remove
                (
                    hashCode,
                    input =>
                    {
                        TKey _key;
                        if (input.TryGetValue(out _key))
                        {
                            return keyCheck.Invoke(_key);
                        }
                        return false;
                    },
                    out value
                );
        }

        /// <summary>
        /// Removes a key by hash code, key predicate and value predicate.
        /// </summary>
        /// <param name="hashCode">The hash code to look for.</param>
        /// <param name="keyCheck">The key predicate.</param>
        /// <param name="valueCheck">The value predicate.</param>
        /// <param name="value">The value.</param>
        /// <returns>
        ///   <c>true</c> if the specified key was removed; otherwise, <c>false</c>.
        /// </returns>
        public bool Remove(int hashCode, Predicate<TKey> keyCheck, Predicate<TValue> valueCheck, out TValue value)
        {
            return _wrapped.Remove
                (
                    hashCode,
                    input =>
                    {
                        TKey _key;
                        if (input.TryGetValue(out _key))
                        {
                            return keyCheck.Invoke(_key);
                        }
                        return false;
                    },
                    valueCheck,
                    out value
                );
        }

        public int RemoveDeadItems()
        {
            return _wrapped.RemoveWhereKey(key => !key.IsAlive);
        }

        /// <summary>
        /// Removes the keys and associated values where the key satisfies the predicate.
        /// </summary>
        /// <param name="keyCheck">The predicate.</param>
        /// <returns>
        /// The number or removed pairs of keys and associated values.
        /// </returns>
        /// <remarks>
        /// It is not guaranteed that all the pairs of keys and associated values that satisfies the predicate will be removed.
        /// </remarks>
        public int RemoveWhereKey(Predicate<TKey> keyCheck)
        {
            return _wrapped.RemoveWhereKey
                (
                    input =>
                    {
                        TKey _key;
                        if (input.TryGetValue(out _key))
                        {
                            return keyCheck.Invoke(_key);
                        }
                        return false;
                    }
                );
        }

        /// <summary>
        /// Removes the keys and associated values where the key satisfies the predicate.
        /// </summary>
        /// <param name="keyCheck">The predicate.</param>
        /// <returns>
        /// An <see cref="IEnumerable{TValue}" /> that allows to iterate over the values of the removed pairs.
        /// </returns>
        /// <remarks>
        /// It is not guaranteed that all the pairs of keys and associated values that satisfies the predicate will be removed.
        /// </remarks>
        public IEnumerable<TValue> RemoveWhereKeyEnumerable(Predicate<TKey> keyCheck)
        {
            return _wrapped.RemoveWhereKeyEnumerable
                (
                    input =>
                    {
                        TKey _key;
                        if (input.TryGetValue(out _key))
                        {
                            return keyCheck.Invoke(_key);
                        }
                        return false;
                    }
                );
        }

        /// <summary>
        /// Removes the keys and associated values where the value satisfies the predicate.
        /// </summary>
        /// <param name="valueCheck">The predicate.</param>
        /// <returns>
        /// The number or removed pairs of keys and associated values.
        /// </returns>
        /// <remarks>
        /// It is not guaranteed that all the pairs of keys and associated values that satisfies the predicate will be removed.
        /// </remarks>
        public int RemoveWhereValue(Predicate<TValue> valueCheck)
        {
            return _wrapped.RemoveWhereValue(valueCheck);
        }

        /// <summary>
        /// Removes the keys and associated values where the value satisfies the predicate.
        /// </summary>
        /// <param name="valueCheck">The predicate.</param>
        /// <returns>
        /// An <see cref="IEnumerable{TValue}" /> that allows to iterate over the values of the removed pairs.
        /// </returns>
        /// <remarks>
        /// It is not guaranteed that all the pairs of keys and associated values that satisfies the predicate will be removed.
        /// </remarks>
        public IEnumerable<TValue> RemoveWhereValueEnumerable(Predicate<TValue> valueCheck)
        {
            return _wrapped.RemoveWhereValueEnumerable(valueCheck);
        }

        /// <summary>
        /// Sets the value associated with the specified key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        public void Set(TKey key, TValue value)
        {
            var needle = NeedleHelper.CreateNeedle<TKey, TNeedle>(key);
            _wrapped.Set(needle, input => !input.IsAlive, value);
        }

        /// <summary>
        /// Sets the value associated with the specified key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="isNew">if set to <c>true</c> the item value was set.</param>
        public void Set(TKey key, TValue value, out bool isNew)
        {
            var needle = NeedleHelper.CreateNeedle<TKey, TNeedle>(key);
            _wrapped.Set(needle, input => !input.IsAlive, value, out isNew);
        }

        /// <summary>
        /// Attempts to add the specified key and associated value. The value is added if the key is not found.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns>
        ///   <c>true</c> if the specified key and associated value were added; otherwise, <c>false</c>.
        /// </returns>
        public bool TryAdd(TKey key, TValue value)
        {
            var needle = NeedleHelper.CreateNeedle<TKey, TNeedle>(key);
            if (_wrapped.TryAdd(needle, input => !input.IsAlive, value))
            {
                return true;
            }
            needle.Dispose();
            return false;
        }

        /// <summary>
        /// Attempts to add the specified key and associated value. The value is added if the key is not found.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="stored">The stored pair independently of success.</param>
        /// <returns>
        ///   <c>true</c> if the specified key and associated value were added; otherwise, <c>false</c>.
        /// </returns>
        public bool TryAdd(TKey key, TValue value, out KeyValuePair<TKey, TValue> stored)
        {
            var needle = NeedleHelper.CreateNeedle<TKey, TNeedle>(key);
            KeyValuePair<TNeedle, TValue> _stored;
            var result = _wrapped.TryAdd(needle, input => !input.IsAlive, value, out _stored);
            if (!result)
            {
                needle.Dispose();
            }
            stored = new KeyValuePair<TKey, TValue>(_stored.Key.Value, _stored.Value);
            return result;
        }

        /// <summary>
        /// Tries to retrieve the value associated with the specified key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns>
        ///   <c>true</c> if the value was retrieved; otherwise, <c>false</c>.
        /// </returns>
        public bool TryGetValue(TKey key, out TValue value)
        {
            return _wrapped.TryGetValue(_keyComparer.GetHashCode(key), input => Equals(key, input.Value), out value);
        }

        /// <summary>
        /// Tries to retrieve the value by hash code and key predicate.
        /// </summary>
        /// <param name="hashCode">The hash code to look for.</param>
        /// <param name="keyCheck">The key predicate.</param>
        /// <param name="value">The value.</param>
        /// <returns>
        ///   <c>true</c> if the value was retrieved; otherwise, <c>false</c>.
        /// </returns>
        public bool TryGetValue(int hashCode, Predicate<TKey> keyCheck, out TValue value)
        {
            return _wrapped.TryGetValue(hashCode, input => keyCheck(input.Value), out value);
        }

        /// <summary>
        /// Returns the values where the key satisfies the predicate.
        /// </summary>
        /// <param name="keyCheck">The predicate.</param>
        /// <returns>
        /// An <see cref="IEnumerable{TValue}" /> that allows to iterate over the values of the matched pairs.
        /// </returns>
        /// <remarks>
        /// It is not guaranteed that all the pairs of keys and associated values that satisfies the predicate will be returned.
        /// </remarks>
        public IEnumerable<TValue> Where(Predicate<TKey> keyCheck)
        {
            var matches = _wrapped.Where
                (
                    input =>
                    {
                        TKey key;
                        if (input.TryGetValue(out key))
                        {
                            return keyCheck(key);
                        }
                        return false;
                    }
                );
            foreach (var value in matches)
            {
                yield return value;
            }
        }

        /// <summary>
        /// Adds the specified key and associated value.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="keyOverwriteCheck">The key predicate to approve overwriting.</param>
        /// <param name="value">The value.</param>
        /// <exception cref="System.ArgumentException">An item with the same key has already been added</exception>
        internal void AddNew(TKey key, Predicate<TKey> keyOverwriteCheck, TValue value)
        {
            var needle = NeedleHelper.CreateNeedle<TKey, TNeedle>(key);
            try
            {
                _wrapped.AddNew
                    (
                        needle,
                        input =>
                        {
                            TKey _key;
                            if (input.TryGetValue(out _key))
                            {
                                return keyOverwriteCheck(_key);
                            }
                            return true;
                        },
                        value
                    );
            }
            catch (ArgumentException)
            {
                needle.Dispose();
                throw;
            }
        }

        internal TValue GetOrAdd(TKey key, Predicate<TKey> keyOverwriteCheck, TValue value)
        {
            var needle = NeedleHelper.CreateNeedle<TKey, TNeedle>(key);
            return _wrapped.GetOrAdd
                (
                    needle,
                    input =>
                    {
                        TKey _key;
                        if (input.TryGetValue(out _key))
                        {
                            return keyOverwriteCheck(_key);
                        }
                        return true;
                    },
                    value
                );
        }

        internal TValue GetOrAdd(TKey key, Predicate<TKey> keyOverwriteCheck, Func<TValue> valueFactory)
        {
            var needle = NeedleHelper.CreateNeedle<TKey, TNeedle>(key);
            return _wrapped.GetOrAdd
                (
                    needle,
                    input =>
                    {
                        TKey _key;
                        if (input.TryGetValue(out _key))
                        {
                            return keyOverwriteCheck(_key);
                        }
                        return true;
                    },
                    valueFactory
                );
        }

        /// <summary>
        /// Attempts to add the specified key and associated value. The value is added if the key is not found.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="keyOverwriteCheck">The key predicate to approve overwriting.</param>
        /// <param name="value">The value.</param>
        /// <returns>
        ///   <c>true</c> if the specified key and associated value were added; otherwise, <c>false</c>.
        /// </returns>
        internal bool TryAdd(TKey key, Predicate<TKey> keyOverwriteCheck, TValue value)
        {
            var needle = NeedleHelper.CreateNeedle<TKey, TNeedle>(key);
            if
                (
                    _wrapped.TryAdd
                    (
                        needle,
                        input =>
                        {
                            TKey _key;
                            if (input.TryGetValue(out _key))
                            {
                                return keyOverwriteCheck(_key);
                            }
                            return true;
                        },
                        value
                    )
                )
            {
                return true;
            }
            needle.Dispose();
            return false;
        }

        /// <summary>
        /// Attempts to add the specified key and associated value. The value is added if the key is not found.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="keyOverwriteCheck">The key predicate to approve overwriting.</param>
        /// <param name="value">The value.</param>
        /// <param name="stored">The stored pair independently of success.</param>
        /// <returns>
        ///   <c>true</c> if the specified key and associated value were added; otherwise, <c>false</c>.
        /// </returns>
        internal bool TryAdd(TKey key, Predicate<TKey> keyOverwriteCheck, TValue value, out KeyValuePair<TKey, TValue> stored)
        {
            var needle = NeedleHelper.CreateNeedle<TKey, TNeedle>(key);
            KeyValuePair<TNeedle, TValue> _stored;
            var result = _wrapped.TryAdd
                (
                    needle,
                    input =>
                    {
                        TKey _key;
                        if (input.TryGetValue(out _key))
                        {
                            return keyOverwriteCheck(_key);
                        }
                        return true;
                    },
                    value,
                    out _stored
                );
            if (!result)
            {
                needle.Dispose();
            }
            stored = new KeyValuePair<TKey, TValue>(_stored.Key.Value, _stored.Value);
            return result;
        }

        protected bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return ((ICollection<KeyValuePair<TKey, TValue>>)this).Contains(item);
        }

        [global::System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes", Justification = "Use AddNew")]
        void IDictionary<TKey, TValue>.Add(TKey key, TValue value)
        {
            AddNew(key, value);
        }

        [global::System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes", Justification = "Use AddNew")]
        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item)
        {
            AddNew(item.Key, item.Value);
        }

        bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item)
        {
            return _wrapped.ContainsKey
                (
                    _keyComparer.GetHashCode(item.Key),
                    input =>
                    {
                        TKey _key;
                        if (input.TryGetValue(out _key))
                        {
                            return _keyComparer.Equals(_key, item.Key);
                        }
                        return false;
                    },
                    input => EqualityComparer<TValue>.Default.Equals(input, item.Value)
                );
        }

        private void GarbageCollected(object sender, EventArgs e)
        {
            RemoveDeadItems();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        private void RegisterForAutoRemoveDeadItems()
        {
            if (RegisterForAutoRemoveDeadItemsExtracted())
            {
                GC.ReRegisterForFinalize(this);
            }
        }

        private bool RegisterForAutoRemoveDeadItemsExtracted()
        {
            bool result = false;
            EventHandler eventHandler;
            if (ReferenceEquals(_eventHandler.Value, null))
            {
                eventHandler = GarbageCollected;
                _eventHandler = new WeakNeedle<EventHandler>(eventHandler);
                result = true;
            }
            else
            {
                eventHandler = _eventHandler.Value.Value;
                if (!_eventHandler.IsAlive)
                {
                    eventHandler = GarbageCollected;
                    _eventHandler.Value = eventHandler;
                    result = true;
                }
            }
            GCMonitor.Collected += eventHandler;
            return result;
        }

        bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item)
        {
            TValue found;
            return _wrapped.Remove
                (
                    _keyComparer.GetHashCode(item.Key),
                    input =>
                    {
                        TKey _key;
                        if (input.TryGetValue(out _key))
                        {
                            return _keyComparer.Equals(_key, item.Key);
                        }
                        return false;
                    },
                    input => EqualityComparer<TValue>.Default.Equals(input, item.Value),
                    out found
                );
        }
        private void UnRegisterForAutoRemoveDeadItems()
        {
            if (UnRegisterForAutoRemoveDeadItemsExtracted())
            {
                GC.SuppressFinalize(this);
            }
        }

        private bool UnRegisterForAutoRemoveDeadItemsExtracted()
        {
            EventHandler eventHandler;
            if (_eventHandler.Value.Retrieve(out eventHandler))
            {
                GCMonitor.Collected -= eventHandler;
                _eventHandler.Value = null;
                return true;
            }
            _eventHandler.Value = null;
            return false;
        }
    }
}

#endif