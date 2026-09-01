using AgentTrust.Runner;
using Xunit;

namespace AgentTrust.Tests;

public class ScenarioSuiteTests
{
    private static string ScenariosDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.GetFiles("*.sln").Any())
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir!.FullName, "scenarios");
    }

    public static IEnumerable<object[]> Scenarios() =>
        ScenarioRunner.LoadAll(ScenariosDir()).Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task ScenarioMatchesGroundTruth(ScenarioDefinition scenario)
    {
        var result = await ScenarioRunner.RunAsync(scenario);

        Assert.True(result.Correct,
            $"{scenario.ScenarioId}: expected {scenario.ExpectedDecision}, got {result.ActualDecision}. Reasons: {string.Join(",", result.ReasonCodes)}");
    }
}
