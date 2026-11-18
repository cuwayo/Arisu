using Arisu.Services;
using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Arisu.Modules;

public sealed class ImagineModule : InteractionModuleBase<SocketInteractionContext>
{
    private const int MaximumPromptLength = 4_000;
    private const int MinimumDimension = 256;
    private const int MaximumDimension = 4_096;
    private const int MaximumImagePixels = 4_000_000;

    private readonly IImageGenerationService _imageGenerationService;
    private readonly IImageGenerationRequestLimiter _requestLimiter;
    private readonly ILogger<ImagineModule> _logger;

    public ImagineModule(
        IImageGenerationService imageGenerationService,
        IImageGenerationRequestLimiter requestLimiter,
        ILogger<ImagineModule> logger)
    {
        _imageGenerationService = imageGenerationService;
        _requestLimiter = requestLimiter;
        _logger = logger;
    }

    [SlashCommand("imagine", "Open a form to generate an image.")]
    public Task ImagineAsync() =>
        RespondWithModalAsync<ImagineModal>("imagine");

    [ModalInteraction("imagine")]
    public async Task HandleImagineModalAsync(ImagineModal modal)
    {
        var normalizedPrompt = modal.Prompt.Trim();
        if (string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            await RespondAsync("Describe the image that you want to generate.", ephemeral: true);
            return;
        }

        if (normalizedPrompt.Length > MaximumPromptLength)
        {
            await RespondAsync(
                $"Image descriptions can contain at most {MaximumPromptLength:N0} characters.",
                ephemeral: true);
            return;
        }

        var normalizedNegativePrompt = string.IsNullOrWhiteSpace(modal.NegativePrompt)
            ? null
            : modal.NegativePrompt.Trim();
        if (normalizedNegativePrompt?.Length > MaximumPromptLength)
        {
            await RespondAsync(
                $"Negative image descriptions can contain at most {MaximumPromptLength:N0} characters.",
                ephemeral: true);
            return;
        }

        var widthText = string.IsNullOrWhiteSpace(modal.Width)
            ? null
            : modal.Width.Trim();
        var heightText = string.IsNullOrWhiteSpace(modal.Height)
            ? null
            : modal.Height.Trim();

        if ((widthText is null) != (heightText is null))
        {
            await RespondAsync(
                "Set both image width and height, or leave both blank.",
                ephemeral: true);
            return;
        }

        ImageDimensions? dimensions = null;
        if (widthText is not null && heightText is not null)
        {
            if (!int.TryParse(widthText, out var imageWidth) ||
                !int.TryParse(heightText, out var imageHeight))
            {
                await RespondAsync(
                    "Image width and height must be whole numbers.",
                    ephemeral: true);
                return;
            }

            if (imageWidth is < MinimumDimension or > MaximumDimension ||
                imageHeight is < MinimumDimension or > MaximumDimension)
            {
                await RespondAsync(
                    $"Image width and height must be between {MinimumDimension:N0} and " +
                    $"{MaximumDimension:N0} pixels.",
                    ephemeral: true);
                return;
            }

            if (imageWidth % 16 != 0 || imageHeight % 16 != 0)
            {
                await RespondAsync(
                    "Image width and height must be multiples of 16 pixels.",
                    ephemeral: true);
                return;
            }

            if ((long)imageWidth * imageHeight > MaximumImagePixels)
            {
                await RespondAsync(
                    $"Image dimensions can total at most {MaximumImagePixels:N0} pixels.",
                    ephemeral: true);
                return;
            }

            dimensions = new ImageDimensions(imageWidth, imageHeight);
        }

        if (!_imageGenerationService.IsEnabled)
        {
            await RespondAsync(
                "AI image generation is not configured on this bot.",
                ephemeral: true);
            return;
        }

        var admission = _requestLimiter.TryAcquire(Context.User.Id);
        if (!admission.IsAccepted)
        {
            await RespondAsync(GetRejectionMessage(admission), ephemeral: true);
            return;
        }

        using var lease = admission.Lease!;
        await DeferAsync(ephemeral: true);

        try
        {
            var image = await _imageGenerationService.GenerateAsync(
                normalizedPrompt,
                normalizedNegativePrompt,
                dimensions);

            if ((ulong)image.Content.LongLength > Context.Interaction.AttachmentSizeLimit)
            {
                await ModifyOriginalResponseAsync(properties =>
                    properties.Content = "The generated image exceeds this channel's upload limit. " +
                                         "Try smaller dimensions.");
                return;
            }

            await using var stream = new MemoryStream(image.Content, writable: false);
            try
            {
                await ModifyOriginalResponseAsync(properties =>
                    properties.Content = "Your generated image is ready.");
                await FollowupWithFileAsync(
                    stream,
                    image.FileName,
                    text: $"Generated for {Context.User.Mention}.",
                    allowedMentions: AllowedMentions.None);
            }
            catch (HttpException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not upload a generated image for user {UserId} in guild {GuildId}.",
                    Context.User.Id,
                    Context.Guild?.Id);
                await ModifyOriginalResponseAsync(properties =>
                    properties.Content = "I could not upload the generated image. Try smaller dimensions.");
                return;
            }

            _logger.LogInformation(
                "Generated an image for user {UserId} in guild {GuildId}.",
                Context.User.Id,
                Context.Guild?.Id);
        }
        catch (ImageGenerationException exception)
        {
            _logger.LogWarning(
                exception,
                "Could not generate an image for user {UserId} in guild {GuildId}.",
                Context.User.Id,
                Context.Guild?.Id);
            await ModifyOriginalResponseAsync(properties => properties.Content = exception.Message);
        }
    }

    private static string GetRejectionMessage(ImageGenerationRequestAdmission admission) =>
        admission.Rejection switch
        {
            ImageGenerationRequestRejection.AlreadyRunning =>
                "You already have an image generation in progress.",
            ImageGenerationRequestRejection.CoolingDown =>
                $"Wait {Math.Ceiling(admission.RetryAfter.TotalSeconds):N0} seconds before generating another image.",
            ImageGenerationRequestRejection.AtCapacity =>
                "Image generation is currently busy. Please try again in a few minutes.",
            _ => "I could not start image generation. Please try again later."
        };

    public sealed class ImagineModal : IModal
    {
        public string Title => "Generate an image";

        [InputLabel("Prompt", "Describe the image you want to generate.")]
        [ModalTextInput(
            "prompt",
            TextInputStyle.Paragraph,
            placeholder: "A fox reading beside a rainy window",
            maxLength: MaximumPromptLength)]
        public string Prompt { get; set; } = string.Empty;

        [RequiredInput(false)]
        [InputLabel("Negative prompt", "Optional. Describe what to exclude from the image.")]
        [ModalTextInput(
            "negative-prompt",
            TextInputStyle.Paragraph,
            placeholder: "Blurry, distorted, watermark",
            maxLength: MaximumPromptLength)]
        public string? NegativePrompt { get; set; }

        [RequiredInput(false)]
        [InputLabel("Width", "Optional. Leave both size fields blank for the workflow default.")]
        [ModalTextInput(
            "width",
            TextInputStyle.Short,
            placeholder: "1024",
            maxLength: 4)]
        public string? Width { get; set; }

        [RequiredInput(false)]
        [InputLabel("Height", "Optional. Leave both size fields blank for the workflow default.")]
        [ModalTextInput(
            "height",
            TextInputStyle.Short,
            placeholder: "1024",
            maxLength: 4)]
        public string? Height { get; set; }
    }
}
