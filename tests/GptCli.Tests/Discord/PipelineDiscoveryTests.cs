using GPT.CLI.Chat.Discord.Modules;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GptCli.Tests.Discord;

[Collection("fs")]
public sealed class PipelineDiscoveryTests
{
    [Fact]
    public void Create_with_empty_modules_folder_still_loads_infobot()
    {
        var modulesPath = Path.Combine(Path.GetTempPath(), "gptcli-modules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modulesPath);
        try
        {
            var logs = new List<string>();
            var pipeline = DiscordModulePipeline.Create(
                new ServiceCollection().BuildServiceProvider(),
                context: null,
                modulesPath,
                logs.Add);

            Assert.Contains(pipeline.Modules, m => m.Id == "infobot");
            var slash = pipeline.GetSlashCommandContributions();
            Assert.NotEmpty(slash);
        }
        finally
        {
            if (Directory.Exists(modulesPath))
            {
                Directory.Delete(modulesPath, recursive: true);
            }
        }
    }
}
