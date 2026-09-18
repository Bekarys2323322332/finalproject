using Markdig;

namespace CvManager.Services;

// Thin wrapper over Markdig so the pipeline is configured once and every place
// that shows markdown (project descriptions, Text attributes, discussion posts)
// renders it the same way.
public class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdownRenderer()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            // Users write this text, so raw HTML must not pass through - without
            // this a project description containing a <script> tag would run in
            // every recruiter's browser.
            .DisableHtml()
            .Build();
    }

    public string ToHtml(string? markdown)
    {
        return string.IsNullOrWhiteSpace(markdown)
            ? string.Empty
            : Markdown.ToHtml(markdown, _pipeline);
    }
}
