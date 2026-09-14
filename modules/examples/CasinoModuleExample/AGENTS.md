# Casino module (`casino`)

Id `casino`. Source: `CasinoModuleExample/CasinoGameModule.cs`. Channel must have the module enabled; there is also a `/gptcli set casino` toggle on `ChannelState.Options.CasinoEnabled`.

## Behavior

Wallet per Discord user on the channel (`ChannelState.CasinoBalances`). Games are **deterministic given `CasinoGameModule.Rng`** (tests seed it). LLM is used only to phrase a result the engine already computed — do not let the model change numbers.

Games: coinflip, dice, roulette, slots, blackjack. Meta: help, status, personal stats, purchase, wallet, leaderboard.

Inputs: `/gptcli casino …` and bang commands (`!coinflip`, `!dice`, `!casino help`, …) via `TryParseMessageCommand`.

## Tests

`tests/GptCli.Modules.Tests/Casino/`. Seed `Rng` in the test ctor and restore `Random.Shared` on dispose.

## Build

`build-module.sh` / `build-module.bat` → `modules/CasinoModuleExample.dll`.
