# Welcome module (`welcome`)

Id `welcome`. Source: `WelcomeModuleExample/WelcomeOnboardingModule.cs`. Guild-scoped config on channel state (`WelcomeState`): destination channel, role, rules text, validations, per-user progress.

## Behavior

Onboarding for new members: post rules, require validations (`acknowledge`, `reaction`, `phrase`), optional nudge (10-minute cooldown). Slash: `/gptcli welcome enable|setchannel|setrole|…`. Message/reaction handlers complete validations. No combat-style engine; keep validation logic in the helpers already tested (`ApplyAcknowledgeValidation`, etc.).

## Tests

`tests/GptCli.Modules.Tests/Welcome/` — validation application and nudge cooldown. Does not join a guild.

## Build

`build-module.sh` → `modules/WelcomeModuleExample.dll`.
