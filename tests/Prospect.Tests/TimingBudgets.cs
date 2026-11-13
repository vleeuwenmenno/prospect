using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The collection of the test classes that hold code to a time budget. Its tests run one at a time, after every
/// collection that runs side by side has finished, so a budget measures the code and not how many other tests
/// shared the processor core it ran on. The budgets themselves are the same wherever the test runs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingBudgets
{
    /// <summary>The collection's name, for <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "Timing budgets";
}
