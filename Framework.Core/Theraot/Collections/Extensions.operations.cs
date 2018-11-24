// Needed for NET40

using System;
using System.Collections.Generic;

namespace Theraot.Collections
{
    public static partial class Extensions
    {
        public static ICollection<T> AsCollection<T>(IEnumerable<T> source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (source is string && typeof(T) == typeof(char))
            {
                return (ICollection<T>)(object)(source as string).ToCharArray();
            }
            var result = source as ICollection<T>;
            return result ?? new ProgressiveCollection<T>(source);
        }

        public static ICollection<T> AsDistinctCollection<T>(IEnumerable<T> source)
        {
#if NET35
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            // Workaround for .NET 3.5 when all you want is Contains and no duplicates
            var resultHashSet = source as HashSet<T>;
            if (resultHashSet == null)
            {
                // Remember that On .NET 3.5 HashSet is not an ISet
                var resultISet = source as ISet<T>;
                return resultISet ?? new ProgressiveSet<T>(source);
            }
            return resultHashSet;
#else
            return AsSet(source);
#endif
        }

        public static IList<T> AsList<T>(IEnumerable<T> source)
        {
            if (!(source is IList<T> result))
            {
                return new ProgressiveList<T>(source);
            }
            return result;
        }

        public static ISet<T> AsSet<T>(IEnumerable<T> source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            // Remember that On .NET 3.5 HashSet is not an ISet
            var resultISet = source as ISet<T>;
            return resultISet ?? new ProgressiveSet<T>(source);
        }

        public static IEnumerable<T> AsUnaryEnumerable<T>(this T source)
        {
            yield return source;
        }

        public static IList<T> AsUnaryList<T>(this T source)
        {
            return new ProgressiveList<T>
                   (
                       AsUnaryEnumerable(source)
                   );
        }

        public static ISet<T> AsUnarySet<T>(this T source)
        {
            return new ProgressiveSet<T>
                   (
                       AsUnaryEnumerable(source)
                   );
        }

        public static bool HasAtLeast<TSource>(this IEnumerable<TSource> source, int count)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (count == 0)
            {
                return true;
            }
            if (source is string && typeof(TSource) == typeof(char))
            {
                return (source as string).Length >= count;
            }
            if (source is ICollection<TSource> sourceAsCollection)
            {
                return sourceAsCollection.Count >= count;
            }
            var result = 0;
            using (var item = source.GetEnumerator())
            {
                while (item.MoveNext())
                {
                    checked
                    {
                        result++;
                    }
                    if (result == count)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static IEnumerable<T> SkipItems<T>(this IEnumerable<T> source, int skipCount)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            return SkipItemsExtracted(source, skipCount);
        }

        public static IEnumerable<T> SkipItems<T>(this IEnumerable<T> source, Predicate<T> predicateCount, int skipCount)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            return predicateCount == null ? SkipItemsExtracted(source, skipCount) : SkipItemsExtracted(source, predicateCount, skipCount);
        }

        public static IEnumerable<T> StepItems<T>(this IEnumerable<T> source, int stepCount)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            return StepItemsExtracted();

            IEnumerable<T> StepItemsExtracted()
            {
                var count = 0;
                foreach (var item in source)
                {
                    if (count % stepCount == 0)
                    {
                        count++;
                    }
                    else
                    {
                        yield return item;
                        count++;
                    }
                }
            }
        }

        public static IEnumerable<T> TakeItems<T>(this IEnumerable<T> source, int takeCount)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            return TakeItemsExtracted(source, takeCount);
        }

        public static IEnumerable<T> TakeItems<T>(this IEnumerable<T> source, Predicate<T> predicateCount, int takeCount)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            return predicateCount == null ? TakeItemsExtracted(source, takeCount) : TakeItemsExtracted(source, predicateCount, takeCount);
        }

        private static IEnumerable<T> SkipItemsExtracted<T>(IEnumerable<T> source, int skipCount)
        {
            var count = 0;
            foreach (var item in source)
            {
                if (count < skipCount)
                {
                    count++;
                }
                else
                {
                    yield return item;
                }
            }
        }

        private static IEnumerable<T> SkipItemsExtracted<T>(IEnumerable<T> source, Predicate<T> predicateCount, int skipCount)
        {
            var count = 0;
            foreach (var item in source)
            {
                if (count < skipCount)
                {
                    if (predicateCount(item))
                    {
                        count++;
                    }
                }
                else
                {
                    yield return item;
                }
            }
        }

        private static IEnumerable<T> TakeItemsExtracted<T>(IEnumerable<T> source, int takeCount)
        {
            var count = 0;
            foreach (var item in source)
            {
                if (count == takeCount)
                {
                    break;
                }
                yield return item;
                count++;
            }
        }

        private static IEnumerable<T> TakeItemsExtracted<T>(IEnumerable<T> source, Predicate<T> predicateCount, int takeCount)
        {
            var count = 0;
            foreach (var item in source)
            {
                if (count == takeCount)
                {
                    break;
                }
                yield return item;
                if (predicateCount(item))
                {
                    count++;
                }
            }
        }
    }
}