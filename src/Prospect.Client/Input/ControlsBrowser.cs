namespace Prospect.Client.Input;

/// <summary>
/// The state shared by the F1 reference and the Settings Controls page: which tab is open and what the search says.
/// A tab with no match is greyed but stays; a tab with matches shows how many; the matching rows are highlighted. No
/// drawing, so the rules are testable.
/// </summary>
internal sealed class ControlsBrowser(ControlsCatalog catalog)
{
    /// <summary>The search text, trimmed; empty means no search.</summary>
    public string Query { get; private set; } = "";

    /// <summary>The open tab.</summary>
    public int Selected { get; private set; }

    /// <summary>How many rows of each tab match the search; all zero when there is no search.</summary>
    public int[] Counts { get; } = new int[catalog.Tabs.Length];

    public ControlsCatalog Catalog { get; } = catalog;

    /// <summary>True while the search box holds something.</summary>
    public bool Searching => Query.Length > 0;

    /// <summary>True when the tab has something to show for the search: always without one.</summary>
    public bool TabActive(int tab) => !Searching || Counts[tab] > 0;

    /// <summary>True when the row is one the search found.</summary>
    public bool Highlighted(in ControlsCatalog.Row row) => Searching && row.Matches(Query);

    /// <summary>The number of rows the search found, in every tab.</summary>
    public int TotalMatches
    {
        get
        {
            var total = 0;
            foreach (var count in Counts)
                total += count;
            return total;
        }
    }

    /// <summary>Sets the search. When the open tab has no match the first tab that has one opens instead.</summary>
    public void SetQuery(string query)
    {
        Query = query.Trim();
        Recount();
        if (Searching && Counts[Selected] == 0)
            for (var i = 0; i < Counts.Length; i++)
                if (Counts[i] > 0)
                {
                    Selected = i;
                    break;
                }
    }

    /// <summary>Opens a tab, whether or not it has a match.</summary>
    public void Select(int tab) => Selected = Math.Clamp(tab, 0, Counts.Length - 1);

    /// <summary>Counts again after a binding changed, since the rows' text is what the search reads.</summary>
    public void Recount()
    {
        for (var i = 0; i < Counts.Length; i++)
        {
            var count = 0;
            if (Searching)
                foreach (var row in Catalog.Tabs[i].Rows)
                    if (row.Matches(Query))
                        count++;
            Counts[i] = count;
        }
    }

    /// <summary>Clears the search and goes back to the first tab.</summary>
    public void Reset()
    {
        SetQuery("");
        Selected = 0;
    }
}
