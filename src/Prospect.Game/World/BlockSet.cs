using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// A set of block ids of one design: one bit per block in 64-bit words, <see cref="WordCount"/> of them, block
/// <c>b</c> in bit <c>b &amp; 63</c> of word <c>b &gt;&gt; 6</c>. The words belong to whoever holds the set (a
/// design, the block state store, a snapshot, working memory); these are the operations on them. Bits past the
/// design's block count are never set, so two sets of one design are equal exactly when their words are.
/// Nothing here allocates.
/// </summary>
public static class BlockSet
{
    /// <summary>The words a set of this many blocks takes.</summary>
    public static int WordCount(int blocks) => (blocks + 63) >> 6;

    /// <summary>True if the block is in the set.</summary>
    public static bool Has(ReadOnlySpan<ulong> set, int block) => (set[block >> 6] & 1UL << (block & 63)) != 0;

    /// <summary>Puts a block in the set.</summary>
    public static void Add(Span<ulong> set, int block) => set[block >> 6] |= 1UL << (block & 63);

    /// <summary>Takes a block out of the set.</summary>
    public static void Remove(Span<ulong> set, int block) => set[block >> 6] &= ~(1UL << (block & 63));

    /// <summary>How many blocks are in the set.</summary>
    public static int Count(ReadOnlySpan<ulong> set)
    {
        var count = 0;
        foreach (var word in set)
            count += BitOperations.PopCount(word);
        return count;
    }

    /// <summary>True if the set holds any block.</summary>
    public static bool Any(ReadOnlySpan<ulong> set)
    {
        foreach (var word in set)
        {
            if (word != 0)
                return true;
        }

        return false;
    }

    /// <summary>Makes the set every block of a design of <paramref name="blocks"/> blocks, and nothing past them.</summary>
    public static void Fill(Span<ulong> set, int blocks)
    {
        var whole = blocks >> 6;
        set[..whole].Fill(ulong.MaxValue);
        set[whole..].Clear();
        if ((blocks & 63) != 0)
            set[whole] = (1UL << (blocks & 63)) - 1;
    }

    /// <summary>True if the set is every block of a design of <paramref name="blocks"/> blocks: the intact hull.</summary>
    public static bool IsFull(ReadOnlySpan<ulong> set, int blocks)
    {
        var whole = blocks >> 6;
        for (var i = 0; i < whole; i++)
        {
            if (set[i] != ulong.MaxValue)
                return false;
        }

        return (blocks & 63) == 0 || set[whole] == (1UL << (blocks & 63)) - 1;
    }

    /// <summary>Copies a set onto another of the same design.</summary>
    public static void Copy(ReadOnlySpan<ulong> from, Span<ulong> to) => from.CopyTo(to);

    /// <summary>True if two sets of one design hold the same blocks.</summary>
    public static bool Equal(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b) => a.SequenceEqual(b);

    /// <summary>True if every block of <paramref name="all"/> is in <paramref name="set"/>; both of one design.</summary>
    public static bool HasAll(ReadOnlySpan<ulong> set, ReadOnlySpan<ulong> all)
    {
        for (var i = 0; i < all.Length; i++)
        {
            if ((set[i] & all[i]) != all[i])
                return false;
        }

        return true;
    }

    /// <summary>The blocks of a set from the lowest id up, for a <c>foreach</c>.</summary>
    public static Enumerator Each(ReadOnlySpan<ulong> set) => new(set);

    /// <summary>
    /// Walks the blocks of a set in rising order. A word is read when the walk reaches it, so a block taken out
    /// of the word being walked is still visited; nothing the walks here do depends on that.
    /// </summary>
    public ref struct Enumerator
    {
        private readonly ReadOnlySpan<ulong> _words;
        private ulong _bits;
        private int _word;

        internal Enumerator(ReadOnlySpan<ulong> words)
        {
            _words = words;
            _word = -1;
            _bits = 0;
            Current = -1;
        }

        /// <summary>The block the walk is at.</summary>
        public int Current { get; private set; }

        /// <summary>The walk itself, so a <c>foreach</c> can take it.</summary>
        public readonly Enumerator GetEnumerator() => this;

        /// <summary>Moves to the next block of the set; false when there is none.</summary>
        public bool MoveNext()
        {
            while (_bits == 0)
            {
                if (++_word >= _words.Length)
                    return false;
                _bits = _words[_word];
            }

            Current = (_word << 6) + BitOperations.TrailingZeroCount(_bits);
            _bits &= _bits - 1;
            return true;
        }
    }
}
