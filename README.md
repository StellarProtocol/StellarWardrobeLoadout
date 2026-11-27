# StellarWardrobeLoadout

A [StellarResonance](https://github.com/StellarProtocol/StellarResonanceModSystem) plugin for
saving worn cosmetic outfits and switching between them quickly by hotkey or an overlay select
menu.

Source repo. Built and deployed through the StellarResonance DevKit; consumed via
`Stellar.Abstractions` only.

## What an outfit remembers

- The pieces you are wearing, one per wardrobe slot.
- Your class's weapon skin.
- Which pieces are hidden (the game's show/hide toggles).
- The exact colours of every piece. When you wear the outfit, each piece whose colour changed since is
  put back by applying the matching **Dyeing Scheme** you stored on that piece in the game (free). A
  piece with no matching Dyeing Scheme keeps its current colour, and you get one notice. The plugin
  never dyes for you, because dyeing costs currency. Outfits saved before this feature keep their
  colours as they are until you save or update them again.

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
