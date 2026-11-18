using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Arisu.Configuration;
using Arisu.Utilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arisu.Services;

public interface IImageGenerationService
{
    bool IsEnabled { get; }

    Task<GeneratedImage> GenerateAsync(
        string prompt,
        string? negativePrompt = null,
        ImageDimensions? dimensions = null,
        CancellationToken cancellationToken = default);
}

public sealed record GeneratedImage(byte[] Content, string ContentType, string FileName);

public sealed record ImageDimensions(int Width, int Height);

public sealed class ImageGenerationException : Exception
{
    public ImageGenerationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class ComfyUiImageGenerationService : IImageGenerationService
{
    public const string HttpClientName = "ComfyUi";

    private const string LatentImageNodeId = "18:6";
    private const int MaximumImageBytes = 8 * 1024 * 1024;
    private const int MaximumResponseBytes = 12 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ComfyUiImageGenerationService> _logger;
    private readonly ComfyUiOptions _options;

    public ComfyUiImageGenerationService(
        IHttpClientFactory httpClientFactory,
        IHostEnvironment environment,
        IOptions<ComfyUiOptions> options,
        ILogger<ComfyUiImageGenerationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _environment = environment;
        _logger = logger;
        _options = options.Value;
    }

    public bool IsEnabled => _options.Enabled;

    public async Task<GeneratedImage> GenerateAsync(
        string prompt,
        string? negativePrompt = null,
        ImageDimensions? dimensions = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            throw new ImageGenerationException("AI image generation is not configured.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds + 30));
        var requestToken = timeout.Token;
        var workflow = await LoadWorkflowAsync(requestToken);
        SetPrompt(workflow, prompt);
        if (!string.IsNullOrWhiteSpace(negativePrompt))
        {
            SetNegativePrompt(workflow, negativePrompt);
        }

        if (dimensions is not null)
        {
            SetDimensions(workflow, dimensions);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(
                new ComfyUiRunRequest(workflow, _options.TimeoutSeconds),
                options: SerializerOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ComfyUI image generation request failed with HTTP status {StatusCode}.",
                    (int)response.StatusCode);
                throw new ImageGenerationException(
                    "The image service could not generate an image. Please try again later.");
            }

            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            {
                throw new InvalidDataException("The image response exceeds the size limit.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(requestToken);
            var responseBytes = await BoundedStreamReader.ReadAsync(source, MaximumResponseBytes, requestToken);
            var result = JsonSerializer.Deserialize<CerebriumRunResponse>(responseBytes, SerializerOptions);
            var image = result?.Result?.Images?.FirstOrDefault()
                ?? result?.Images?.FirstOrDefault();
            if (image is null || string.IsNullOrWhiteSpace(image.Base64))
            {
                _logger.LogWarning("ComfyUI image generation response did not include an image.");
                throw new ImageGenerationException(
                    "The image service completed without returning an image. Please try again.");
            }

            if (image.Base64.Length > 4 * ((MaximumImageBytes + 2) / 3))
            {
                throw new InvalidDataException("The generated image exceeds the size limit.");
            }

            byte[] content;
            try
            {
                content = Convert.FromBase64String(image.Base64);
            }
            catch (FormatException exception)
            {
                _logger.LogWarning(exception, "ComfyUI returned invalid base64 image content.");
                throw new ImageGenerationException(
                    "The image service returned an invalid image. Please try again later.",
                    exception);
            }

            if (content.Length == 0)
            {
                throw new ImageGenerationException(
                    "The image service returned an empty image. Please try again.");
            }

            if (content.Length > MaximumImageBytes)
            {
                throw new InvalidDataException("The generated image exceeds the size limit.");
            }

            var contentType = ValidateImage(content);
            return new GeneratedImage(content, contentType, GetFileName(contentType));
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Could not reach the ComfyUI image generation service.");
            throw new ImageGenerationException(
                "The image service is unavailable. Please try again later.",
                exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "ComfyUI image generation request timed out.");
            throw new ImageGenerationException(
                "The image service took too long to respond. Please try again later.",
                exception);
        }
        catch (InvalidDataException exception)
        {
            _logger.LogWarning(exception, "ComfyUI returned oversized or unsupported image content.");
            throw new ImageGenerationException(
                "The image service returned an unsupported or oversized image. Try smaller dimensions.",
                exception);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "ComfyUI returned an invalid image generation response.");
            throw new ImageGenerationException(
                "The image service returned an invalid response. Please try again later.",
                exception);
        }
    }

    private async Task<JsonObject> LoadWorkflowAsync(CancellationToken cancellationToken)
    {
        var workflowPath = Path.IsPathFullyQualified(_options.WorkflowPath)
            ? _options.WorkflowPath
            : Path.Combine(_environment.ContentRootPath, _options.WorkflowPath);

        try
        {
            var workflowInfo = new FileInfo(workflowPath);
            if (!workflowInfo.Exists || workflowInfo.Length > 4 * 1024 * 1024)
            {
                throw new IOException("The workflow file is unavailable or exceeds the 4 MB limit.");
            }

            var workflowJson = await File.ReadAllTextAsync(workflowPath, cancellationToken);
            return JsonNode.Parse(workflowJson) as JsonObject
                ?? throw new JsonException("The workflow must be a JSON object.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(exception, "Could not load ComfyUI workflow {WorkflowPath}.", workflowPath);
            throw new ImageGenerationException(
                "The image-generation workflow is unavailable. Please contact an administrator.",
                exception);
        }
    }

    private void SetPrompt(JsonObject workflow, string prompt)
    {
        SetWorkflowInput(
            workflow,
            _options.PromptNodeId,
            _options.PromptInputName,
            prompt,
            "prompt");
    }

    private void SetNegativePrompt(JsonObject workflow, string negativePrompt)
    {
        SetWorkflowInput(
            workflow,
            _options.NegativePromptNodeId,
            _options.NegativePromptInputName,
            negativePrompt,
            "negative prompt");
    }

    private void SetWorkflowInput(
        JsonObject workflow,
        string nodeId,
        string inputName,
        string value,
        string description)
    {
        var inputs = workflow[nodeId]?["inputs"]?.AsObject();
        if (inputs is null || !inputs.ContainsKey(inputName))
        {
            _logger.LogError(
                "ComfyUI workflow does not contain {Description} input {InputName} on node {NodeId}.",
                description,
                inputName,
                nodeId);
            throw new ImageGenerationException(
                "The image-generation workflow is invalid. Please contact an administrator.");
        }

        inputs[inputName] = value;
    }

    private void SetDimensions(JsonObject workflow, ImageDimensions dimensions)
    {
        var inputs = workflow[LatentImageNodeId]?["inputs"]?.AsObject();
        if (inputs is null)
        {
            _logger.LogError(
                "ComfyUI workflow does not contain latent image node {LatentImageNodeId}.",
                LatentImageNodeId);
            throw new ImageGenerationException(
                "The image-generation workflow is invalid. Please contact an administrator.");
        }

        inputs["width"] = dimensions.Width;
        inputs["height"] = dimensions.Height;
    }

    private static string ValidateImage(byte[] content)
    {
        using var data = SkiaSharp.SKData.CreateCopy(content);
        using var codec = SkiaSharp.SKCodec.Create(data)
            ?? throw new InvalidDataException("The generated content is not a valid image.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 4_000_000)
        {
            throw new InvalidDataException("The generated image has too many pixels.");
        }

        return codec.EncodedFormat switch
        {
            SkiaSharp.SKEncodedImageFormat.Png => "image/png",
            SkiaSharp.SKEncodedImageFormat.Jpeg => "image/jpeg",
            SkiaSharp.SKEncodedImageFormat.Webp => "image/webp",
            _ => throw new InvalidDataException("The generated image format is unsupported.")
        };
    }

    private static string GetFileName(string contentType) => contentType switch
    {
        "image/jpeg" => "imagine.jpg",
        "image/webp" => "imagine.webp",
        _ => "imagine.png"
    };

    private sealed record ComfyUiRunRequest(
        JsonObject Prompt,
        [property: JsonPropertyName("timeout_seconds")] int TimeoutSeconds);

    private sealed record CerebriumRunResponse(
        ComfyUiRunResult? Result,
        IReadOnlyList<ComfyUiImage>? Images);

    private sealed record ComfyUiRunResult(IReadOnlyList<ComfyUiImage>? Images);

    private sealed record ComfyUiImage(
        string? FileName,
        [property: JsonPropertyName("content_type")] string? ContentType,
        string? Base64);
}
