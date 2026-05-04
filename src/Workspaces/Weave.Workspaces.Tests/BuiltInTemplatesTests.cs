using Weave.Workspaces.Actors;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Tests;

/// <summary>
/// Pins the curated <see cref="BuiltInTemplates.All"/> set against the same
/// validator the runtime registry runs at publish time. A drift between a
/// template's tool list and its capability grants is the exact bug
/// <c>CapabilityTemplateActor.Validate</c> exists to catch — this suite makes
/// sure the curated set ships pre-validated.
/// </summary>
public sealed class BuiltInTemplatesTests
{
    [Fact]
    public void All_HasFiveTemplates()
    {
        BuiltInTemplates.All.Count.ShouldBe(5);
    }

    [Fact]
    public void All_TemplateIdsAreUnique()
    {
        var ids = BuiltInTemplates.All.Select(t => t.TemplateId).ToList();
        ids.Distinct().Count().ShouldBe(ids.Count);
    }

    [Theory]
    [MemberData(nameof(EveryTemplate))]
    public void Validate_PassesForEveryBuiltInTemplate(CapabilityTemplate template)
    {
        var results = CapabilityTemplateActor.Validate(template);

        results.ShouldAllBe(r => r.Passed,
            customMessage: $"Template '{template.TemplateId}' failed validation: " +
                string.Join("; ", results.Where(r => !r.Passed).Select(r => $"{r.Check}: {r.Detail}")));
    }

    [Theory]
    [MemberData(nameof(EveryTemplate))]
    public void EveryTemplate_TaggedAsBuiltIn(CapabilityTemplate template)
    {
        template.Tags.ShouldContain("built-in");
    }

    public static TheoryData<CapabilityTemplate> EveryTemplate()
    {
        var data = new TheoryData<CapabilityTemplate>();
        foreach (var template in BuiltInTemplates.All)
            data.Add(template);
        return data;
    }
}
