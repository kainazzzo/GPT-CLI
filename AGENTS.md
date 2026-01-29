# Repository Guidelines

## Project Structure & Module Organization
- `Program.cs`, `OpenAILogic.cs`, and `GptOptions.cs` host the CLI entry point and core GPT logic.
- `Chat/` contains chat-oriented flows, with `Chat/Discord/` holding Discord bot integrations.
- `Embeddings/` includes document/embedding utilities (e.g., `Document.cs`, `CosineSimilarity.cs`).
- `deploy/` holds shell scripts for deployment helpers.
- `Properties/` contains runtime launch settings.
- Build artifacts land in `bin/` and `obj/` (do not edit by hand).

## Build, Test, and Development Commands
- `dotnet publish gpt.csproj -c Release -r win-x64 -o c:\bin\ --self-contained true -p:PublishSingleFile=true`
  - Used by `localinstall.bat` to publish a single-file Windows binary.
- `dotnet publish gpt.csproj -c Release -r linux-x64 -o $HOME/bin --self-contained true -p:PublishSingleFile=true`
  - Used by `linuxinstall.sh` for Linux installs.
- `release.bat <version>` updates version fields in `gpt.csproj`, commits, tags, and pushes.
- `test.bat` currently runs a publish command (no automated tests are wired here).

## Coding Style & Naming Conventions
- Language: C# targeting `net10.0` (see `gpt.csproj`).
- Use standard .NET conventions: PascalCase for types/methods, camelCase for locals/fields.
- Keep new files near related functionality (e.g., Discord features in `Chat/Discord/`).
- No repo-specific formatter is configured; avoid sweeping style reflows.

## Testing Guidelines
- There is no dedicated test project or framework in this repository yet.
- If you add tests, keep them in a `tests/` or `*.Tests` project and document how to run them.

## Commit & Pull Request Guidelines
- Commit messages follow a Conventional Commits style (e.g., `feat(discord): …`, `refactor(state): …`).
- PRs should include: a concise summary, key commands run, and any config changes (e.g., `appsettings.json` or env vars).
- For user-facing changes, include example CLI usage or a short before/after note.

## Configuration Tips
- Local settings live in `appsettings.json` or environment variables (`OPENAI__APIKEY`, `GPT__PROMPT`).
- Avoid committing real API keys; use placeholders in docs and examples.
