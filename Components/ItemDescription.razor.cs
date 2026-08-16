using Markdig;
using Microsoft.AspNetCore.Components;

namespace CalorieTracker.Components;

public partial class ItemDescription
{
    private const int ClampThreshold = 200;

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().DisableHtml().Build();

    [Parameter] public string? Text { get; set; }

    private bool _expanded;
    private MarkupString _html;

    private bool IsLong => Text is { } t && (t.Length > ClampThreshold || t.Count(c => c == '\n') > 2);

    protected override void OnParametersSet() =>
        _html = (MarkupString)Markdown.ToHtml(Text ?? "", Pipeline);
}
