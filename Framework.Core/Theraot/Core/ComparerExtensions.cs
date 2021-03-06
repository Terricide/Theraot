﻿// Needed for Workaround

#pragma warning disable S2234 // Parameters should be passed in the correct order

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Theraot.Core
{
    public static class ComparerExtensions
    {
        public static IComparer<T> Reverse<T>(this IComparer<T> comparer)
        {
            return !(comparer is ReverseComparer<T> originalAsReverse) ? new ReverseComparer<T>(comparer ?? Comparer<T>.Default) : originalAsReverse.Wrapped;
        }

        private sealed class ReverseComparer<T> : IComparer<T>
        {
            public ReverseComparer(IComparer<T> wrapped)
            {
                Wrapped = wrapped ?? throw new ArgumentNullException(nameof(wrapped));
            }

            internal IComparer<T> Wrapped { get; }

            public int Compare([AllowNull] T x, [AllowNull] T y)
            {
                return Wrapped.Compare(y!, x!);
            }
        }
    }
}