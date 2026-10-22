## What and why

<!-- What the change does for the player, and the feature's config key. -->

## Fair play and compatibility

- [ ] Shows, remembers or eases only what an unmodded player can already see or do
- [ ] Reveals no hidden information and aims, flies, fires or reacts for no one
- [ ] Touches no networking (SyncVars, RPCs, messages, serialization) and no simulation stuff
- [ ] Callers of every patched method checked and listed in this description
- [ ] Turning the feature off restores vanilla without a restart (or says why not)
- [ ] Changes to what the player sees ship off by default

## Checks

- [ ] `dotnet build -p:EnforceCodeStyleInBuild=true` has no warnings
- [ ] `<Version>` bumped if a player would notice the change
- [ ] Tested in game: <!-- game version, single player or multiplayer, what you tried -->
