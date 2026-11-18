using System.Reflection;
using Arisu.Modules;
using Arisu.Services;
using Arisu.Storage;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class CommandRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllModulesProduceValidApplicationCommands(bool imagineEnabled)
    {
        using var client = new DiscordSocketClient();
        using var commands = new InteractionService(client, new InteractionServiceConfig
        {
            DefaultRunMode = RunMode.Async
        });
        using var services = CreateServices();

        // Match InteractionHandler.StartAsync, using the bot assembly instead of the test host's entry assembly.
        var assembly = typeof(PingModule).Assembly;
        if (imagineEnabled)
        {
            await commands.AddModulesAsync(assembly, services);
        }
        else
        {
            foreach (var moduleType in assembly.DefinedTypes.Where(type =>
                         type is { IsAbstract: false, IsClass: true } &&
                         type.DeclaringType is null &&
                         type.AsType() != typeof(ImagineModule) &&
                         typeof(IInteractionModuleBase).IsAssignableFrom(type)))
            {
                await commands.AddModuleAsync(moduleType, services);
            }
        }

        var properties = commands.Modules
            .Where(module => !module.IsSubModule)
            .SelectMany(ToApplicationCommandProps)
            .ToArray();
        Assert.NotEmpty(properties);
        var slashCommands = properties.Select(property => Assert.IsType<SlashCommandProperties>(property)).ToArray();
        Assert.Equal(slashCommands.Length, slashCommands.Select(command => command.Name.Value).Distinct().Count());
        var paths = slashCommands.SelectMany(ValidateCommand).ToHashSet(StringComparer.Ordinal);

        string[] expectedPaths =
        [
            "ping", "help", "kick", "ban", "unban", "timeout", "untimeout", "nick",
            "warn", "warnings", "delwarn", "clearwarnings", "reason", "modlogs",
            "purge", "slowmode", "lock", "unlock", "rank", "profile", "leaderboard",
            "settings leveling enable", "settings leveling disable", "settings leveling status",
            "settings leveling cooldown", "settings leveling exclude-channel", "settings leveling include-channel",
            "settings leveling exclude-role", "settings leveling include-role",
            "settings leveling reward-add", "settings leveling reward-remove",
            "voice mute", "voice unmute", "voice deafen", "voice undeafen", "voice disconnect", "voice move",
            "voicemaster setup", "voicemaster disable", "voicemaster rename", "voicemaster limit",
            "voicemaster lock", "voicemaster unlock", "voicemaster hide", "voicemaster show",
            "voicemaster permit", "voicemaster reject", "voicemaster transfer", "voicemaster claim",
            "automod status", "automod mentions", "automod spam enable", "automod spam disable",
            "automod invites enable", "automod invites disable", "automod words add", "automod words remove",
            "automod words list", "automod exempt role", "automod exempt channel",
            "settings logs set", "settings logs remove", "settings member-logs set", "settings member-logs remove",
            "settings welcome set", "settings welcome background", "settings welcome background-reset",
            "settings welcome remove", "settings warnings set-escalation", "settings warnings remove-escalation"
        ];
        foreach (var path in expectedPaths)
        {
            Assert.Contains(path, paths);
        }

        Assert.Equal(imagineEnabled, paths.Contains("imagine"));
        Assert.Equal(commands.SlashCommands.Count, paths.Count);
        foreach (var command in commands.SlashCommands)
        {
            foreach (var parameter in command.Parameters)
            {
                Assert.NotNull(parameter.TypeConverter);
                if (!parameter.IsRequired && parameter.DefaultValue is { } defaultValue)
                {
                    var parameterType = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                    Assert.True(parameterType.IsInstanceOfType(defaultValue),
                        $"{command.Name}/{parameter.Name} has an incompatible default value.");
                }
            }
        }
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection().AddLogging();
        // Module building invokes constructors. These fail-fast proxies let it do so without any I/O.
        Type[] dependencies =
        [
            typeof(IDiscordAutoModService), typeof(IHostApplicationLifetime), typeof(IChannelLockService),
            typeof(IAuditChannelService), typeof(IModerationHistoryStore), typeof(IGuildChannelConfigurationStore),
            typeof(IMemberLogChannelService), typeof(IGuildEventBackgroundService), typeof(IGuildEventChannelService),
            typeof(IImageGenerationService), typeof(IImageGenerationRequestLimiter), typeof(IVoiceMasterService),
            typeof(ILevelingStore), typeof(ILevelingCardService)
        ];
        foreach (var dependency in dependencies)
        {
            services.AddSingleton(dependency, DispatchProxy.Create(dependency, typeof(RegistrationOnlyProxy)));
        }

        return services.BuildServiceProvider();
    }

    public class RegistrationOnlyProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Offline registration must not invoke {targetMethod}.");
    }

    private static IReadOnlyCollection<ApplicationCommandProperties> ToApplicationCommandProps(ModuleInfo module)
    {
        // Discord.Net uses this internal utility for REST registration, but exposes no public offline equivalent.
        var utility = typeof(InteractionService).Assembly.GetType(
            "Discord.Interactions.ApplicationCommandRestUtil", throwOnError: true)!;
        var method = utility.GetMethod("ToApplicationCommandProps", BindingFlags.Public | BindingFlags.Static,
            [typeof(ModuleInfo), typeof(bool)]);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<IReadOnlyCollection<ApplicationCommandProperties>>(
            method.Invoke(null, [module, false]));
    }

    private static IEnumerable<string> ValidateCommand(SlashCommandProperties command)
    {
        var name = command.Name.Value;
        ValidateNameAndDescription(name, command.Description.Value);
        return ValidateOptions(name, command.Options.IsSpecified ? command.Options.Value : [], depth: 0);
    }

    private static IEnumerable<string> ValidateOptions(
        string path, IReadOnlyCollection<ApplicationCommandOptionProperties> options, int depth)
    {
        Assert.InRange(options.Count, 0, 25);
        Assert.Equal(options.Count, options.Select(option => option.Name).Distinct().Count());
        var hasSubcommands = options.Any(option => option.Type is
            ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup);
        var optionalSeen = false;
        foreach (var option in options)
        {
            ValidateNameAndDescription(option.Name, option.Description);
            Assert.True(Enum.IsDefined(option.Type), $"{path}/{option.Name} has an unsupported option type.");
            if (option.Type is ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup)
            {
                Assert.InRange(depth, 0, 1);
                if (option.Type == ApplicationCommandOptionType.SubCommandGroup)
                {
                    Assert.Equal(0, depth);
                    Assert.NotNull(option.Options);
                    Assert.NotEmpty(option.Options);
                    Assert.All(option.Options, child => Assert.Equal(ApplicationCommandOptionType.SubCommand, child.Type));
                }
                else
                {
                    Assert.DoesNotContain(option.Options ?? [], child => child.Type is
                        ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup);
                }

                foreach (var childPath in ValidateOptions($"{path} {option.Name}", option.Options ?? [], depth + 1))
                {
                    yield return childPath;
                }
            }
            else
            {
                Assert.False(hasSubcommands, $"{path} mixes subcommands and parameters.");
                Assert.Empty(option.Options ?? []);
                Assert.False(optionalSeen && option.IsRequired == true,
                    $"{path}/{option.Name} is required but follows an optional parameter.");
                optionalSeen |= option.IsRequired != true;
                Assert.InRange(option.Choices?.Count ?? 0, 0, 25);
                Assert.False(option.IsAutocomplete && option.Choices is { Count: > 0 });
            }
        }

        if (!hasSubcommands)
        {
            yield return path;
        }
    }

    private static void ValidateNameAndDescription(string name, string description)
    {
        Assert.InRange(name.Length, 1, 32);
        Assert.Matches("^[a-z0-9_-]+$", name);
        Assert.InRange(description.Length, 1, 100);
    }
}
