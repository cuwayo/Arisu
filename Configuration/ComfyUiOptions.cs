namespace Arisu.Configuration;

public sealed class ComfyUiOptions
{
    public const string SectionName = "ComfyUi";

    /// <summary>
    /// Gets a value indicating whether AI image generation is available.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the absolute URL of the ComfyUI instance's run endpoint.
    /// </summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    /// Gets the bearer token used to call the private ComfyUI instance.
    /// </summary>
    public string Token { get; init; } = string.Empty;

    /// <summary>
    /// Gets the path to the API-format workflow, relative to the application content root.
    /// </summary>
    public string WorkflowPath { get; init; } = "Workflows/MossImageTextToImage.json";

    /// <summary>
    /// Gets the workflow node that receives the user's image description.
    /// </summary>
    public string PromptNodeId { get; init; } = "18:17";

    /// <summary>
    /// Gets the input name on <see cref="PromptNodeId"/> that receives the user's image description.
    /// </summary>
    public string PromptInputName { get; init; } = "value";

    /// <summary>
    /// Gets the workflow node that receives the optional negative image description.
    /// </summary>
    public string NegativePromptNodeId { get; init; } = "18:5";

    /// <summary>
    /// Gets the input name on <see cref="NegativePromptNodeId"/> that receives the negative description.
    /// </summary>
    public string NegativePromptInputName { get; init; } = "text";

    /// <summary>
    /// Gets the maximum time, in seconds, that the ComfyUI instance may run a workflow.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 840;

    /// <summary>
    /// Gets the maximum number of image-generation requests that may run at once.
    /// </summary>
    public int MaximumConcurrentRequests { get; init; } = 1;

    /// <summary>
    /// Gets the time, in seconds, a user must wait after an image-generation request completes.
    /// </summary>
    public int UserCooldownSeconds { get; init; } = 60;
}
