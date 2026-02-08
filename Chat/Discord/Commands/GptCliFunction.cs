using System.Text.Json;
using Discord;
using Discord.WebSocket;
using GPT.CLI.Chat.Discord.Modules;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.ObjectModels.SharedModels;

namespace GPT.CLI.Chat.Discord.Commands;

public enum GptCliSlashBindingKind
{
    SubCommand,
    GroupSubCommand,
    SetOption
}

public sealed record GptCliSlashBinding(
    GptCliSlashBindingKind Kind,
    string TopLevelName,
    string TopLevelDescription = null,
    string SubCommandName = null,
    string SetOptionName = null);

public enum GptCliParamType
{
    Boolean,
    Integer,
    Number,
    String,
    User,
    Channel,
    Role,
    Attachment
}

public sealed record GptCliParamChoice(string Name, string Value);

public sealed record GptCliParamSpec(
    string Name,
    GptCliParamType Type,
    string Description,
    bool Required = false,
    int? MinInt = null,
    int? MaxInt = null,
    IReadOnlyList<GptCliParamChoice> Choices = null);

public sealed record GptCliExecutionContext(
    DiscordModuleContext Context,
    InstructionGPT.ChannelState ChannelState,
    IMessageChannel Channel,
    IUser User,
    SocketSlashCommand SlashCommand = null,
    SocketMessage Message = null);

public sealed record GptCliExecutionResult(
    bool Handled,
    string Response,
    bool StateChanged = true)
{
    public static readonly GptCliExecutionResult NotHandled = new(false, null, false);
}

public sealed class GptCliFunction
{
    public string ToolName { get; init; }
    public string Description { get; init; }
    public GptCliSlashBinding Slash { get; init; }
    public IReadOnlyList<GptCliParamSpec> Parameters { get; init; } = Array.Empty<GptCliParamSpec>();

    // Optional: identifies the owning feature module. When set, the host can hide tools when the module is disabled.
    public string ModuleId { get; set; }

    // Some functions (like "enable/disable this module") must remain callable even when the module is disabled.
    public bool ExposeWhenModuleDisabled { get; set; }

    // argumentsJson is always a JSON object; modules/core should parse only the keys they declared in Parameters.
    public Func<GptCliExecutionContext, string, CancellationToken, Task<GptCliExecutionResult>> ExecuteAsync { get; init; }

	    public ToolDefinition ToToolDefinition()
	    {
	        if (string.IsNullOrWhiteSpace(ToolName))
	        {
            throw new InvalidOperationException("ToolName is required.");
        }

	        var properties = new Dictionary<string, PropertyDefinition>(StringComparer.OrdinalIgnoreCase);
	        var required = new List<string>();

        foreach (var p in Parameters ?? Array.Empty<GptCliParamSpec>())
        {
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                continue;
            }

            var prop = new PropertyDefinition
            {
                Type = p.Type switch
                {
                    GptCliParamType.Boolean => "boolean",
                    GptCliParamType.Integer => "integer",
                    GptCliParamType.Number => "number",
                    _ => "string"
                },
                Description = p.Description
            };

            if (p.Choices is { Count: > 0 })
            {
                prop.Enum = p.Choices.Select(c => c.Value).ToList();
            }

            properties[p.Name] = prop;
            if (p.Required)
            {
                required.Add(p.Name);
            }
        }

	        return new ToolDefinition
	        {
	            Type = "function",
	            Function = new FunctionDefinition
	            {
	                Name = ToolName,
	                Description = string.IsNullOrWhiteSpace(Description) ? ToolName : Description,
	                // Keep tool schemas flexible: strict mode requires every property be listed in `required`,
	                // which breaks optional parameters for natural-language routing.
	                Strict = false,
	                Parameters = new PropertyDefinition
	                {
	                    Type = "object",
	                    AdditionalProperties = false,
	                    Properties = properties,
	                    // Always supply `required` (even empty) to avoid schema validation failures on some models/providers.
	                    Required = required
	                }
	            }
	        };
	    }

    public ApplicationCommandOptionType GetSlashOptionType()
    {
        return Slash?.Kind switch
        {
            GptCliSlashBindingKind.SubCommand => ApplicationCommandOptionType.SubCommand,
            GptCliSlashBindingKind.GroupSubCommand => ApplicationCommandOptionType.SubCommand,
            GptCliSlashBindingKind.SetOption => ApplicationCommandOptionType.SubCommand,
            _ => ApplicationCommandOptionType.SubCommand
        };
    }

    public SlashCommandOptionBuilder BuildSlashSubCommand()
    {
        if (Slash?.Kind != GptCliSlashBindingKind.GroupSubCommand)
        {
            throw new InvalidOperationException("BuildSlashSubCommand is only valid for GroupSubCommand bindings.");
        }

        var builder = new SlashCommandOptionBuilder()
            .WithName(Slash.SubCommandName)
            .WithDescription(string.IsNullOrWhiteSpace(Description) ? Slash.SubCommandName : Description)
            .WithType(ApplicationCommandOptionType.SubCommand);

        foreach (var p in Parameters ?? Array.Empty<GptCliParamSpec>())
        {
            builder.AddOption(BuildSlashParamOption(p));
        }

        return builder;
    }

    public SlashCommandOptionBuilder BuildSlashSetOption()
    {
        if (Slash?.Kind != GptCliSlashBindingKind.SetOption)
        {
            throw new InvalidOperationException("BuildSlashSetOption is only valid for SetOption bindings.");
        }

        var valueSpec = Parameters?.FirstOrDefault();
        if (valueSpec == null)
        {
            throw new InvalidOperationException("SetOption requires a value parameter spec.");
        }

        // For `/gptcli set`, every option must be optional; otherwise Discord will require *all* options in the subcommand.
        // The tool/function schema can still require the value argument.
        var renamed = valueSpec with { Name = Slash.SetOptionName, Required = false };
        return BuildSlashParamOption(renamed);
    }

    public SlashCommandOptionBuilder BuildSlashParamOption(GptCliParamSpec p)
    {
        var optType = p.Type switch
        {
            GptCliParamType.Boolean => ApplicationCommandOptionType.Boolean,
            GptCliParamType.Integer => ApplicationCommandOptionType.Integer,
            GptCliParamType.Number => ApplicationCommandOptionType.Number,
            GptCliParamType.User => ApplicationCommandOptionType.User,
            GptCliParamType.Channel => ApplicationCommandOptionType.Channel,
            GptCliParamType.Role => ApplicationCommandOptionType.Role,
            GptCliParamType.Attachment => ApplicationCommandOptionType.Attachment,
            _ => ApplicationCommandOptionType.String
        };

        var option = new SlashCommandOptionBuilder()
            .WithName(p.Name)
            .WithDescription(p.Description ?? p.Name)
            .WithType(optType)
            .WithRequired(p.Required);

        if (p.Choices is { Count: > 0 } && optType == ApplicationCommandOptionType.String)
        {
            foreach (var c in p.Choices)
            {
                option.AddChoice(c.Name, c.Value);
            }
        }

        if (p.MinInt.HasValue && optType == ApplicationCommandOptionType.Integer)
        {
            option.WithMinValue(p.MinInt.Value);
        }

        if (p.MaxInt.HasValue && optType == ApplicationCommandOptionType.Integer)
        {
            option.WithMaxValue(p.MaxInt.Value);
        }

        return option;
    }

	    public static bool TryGetJsonProperty(string argumentsJson, string name, out JsonElement value)
	    {
	        value = default;
	        if (string.IsNullOrWhiteSpace(argumentsJson))
	        {
	            return false;
	        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

	            if (!doc.RootElement.TryGetProperty(name, out var element))
	            {
	                return false;
	            }

	            // JsonElement is backed by its JsonDocument; clone so callers can safely use it after disposing the document.
	            value = element.Clone();
	            return true;
	        }
	        catch
	        {
	            return false;
	        }
    }
}
