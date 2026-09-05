# StellarWardrobeLoadout

A [StellarResonance](https://github.com/StellarProtocol/StellarResonanceModSystem) plugin for
saving worn cosmetic outfits and switching between them quickly by hotkey or an overlay select
menu.

Source repo. Built and deployed through the StellarResonance DevKit; consumed via
`Stellar.Abstractions` only.

## Where your outfits are stored

Saved outfits are **user data**, so they live in the plugin's own data store —
`<game_mini>/stellar/plugindata/stellar.wardrobeloadout.data/outfits.json` (the framework's
`IPluginDataStore`), one compact UTF-8 JSON document keyed by character name. They are **not** in
the shared `stellar/plugins/stellar.wardrobeloadout.config.json` any more (owner ruling
2026-09-05: plugins keep their own data; the config file is for settings). The plugindata write is
synchronous and atomic (temp file + rename), and the directory is outside both the config file
watcher and the plugin-DLL scan path, so saving an outfit no longer rewrites and re-broadcasts a
55 KB settings file.

Outfits saved by an earlier build are **migrated automatically** the first time this build starts,
and the plugin logs one line:

```
[WardrobeLoadout] migrated 53 outfits for 1 characters from config to plugindata (config key 'wardrobe.outfits' left in place, frozen, so a rollback still finds them)
```

The migration only ever **copies**. The old `wardrobe.outfits` config key is left exactly as it was
and is never written again, so rolling back to an older build still shows the outfits you had when
the migration ran; coming back to this build ignores that frozen copy, because the plugindata file
now exists and always wins.
