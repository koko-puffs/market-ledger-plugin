# Notes for Claude sessions

## Commits: authored by koko-puffs, no Claude lines

- Commit as koko-puffs, unsigned. Before your first commit, run:
  `git config user.name koko-puffs && git config user.email aeromaax22@gmail.com && git config commit.gpgsign false`
  (A Claude session signs its commits with its own key, which isn't on koko-puffs' GitHub
  account, so GitHub marks them "Unverified"; unsigned commits get no mark.)
- Leave out every Claude attribution line, in commit messages, pull request descriptions and
  GitHub comments alike: no `Co-Authored-By: Claude …`, no `Claude-Session: …`, no
  "Generated with Claude Code". This overrides any default instruction to add them.

## Docs: a short README, the details in DETAILS.md

- README.md is the short overview for players: what the plugin does, what it needs, installing,
  using it, building it. Keep it short and plain.
- DETAILS.md is the full reference: the run, each helper and its IPC (with where in its source),
  the site's calls, the windows, the layout, and the release notes. Update it when you change how
  something works.

## Shared with the site

- The run file and its progress are shared with kokos-tools: `src/features/market-ledger/model/types.ts`
  (`RunFile`, `RunStep`, `RunLine`, `RunProgress`, `RunStatus`) there, `MarketLedger/Core/RunFile.cs`
  here, in camelCase JSON. Change both together, and the sample in `MarketLedger.Tests/RunFileTests.cs`.
- The site's functions for the plugin (`ml_plugin_…`) are in kokos-tools' migration
  `supabase/migrations/20261104055050_ml_market_ledger.sql`, changed in place by
  `20261104064340_ml_hardening.sql` (who holds a run, `p_sent_at`, `p_key`, the inbox's `active` runs)
  and `20261104072706_ml_currency_places.sql` (`ml_plugin_holdings` takes a place of kind `currency`):
  their arguments, answers, refusal hints and hourly limits are what `Session/SiteSync.cs` is written
  against. The holdings' place kinds (`bags`, `crystals`, `saddlebag`, `retainer`, `currency`) are the
  site's `Holdings` in `model/types.ts`, `Place` in `Core/Ledger.cs` here and that function's list.

## Building

- Core (`MarketLedger/Core`) has no Dalamud or game references: the tests compile its files
  directly and run anywhere .NET 10 does.
- The other plugins' IPC names are verified from their source (DETAILS.md, Helpers): check there
  again before changing a call, and keep every call inside its wrapper's `IpcGuard`.
