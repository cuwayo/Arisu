using Arisu.Configuration;
using Arisu.Services;
using Arisu.Storage;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Arisu.Utilities;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

builder.Services
    .AddOptions<DiscordOptions>()
    .Bind(builder.Configuration.GetSection(DiscordOptions.SectionName))
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.Token),
        $"{DiscordOptions.SectionName}:Token must be supplied.")
    .ValidateOnStart();

builder.Services
    .AddOptions<ComfyUiOptions>()
    .Bind(builder.Configuration.GetSection(ComfyUiOptions.SectionName))
    .Validate(
        options => !options.Enabled || EndpointValidation.IsSecureServiceEndpoint(options.Endpoint),
        $"{ComfyUiOptions.SectionName}:Endpoint must be an HTTPS URI (or loopback HTTP URI) without user info or a fragment when image generation is enabled.")
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.Token),
        $"{ComfyUiOptions.SectionName}:Token must be supplied when image generation is enabled.")
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.WorkflowPath),
        $"{ComfyUiOptions.SectionName}:WorkflowPath must be supplied when image generation is enabled.")
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.PromptNodeId),
        $"{ComfyUiOptions.SectionName}:PromptNodeId must be supplied when image generation is enabled.")
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.PromptInputName),
        $"{ComfyUiOptions.SectionName}:PromptInputName must be supplied when image generation is enabled.")
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.NegativePromptNodeId),
        $"{ComfyUiOptions.SectionName}:NegativePromptNodeId must be supplied when image generation is enabled.")
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.NegativePromptInputName),
        $"{ComfyUiOptions.SectionName}:NegativePromptInputName must be supplied when image generation is enabled.")
    .Validate(
        options => options.TimeoutSeconds is >= 1 and <= 840,
        $"{ComfyUiOptions.SectionName}:TimeoutSeconds must be between 1 and 840.")
    .Validate(
        options => options.MaximumConcurrentRequests >= 1,
        $"{ComfyUiOptions.SectionName}:MaximumConcurrentRequests must be at least 1.")
    .Validate(
        options => options.UserCooldownSeconds >= 1,
        $"{ComfyUiOptions.SectionName}:UserCooldownSeconds must be at least 1.")
    .ValidateOnStart();

builder.Services
    .AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .Validate(
        options => DatabaseOptions.IsValidConnectionString(options.ConnectionString),
        $"{DatabaseOptions.SectionName}:ConnectionString must specify a PostgreSQL host, database, and username with finite timeouts and sensitive diagnostic logging disabled.")
    .ValidateOnStart();

builder.Services
    .AddOptions<ObjectStorageOptions>()
    .Bind(builder.Configuration.GetSection(ObjectStorageOptions.SectionName))
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.FolderPath),
        $"{ObjectStorageOptions.SectionName}:FolderPath must be supplied.")
    .Validate(
        options => options.LocalEventCardArchiveRetentionDays >= 0,
        $"{ObjectStorageOptions.SectionName}:LocalEventCardArchiveRetentionDays must not be negative.")
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.BucketName),
        $"{ObjectStorageOptions.SectionName}:BucketName must be supplied when object storage is enabled.")
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.Region),
        $"{ObjectStorageOptions.SectionName}:Region must be supplied when object storage is enabled.")
    .Validate(
        options => !options.Enabled ||
                   string.IsNullOrWhiteSpace(options.AccessKey) ==
                   string.IsNullOrWhiteSpace(options.SecretKey),
        $"{ObjectStorageOptions.SectionName}:AccessKey and SecretKey must be supplied together.")
    .Validate(
        options => !options.Enabled ||
                   string.IsNullOrWhiteSpace(options.ServiceUrl) ||
                   EndpointValidation.IsSecureServiceEndpoint(options.ServiceUrl),
        $"{ObjectStorageOptions.SectionName}:ServiceUrl must be an HTTPS URI (or loopback HTTP URI) without user info or a fragment when supplied.")
    .ValidateOnStart();

builder.Services.AddSingleton(_ =>
    new DiscordSocketClient(new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.Guilds |
                         GatewayIntents.GuildMembers |
                         GatewayIntents.GuildBans |
                         GatewayIntents.GuildVoiceStates |
                         GatewayIntents.GuildMessages |
                         GatewayIntents.MessageContent,
        MessageCacheSize = 100
    }));
builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
})
{
    Timeout = TimeSpan.FromSeconds(10)
});
builder.Services.AddHttpClient(ComfyUiImageGenerationService.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromMinutes(15);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
});

builder.Services.AddSingleton(serviceProvider =>
    new InteractionService(
        serviceProvider.GetRequiredService<DiscordSocketClient>(),
        new InteractionServiceConfig
        {
            DefaultRunMode = RunMode.Async
        }));

builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<
        Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value;
    return NpgsqlDataSource.Create(options.GetNpgsqlConnectionString());
});
builder.Services.AddSingleton<IModerationHistoryStore, PostgresModerationHistoryStore>();
builder.Services.AddSingleton<IVoiceMasterStore, PostgresVoiceMasterStore>();
builder.Services.AddSingleton<ILevelingStore, PostgresLevelingStore>();
builder.Services.AddSingleton<LevelingService>();
builder.Services.AddSingleton<ILevelingCardService, LevelingCardService>();
builder.Services.AddSingleton<VoiceMasterService>();
builder.Services.AddSingleton<IVoiceMasterService>(serviceProvider =>
    serviceProvider.GetRequiredService<VoiceMasterService>());
builder.Services.AddSingleton<PostgresGuildChannelConfigurationStore>();
builder.Services.AddSingleton<IGuildChannelConfigurationStore>(serviceProvider =>
    serviceProvider.GetRequiredService<PostgresGuildChannelConfigurationStore>());
builder.Services.AddSingleton<IObjectStorageAdapter>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<
        Microsoft.Extensions.Options.IOptions<ObjectStorageOptions>>().Value;

    return options.Enabled
        ? new S3ObjectStorageAdapter(
            options,
            serviceProvider.GetRequiredService<ILogger<S3ObjectStorageAdapter>>())
        : new FolderObjectStorageAdapter(
            options,
            serviceProvider.GetRequiredService<IHostEnvironment>(),
            serviceProvider.GetRequiredService<ILogger<FolderObjectStorageAdapter>>());
});

builder.Services.AddSingleton<IChannelLockService, ChannelLockService>();
builder.Services.AddSingleton<IDiscordAutoModService, DiscordAutoModService>();
builder.Services.AddSingleton<LogChannelCache>();
builder.Services.AddSingleton<GatewayLogQueue>();
builder.Services.AddSingleton<IAuditChannelService, AuditChannelService>();
builder.Services.AddSingleton<IImageGenerationService, ComfyUiImageGenerationService>();
builder.Services.AddSingleton<IImageGenerationRequestLimiter, ImageGenerationRequestLimiter>();
builder.Services.AddSingleton<IMemberLogChannelService, MemberLogChannelService>();
builder.Services.AddSingleton<IGuildEventBackgroundService, GuildEventBackgroundService>();
builder.Services.AddSingleton<IGuildEventCardService, GuildEventCardService>();
builder.Services.AddSingleton<IGuildEventChannelService, GuildEventChannelService>();

// The interaction handler must subscribe before the gateway client starts.
builder.Services.AddHostedService<DatabaseInitializationService>();
builder.Services.AddHostedService<ObjectStorageMaintenanceService>();
builder.Services.AddHostedService<InteractionHandler>();
builder.Services.AddHostedService<GuildEventNotificationService>();
builder.Services.AddHostedService<ModerationAuditLogService>();
builder.Services.AddHostedService<MemberLogService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<VoiceMasterService>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<LevelingService>());
builder.Services.AddHostedService<DiscordBotHostedService>();
// Start after the client so shutdown drains accepted logs before Discord logs out.
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<GatewayLogQueue>());

await builder.Build().RunAsync();
