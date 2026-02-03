using System.Reflection;
using System.Runtime.Loader;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using OpenAI.ObjectModels.RequestModels;

namespace GPT.CLI.Chat.Discord.Modules;

public sealed class DiscordModulePipeline
{
    private readonly IReadOnlyList<IFeatureModule> _modules;
    private readonly DiscordModuleContext _context;
    private readonly Action<string> _log;

    private DiscordModulePipeline(DiscordModuleContext context, IReadOnlyList<IFeatureModule> modules, Action<string> log)
    {
        _context = context;
        _modules = modules;
        _log = log;
    }

    public IReadOnlyList<IFeatureModule> Modules => _modules;

    public static DiscordModulePipeline Create(IServiceProvider services, DiscordModuleContext context, string modulesPath, Action<string> log)
    {
        var modules = DiscoverModules(services, modulesPath, log);
        var ordered = OrderModules(modules, log);
        return new DiscordModulePipeline(context, ordered, log);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var module in _modules)
        {
            await SafeInvokeAsync(module, () => module.InitializeAsync(_context, cancellationToken));
        }
    }

    public async Task OnReadyAsync(CancellationToken cancellationToken)
    {
        foreach (var module in _modules)
        {
            await SafeInvokeAsync(module, () => module.OnReadyAsync(_context, cancellationToken));
        }
    }

    public async Task OnMessageReceivedAsync(SocketMessage message, CancellationToken cancellationToken)
    {
        foreach (var module in _modules)
        {
            await SafeInvokeAsync(module, () => module.OnMessageReceivedAsync(_context, message, cancellationToken));
        }
    }

    public async Task OnMessageUpdatedAsync(Cacheable<IMessage, ulong> oldMessage, SocketMessage newMessage, ISocketMessageChannel channel, CancellationToken cancellationToken)
    {
        foreach (var module in _modules)
        {
            await SafeInvokeAsync(module, () => module.OnMessageUpdatedAsync(_context, oldMessage, newMessage, channel, cancellationToken));
        }
    }

    public async Task OnReactionAddedAsync(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction, CancellationToken cancellationToken)
    {
        foreach (var module in _modules)
        {
            await SafeInvokeAsync(module, () => module.OnReactionAddedAsync(_context, message, channel, reaction, cancellationToken));
        }
    }

    public async Task<bool> OnInteractionAsync(SocketInteraction interaction, CancellationToken cancellationToken)
    {
        var handled = false;
        foreach (var module in _modules)
        {
            var moduleHandled = await SafeInvokeAsync(module, () => module.OnInteractionAsync(_context, interaction, cancellationToken));
            handled = handled || moduleHandled;
        }

        return handled;
    }

    public async Task OnMessageCommandExecutedAsync(SocketMessageCommand command, CancellationToken cancellationToken)
    {
        foreach (var module in _modules)
        {
            await SafeInvokeAsync(module, () => module.OnMessageCommandExecutedAsync(_context, command, cancellationToken));
        }
    }

    public async Task<IReadOnlyList<ChatMessage>> GetAdditionalMessageContextAsync(SocketMessage message, InstructionGPT.ChannelState channel, CancellationToken cancellationToken)
    {
        if (_modules.Count == 0)
        {
            return Array.Empty<ChatMessage>();
        }

        var combined = new List<ChatMessage>();
        foreach (var module in _modules)
        {
            var messages = await SafeInvokeAsync(module, () => module.GetAdditionalMessageContextAsync(_context, message, channel, cancellationToken));
            if (messages is { Count: > 0 })
            {
                combined.AddRange(messages);
            }
        }

        return combined;
    }

    public IReadOnlyList<SlashCommandContribution> GetSlashCommandContributions()
    {
        if (_modules.Count == 0)
        {
            return Array.Empty<SlashCommandContribution>();
        }

        var contributions = new List<SlashCommandContribution>();
        foreach (var module in _modules)
        {
            try
            {
                var moduleContributions = module.GetSlashCommandContributions(_context);
                if (moduleContributions is { Count: > 0 })
                {
                    contributions.AddRange(moduleContributions);
                }
            }
            catch (Exception ex)
            {
                _log($"Module {module.Id} failed to provide slash command contributions: {ex.GetType().Name} - {ex.Message}");
            }
        }

        return contributions;
    }

    private async Task SafeInvokeAsync(IFeatureModule module, Func<Task> handler)
    {
        try
        {
            await handler();
        }
        catch (Exception ex)
        {
            _log($"Module {module.Id} failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<ChatMessage>> SafeInvokeAsync(IFeatureModule module, Func<Task<IReadOnlyList<ChatMessage>>> handler)
    {
        try
        {
            return await handler();
        }
        catch (Exception ex)
        {
            _log($"Module {module.Id} failed: {ex.GetType().Name} - {ex.Message}");
            return Array.Empty<ChatMessage>();
        }
    }

    private async Task<bool> SafeInvokeAsync(IFeatureModule module, Func<Task<bool>> handler)
    {
        try
        {
            return await handler();
        }
        catch (Exception ex)
        {
            _log($"Module {module.Id} failed: {ex.GetType().Name} - {ex.Message}");
            return false;
        }
    }

    private static IReadOnlyList<IFeatureModule> DiscoverModules(IServiceProvider services, string modulesPath, Action<string> log)
    {
        var modules = new List<IFeatureModule>();
        var assemblies = new List<Assembly> { typeof(DiscordModulePipeline).Assembly };

        if (!string.IsNullOrWhiteSpace(modulesPath))
        {
            try
            {
                if (Directory.Exists(modulesPath))
                {
                    var dlls = Directory.GetFiles(modulesPath, "*.dll", SearchOption.TopDirectoryOnly);
                    foreach (var dll in dlls)
                    {
                        try
                        {
                            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dll));
                            assemblies.Add(assembly);
                        }
                        catch (Exception ex)
                        {
                            log($"Failed to load module assembly {dll}: {ex.GetType().Name} - {ex.Message}");
                        }
                    }
                }
                else
                {
                    log($"Module directory not found: {modulesPath}");
                }
            }
            catch (Exception ex)
            {
                log($"Failed to scan module directory {modulesPath}: {ex.GetType().Name} - {ex.Message}");
            }
        }

        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var type in GetModuleTypes(assembly, log))
            {
                try
                {
                    if (ActivatorUtilities.CreateInstance(services, type) is IFeatureModule module)
                    {
                        modules.Add(module);
                    }
                }
                catch (Exception ex)
                {
                    log($"Failed to create module {type.FullName}: {ex.GetType().Name} - {ex.Message}");
                }
            }
        }

        return modules;
    }

    private static IEnumerable<Type> GetModuleTypes(Assembly assembly, Action<string> log)
    {
        try
        {
            return assembly.GetTypes()
                .Where(type => typeof(IFeatureModule).IsAssignableFrom(type) && !type.IsAbstract && type.IsClass);
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var loaderException in ex.LoaderExceptions ?? Array.Empty<Exception>())
            {
                log($"Failed to load module types from {assembly.FullName}: {loaderException.Message}");
            }

            return ex.Types
                .Where(type => type != null && typeof(IFeatureModule).IsAssignableFrom(type) && !type.IsAbstract && type.IsClass)!;
        }
    }

    private static IReadOnlyList<IFeatureModule> OrderModules(IEnumerable<IFeatureModule> modules, Action<string> log)
    {
        var moduleList = modules.ToList();
        var byId = new Dictionary<string, IFeatureModule>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in moduleList)
        {
            if (string.IsNullOrWhiteSpace(module.Id))
            {
                log($"Skipping module with empty id: {module.GetType().FullName}");
                continue;
            }

            if (byId.ContainsKey(module.Id))
            {
                log($"Duplicate module id '{module.Id}' found. Skipping {module.GetType().FullName}.");
                continue;
            }

            byId[module.Id] = module;
        }

        var ordered = new List<IFeatureModule>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string id)
        {
            if (invalid.Contains(id) || visited.Contains(id))
            {
                return;
            }

            if (!byId.TryGetValue(id, out var module))
            {
                return;
            }

            if (!visiting.Add(id))
            {
                log($"Detected module dependency cycle at '{id}'. Skipping module.");
                invalid.Add(id);
                return;
            }

            foreach (var dependency in module.DependsOn ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(dependency))
                {
                    continue;
                }

                if (!byId.ContainsKey(dependency))
                {
                    log($"Module '{module.Id}' depends on missing module '{dependency}'. Skipping.");
                    invalid.Add(id);
                    visiting.Remove(id);
                    return;
                }

                Visit(dependency);
                if (invalid.Contains(dependency))
                {
                    invalid.Add(id);
                    visiting.Remove(id);
                    return;
                }
            }

            visiting.Remove(id);
            visited.Add(id);
            if (!invalid.Contains(id))
            {
                ordered.Add(module);
            }
        }

        foreach (var module in byId.Values)
        {
            Visit(module.Id);
        }

        return ordered;
    }
}
