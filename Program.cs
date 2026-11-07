using Arisu.Configuration;
using Arisu.Services;
using Arisu.Storage;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    .AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.Path),
        $"{DatabaseOptions.SectionName}:Path must be supplied.")
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
                   Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out _),
        $"{ObjectStorageOptions.SectionName}:ServiceUrl must be an absolute URI when supplied.")
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
builder.Services.AddSingleton(new HttpClient
{
    Timeout = TimeSpan.FromSeconds(10)
});

builder.Services.AddSingleton(serviceProvider =>
    new InteractionService(
        serviceProvider.GetRequiredService<DiscordSocketClient>(),
        new InteractionServiceConfig
        {
            DefaultRunMode = RunMode.Async
        }));

builder.Services.AddSingleton<SqliteGuildChannelConfigurationStore>();
builder.Services.AddSingleton<IGuildChannelConfigurationStore>(serviceProvider =>
    serviceProvider.GetRequiredService<SqliteGuildChannelConfigurationStore>());
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

builder.Services.AddSingleton<IAuditChannelService, AuditChannelService>();
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
builder.Services.AddHostedService<DiscordBotHostedService>();

await builder.Build().RunAsync();
