using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Rinha;

public sealed class NoBlockingList<T>
{
    private const int bufferSize = 1024;
    private readonly T[] items = new T[bufferSize];

    /// <summary>
    /// 32 bytes:
    /// |  |- lower bits: item index --|--------------- empty ------------|- higher bits: last index -|
    /// 00 01 02 03 04 05 06 07 08 09 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29 30 31
    /// 00 01 01 01 01 01 01 01 01 01 01 00 00 00 00 00 00 00 00 00 00 00 01 01 01 01 01 01 01 01 01 01
    /// </summary>
    private int indexAndCount;

    private (int newItemIndex, Range range1, Range range2, bool shouldReturnNewList) GetPositionAndLength(int maximumCount)
    {
        // last item index is where the last item was collected + 1, meaning the next item of the one that was collected last time
        // it will be the first one to be collected
        int lastItemIndex;
        // current item is the position where we will store the new item, now
        int currentItemIndex;
        int initialValue, newIndexAndCount;
        Range range1, range2;
        bool shouldReturnNewList;
        bool didNotWrapAround;
        do
        {
            initialValue = indexAndCount;
            currentItemIndex = GetCurrentItemIndex(initialValue);
            lastItemIndex = GetLastItemIndex(initialValue);
            didNotWrapAround = currentItemIndex >= lastItemIndex;
            var itemsQuantity = didNotWrapAround ? currentItemIndex - lastItemIndex + 1 : bufferSize - lastItemIndex + currentItemIndex + 1;
            shouldReturnNewList = itemsQuantity >= maximumCount;
            // next item index is where we will store the item that will be added next time (as in: not now), for the next time an item is added
            var nextItemIndex = currentItemIndex + 1;
            if (nextItemIndex == bufferSize)
                nextItemIndex = 0;
            newIndexAndCount = GetIndexAndCount(nextItemIndex, shouldReturnNewList ? nextItemIndex : lastItemIndex);
        } while (Interlocked.CompareExchange(ref indexAndCount, newIndexAndCount, initialValue) != initialValue);

        if (didNotWrapAround)
        {
            range1 = !shouldReturnNewList ? default : new Range(lastItemIndex, currentItemIndex + 1);
            range2 = default;
        }
        else
        {
            if (!shouldReturnNewList)
            {
                range1 = default;
                range2 = default;
            }
            else
            {
                range1 = new Range(lastItemIndex, bufferSize);
                range2 = new Range(0, currentItemIndex + 1);
            }
        }

        return (currentItemIndex, range1, range2, shouldReturnNewList);
    }

    private (Range range1, Range range2) GetAll()
    {
        int initialValue, newIndexAndCount, lastItemIndex;
        // current item index is where the next item would be stored, and it could be null or invalid, we need to get just before it
        int currentItemIndex;
        Range range1, range2;
        do
        {
            initialValue = indexAndCount;
            currentItemIndex = GetCurrentItemIndex(initialValue);
            lastItemIndex = GetLastItemIndex(initialValue);
            if (currentItemIndex == lastItemIndex)
                break;
            newIndexAndCount = GetIndexAndCount(currentItemIndex, currentItemIndex);
        } while (Interlocked.CompareExchange(ref indexAndCount, newIndexAndCount, initialValue) != initialValue);

        if (currentItemIndex > lastItemIndex)
        { // did not wrap around
            range1 = new Range(lastItemIndex, currentItemIndex);
            range2 = default;
        }
        else if (currentItemIndex == lastItemIndex)
        { // no items
            range1 = default;
            range2 = default;
        }
        else
        { // wrapped around
            range1 = new Range(lastItemIndex, bufferSize);
            range2 = currentItemIndex == 0 ? default : new Range(0, currentItemIndex);
        }
        return (range1, range2);
    }

    public bool AddAndTryGet(T item, int maximumCount, [NotNullWhen(true)] out T[]? buffer)
    {
        var (newItemIndex, range1, range2, shouldReturnNewList) = GetPositionAndLength(maximumCount);
        items[newItemIndex] = item;
        if (shouldReturnNewList)
        {
            buffer = GetArrayFromRanges(range1, range2);
            Debug.Assert(buffer.Length == maximumCount);
            return true;
        }
        buffer = null;
        return false;
    }

    private T[] GetArrayFromRanges(Range range1, Range range2)
    {
        T[]? buffer;
        ReadOnlySpan<T> itemsSpan = items;
        var span1 = itemsSpan[range1];
        var count1 = range1.End.Value - range1.Start.Value;
        var count2 = range2.End.Value - range2.Start.Value;
        var count = count1 + count2;
        buffer = new T[count];
        span1.CopyTo(buffer);
        if (count2 > 0)
        {
            var span2 = itemsSpan[range2];
            span2.CopyTo(buffer.AsSpan()[count1..]);
        }

        return buffer;
    }

    public bool TryGetAll([NotNullWhen(true)] out T[]? buffer)
    {
        var (range1, range2) = GetAll();
        var myBuffer = GetArrayFromRanges(range1, range2);
        if (myBuffer.Length > 0)
        {
            buffer = myBuffer;
            return true;
        }
        buffer = null;
        return false;
    }

    public int CurrentItemIndex => GetCurrentItemIndex(indexAndCount);
    public int LastItemIndex => GetLastItemIndex(indexAndCount);

    public T LastItem => items[LastItemIndex];
    public T CurrentItem => items[CurrentItemIndex];
    public Span<T> CurrentItems => items.AsSpan()[LastItemIndex..CurrentItemIndex];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetCurrentItemIndex(int myIndexAndCount) => (myIndexAndCount & 0b0111_1111_1110_0000_0000_0000_0000_0000) >> 21;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetLastItemIndex(int myIndexAndCount) => myIndexAndCount & 0b11_1111_1111;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetIndexAndCount(int currentItemIndex, int lastItemIndex) => (currentItemIndex << 21) + lastItemIndex;
}
