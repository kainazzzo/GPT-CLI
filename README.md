# GPT-CLI: Command Line Interface for OpenAI's GPT

Welcome to GPT-CLI, a command line interface for harnessing the power of OpenAI's GPT, the world's most advanced language model. This project aims to make the incredible capabilities of GPT more accessible and easily integrated into your workflow, right from the command line.

## Prerequisites

You will need an OpenAI API key. [Sign up](https://platform.openai.com) if you haven't already and create an API key.

If you build from source, you’ll need the .NET 10 SDK. The published binaries can be self-contained.

## Configuration

GPT-CLI uses standard .NET configuration sources (appsettings.json, environment variables, and command-line args). Most settings live under the `GPT` section, with OpenAI credentials under `OpenAI`.

Create an `appsettings.json` in the working directory:

```json
{
  "OpenAI": {
    "ApiKey": "sk-your-apikey-here",
    "BaseDomain": "https://api.openai.com/v1"
  },
  "GPT": {
    "ApiKey": "sk-optional-override",
    "Mode": "Completion",
    "Prompt": "generate a hello world python script",
    "Model": "gpt-5.2",
    "VisionModel": "gpt-4o",
    "MaxTokens": 64000,
    "ChunkSize": 1536,
    "MaxChatHistoryLength": 4096,
    "BotToken": "discord-bot-token-if-using-discord"
  }
}
```

Notes:
- `OpenAI:ApiKey` (or `GPT:ApiKey`) is required.
- `GPT:Mode` can be `Completion`, `Chat`, `Embed`, or `Discord`.
- `GPT:BotToken` is required only for `Discord` mode.
- `OpenAI:BaseDomain` is optional and can target OpenAI-compatible endpoints.
- Do not commit real API keys or bot tokens.

Common `GPT` settings:
- `Model` (e.g. `gpt-5.2`)
- `VisionModel` (e.g. `gpt-4o`, used for image analysis)
- `Prompt` (Completion mode only)
- `MaxTokens`, `Temperature`, `TopP`
- `ChunkSize`, `ClosestMatchLimit` (embedding behavior)
- `MaxChatHistoryLength` (Discord chat history limit)
- `LearningPersonalityPrompt` (Discord infobot style)
- `EmbedFilenames` / `EmbedDirectoryNames` (arrays; use `GPT__EMBEDFILENAMES__0`, etc.)

Environment variable equivalents use double underscores:

```bash
OPENAI__APIKEY="sk-your-apikey-here" \
GPT__MODE="Completion" \
GPT__PROMPT="generate a hello world python script" \
GPT__MODEL="gpt-5.2" \
GPT__VISIONMODEL="gpt-4o" \
gpt > hello.py
```

Discord bot example:

```bash
OPENAI__APIKEY="sk-your-apikey-here" \
GPT__MODE="Discord" \
GPT__BOTTOKEN="your-discord-bot-token" \
GPT__MODEL="gpt-5.2" \
GPT__VISIONMODEL="gpt-4o" \
gpt
```

## Discord bot setup

1) Create a Discord application and bot token in the Discord Developer Portal.
2) Enable required intents in the bot settings:
   - Message Content (required)
3) Generate an invite link (scopes `bot` + `applications.commands`). For basic functionality, grant:
   - View Channels
   - Send Messages
   - Read Message History
   - Add Reactions
   - Attach Files
   - Use Application Commands
   If you want a quick generator, use:
```
https://discordutils.com/bot-invite-generator
```
4) Set config:
   - `OPENAI__APIKEY`
   - `GPT__MODE=Discord`
   - `GPT__BOTTOKEN`
   - Optional: `GPT__MODEL`, `GPT__VISIONMODEL`, `GPT__MAXTOKENS`
5) Run `gpt`. The bot will register `/gptcli` commands on startup.

Notes:
- The bot stores per-channel state in `channels/<guild>_<id>/<channel>_<id>/`.
- Use `/gptcli help` in Discord for available commands and reactions.

## Features

GPT, or Generative Pre-trained Transformer, is known for its remarkable language understanding and generation abilities. Here are some features that you might have already experienced using OpenAI's GPT model, whether it's through the API or ChatGPT:

- **Text Completion:** GPT can automatically complete text based on a given prompt, making it great for drafting emails, creating content, or even generating code.
- **Conversational AI:** GPT's ability to understand context and maintain a conversation makes it ideal for building chatbots, AI assistants, and customer support solutions.
- **Translation:** Leverage GPT's multilingual capabilities to translate text between languages quickly and accurately.
- **Summarization:** GPT can summarize long pieces of text, extracting key points and condensing information into a more digestible format.
- **Question-Answering:** GPT can be used to build powerful knowledge bases, capable of answering questions based on context and provided information.

## Project Intent

The primary goal of this project is to create a command line interface (CLI) for GPT, allowing you to access its incredible capabilities right from your terminal. This CLI will enable you to perform tasks like generating code, writing content, and more, all by simply running commands.

For example, you could generate a bash script for "Hello, World!" with the following command:
```bash
GPT__PROMPT="create a bash Hello World script" gpt > hello.sh
```

But GPT-CLI's potential doesn't stop there. You can even pipe GPT commands to other commands, or back to GPT itself, creating powerful and dynamic workflows.

Here are some additional ideas for GPT-CLI functionalities:

1. **Code Refactoring:** Use GPT-CLI to refactor your code by providing a prompt and outputting the result to the desired file.
```bash
cat original_code.py | GPT__PROMPT="refactor this Python function for better readability" gpt > refactored_code.py
```

2. **Automated Documentation:** Generate documentation for your code by providing relevant prompts.
```bash
GPT__PROMPT="create markdown documentation for this JavaScript code" gpt < file.js
```

3. **Text Processing:** Use GPT-CLI in conjunction with other command line tools to process and manipulate text, such as grep, awk, or sed.
```bash
curl -s https://datti.net/2023/03/14/Publishing-an-Azure-Static-Website-with-Github-Actions-&-Jekyll/ | grep -zPo '<section id="content" class="main inactive">\K.*?(?=</section>)' | sed 's/<[^>]*>//g' | GPT__PROMPT="summarize this article" gpt | grep 'keyword' > summarized_with_keyword.txt
```

4. **Combine Text from Multiple Files and Summarize:**
```bash
cat file1.txt file2.txt | GPT__PROMPT="combine and summarize the information from these two texts" gpt > summarized_information.txt
```

5. **Generate a List of Ideas and Sort by Relevance:**
```bash
GPT__PROMPT="generate a list of 10 innovative AI project ideas" gpt | sort -R | GPT__PROMPT="rank these AI project ideas by their potential impact" gpt > sorted_AI_project_ideas.txt
```

6. **Extract Quotes from a Text and Generate a Motivational Poster:**
```bash
grep -o '".*"' input_text.txt | GPT__PROMPT="create a motivational poster using one of these quotes" gpt > motivational_poster.txt
```

7. **Filter Log File and Generate a Report:**
```bash
grep 'ERROR' log_file.txt | GPT__PROMPT="analyze these error logs and generate a brief report on the most common issues" gpt > error_report.txt
```

8. **Embed context into a chat session via curl, stripping html tags with sed:**
```bash
curl http://url/documentation | sed 's/<[^>]\+>//g' | GPT__MODE="Embed" GPT__CHUNKSIZE=2048 gpt > docs.dat && GPT__MODE="Chat" GPT__EMBEDFILENAMES__0=docs.dat gpt
```

## Command-line modes

- `gpt` (default) uses `GPT:Mode` (Completion/Chat/Embed/Discord).
- `gpt chat` forces Chat mode (overrides config mode).
- Piped input in Completion mode is treated as the “source text” and the prompt is applied to it.
With GPT-CLI, the possibilities are limited only by your imagination and ability to prompt and string together commands.

I hope this tool will empower you to integrate GPT into your workflow, streamline your tasks, and unleash your creativity.
