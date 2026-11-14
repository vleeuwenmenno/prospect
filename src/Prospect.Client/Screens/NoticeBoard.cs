namespace Prospect.Client.Screens;

/// <summary>
/// What is on screen of the notices: small, muted messages stacked at the top right. No fonts and no drawing, so the
/// rules can be tested without either.
/// <list type="bullet">
/// <item>A post goes to the back of one fixed-size queue. The oldest <see cref="MaxVisible"/> are shown, newest on top;
/// the rest wait and appear as older ones leave.</item>
/// <item>A notice's timer starts when it is shown, runs out after its duration and fades over the last
/// <see cref="NoticeCatalogue.FadeSeconds"/>. A notice the pointer is over does not age.</item>
/// <item>Closing a notice (its X, or a click that ran its action) lets it fade out at once, whatever the pointer does.</item>
/// <item>A full queue drops its oldest waiting notice for the new one; shown ones are never dropped.</item>
/// </list>
/// Nothing here allocates once built, so it may run in the frame loop.
/// </summary>
internal sealed class NoticeBoard
{
    /// <summary>The most notices shown at once.</summary>
    public const int MaxVisible = 4;

    /// <summary>The most notices held, shown and waiting.</summary>
    public const int Capacity = 32;

    /// <summary>Room for one notice's own text, copied in at post time so the queue holds it without allocating.</summary>
    [System.Runtime.CompilerServices.InlineArray(NoticeCatalogue.MaxText)]
    private struct TextBuffer
    {
        private byte _first;
    }

    private struct Entry
    {
        public TextBuffer Text;
        public int TextLength;
        public int Kind;
        public float Age;
        public float Duration;
        public bool Closing;
    }

    private NoticeCatalogue _catalogue;
    private readonly Entry[] _entries = new Entry[Capacity];
    private int _count;
    private int _hover = -1;
    private int _limit = MaxVisible;

    public NoticeBoard(NoticeCatalogue catalogue)
    {
        _catalogue = catalogue;
    }

    /// <summary>Swaps in a catalogue built from the same file with its key names resolved again, after a remap.</summary>
    public void ReplaceCatalogue(NoticeCatalogue catalogue)
    {
        if (catalogue.Notices.Length != _catalogue.Notices.Length)
            throw new ArgumentException("A rebuilt catalogue has the notices it had.", nameof(catalogue));
        _catalogue = catalogue;
    }

    /// <summary>
    /// False while notices are off, as in a scripted capture or benchmark that did not ask for them: posting then does
    /// nothing, so a run's pictures and timings are not changed by a message over them.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many notices are held, shown and waiting.</summary>
    public int Count => _count;

    /// <summary>
    /// How many may be shown now, 1 to <see cref="MaxVisible"/>. The layout lowers it on a screen too short for four, so
    /// the ones that do not fit wait instead of running off the edge; the waiting ones are not aged.
    /// </summary>
    public int Limit
    {
        get => _limit;
        set => _limit = Math.Clamp(value, 1, MaxVisible);
    }

    /// <summary>How many are shown now, from zero to <see cref="Limit"/>.</summary>
    public int Visible => Math.Min(_count, _limit);

    /// <summary>How many waiting notices were dropped because the queue was full.</summary>
    public int Dropped { get; private set; }

    /// <summary>The catalogue, for drawing.</summary>
    public NoticeCatalogue Catalogue => _catalogue;

    /// <summary>
    /// Posts a notice of <paramref name="kind"/>. False, with nothing held, while notices are off or for a kind the
    /// file does not have.
    /// </summary>
    public bool Post(string kind) => Post(kind, default);

    /// <summary>
    /// Posts a notice of <paramref name="kind"/> that says <paramref name="text"/> (UTF-8, at most
    /// <see cref="NoticeCatalogue.MaxText"/> bytes, the rest cut) instead of the file's text, for a message whose words
    /// are only known when it happens. Colour, duration and action stay the file's. The text is copied; nothing is
    /// allocated. An empty <paramref name="text"/> means the file's own.
    /// </summary>
    public bool Post(string kind, ReadOnlySpan<byte> text)
    {
        if (!Enabled)
            return false;
        var index = _catalogue.IndexOf(kind);
        if (index < 0)
            return false;
        if (_count == Capacity)
        {
            // Everything shown stays; the oldest of those still waiting makes room.
            Array.Copy(_entries, MaxVisible + 1, _entries, MaxVisible, Capacity - MaxVisible - 1);
            _count--;
            Dropped++;
        }

        ref var entry = ref _entries[_count++];
        entry = default;
        entry.Kind = index;
        entry.Duration = _catalogue.Notices[index].Seconds;
        var length = Math.Min(text.Length, NoticeCatalogue.MaxText);
        text[..length].CopyTo(entry.Text);
        entry.TextLength = length;
        return true;
    }

    /// <summary>
    /// Takes back the oldest notice of <paramref name="kind"/>, for a message about something that is over: a shown one
    /// fades out at once, a waiting one just leaves. False when there is none.
    /// </summary>
    public bool Withdraw(string kind)
    {
        var index = _catalogue.IndexOf(kind);
        for (var i = 0; i < _count; i++)
        {
            if (_entries[i].Kind != index)
                continue;
            if (i < Visible)
            {
                Close(Visible - 1 - i);
            }
            else
            {
                Array.Copy(_entries, i + 1, _entries, i, _count - i - 1);
                _count--;
            }

            return true;
        }

        return false;
    }

    /// <summary>The entry shown in <paramref name="row"/>, counted from the top: the newest shown is row zero.</summary>
    private int EntryOf(int row) => Visible - 1 - row;

    /// <summary>The kind, an index into the catalogue, of the notice in <paramref name="row"/>.</summary>
    public int KindAt(int row) => _entries[EntryOf(row)].Kind;

    /// <summary>The notice in <paramref name="row"/>, as the file defines it.</summary>
    public Notice NoticeAt(int row) => _catalogue.Notices[_entries[EntryOf(row)].Kind];

    /// <summary>The words of the notice in <paramref name="row"/>: the text it was posted with, else the file's.</summary>
    public ReadOnlySpan<byte> TextAt(int row)
    {
        ref var entry = ref _entries[EntryOf(row)];
        return entry.TextLength > 0 ? ((ReadOnlySpan<byte>)entry.Text)[..entry.TextLength] : _catalogue.Notices[entry.Kind].TextBytes;
    }

    /// <summary>Seconds the notice in <paramref name="row"/> has been shown.</summary>
    public float AgeAt(int row) => _entries[EntryOf(row)].Age;

    /// <summary>How opaque the notice in <paramref name="row"/> is, 0 to 1: it fades in when first shown and out as it leaves.</summary>
    public float AlphaAt(int row)
    {
        ref var entry = ref _entries[EntryOf(row)];
        var fade = _catalogue.FadeSeconds;
        return Math.Clamp(Math.Min(entry.Age / fade, (entry.Duration - entry.Age) / fade), 0f, 1f);
    }

    /// <summary>
    /// The pointer is over the notice in <paramref name="row"/> this frame: the next <see cref="Update"/> does not age
    /// it. Call once per frame at most per row, while drawing; the hold ends with the frame.
    /// </summary>
    public void Hover(int row) => _hover = EntryOf(row);

    /// <summary>Closes the notice in <paramref name="row"/>: it fades out now and then leaves.</summary>
    public void Close(int row)
    {
        ref var entry = ref _entries[EntryOf(row)];
        entry.Closing = true;
        // Only the fade that is left to go: a notice already past it keeps its place in it.
        entry.Age = Math.Max(entry.Age, entry.Duration - _catalogue.FadeSeconds);
    }

    /// <summary>Ages every shown notice by <paramref name="seconds"/> and removes those that ran out.</summary>
    public void Update(float seconds)
    {
        var shown = Visible;
        var kept = 0;
        var hover = _hover;
        _hover = -1;
        for (var i = 0; i < _count; i++)
        {
            var entry = _entries[i];
            if (i < shown && (i != hover || entry.Closing))
                entry.Age += seconds;
            if (i < shown && entry.Age >= entry.Duration)
            {
                continue;
            }

            _entries[kept++] = entry;
        }

        // A waiting notice that takes a freed place starts its timer then: it was not aged while it waited.
        _count = kept;
    }

    /// <summary>Drops every notice: a new session starts with nothing on screen.</summary>
    public void Clear()
    {
        _count = 0;
        _hover = -1;
    }
}
