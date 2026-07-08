# Auto Challenge Swap

A [BepInEx](https://docs.bepinex.dev/) mod for **Bloobs Adventure Idle** that automatically
swaps your Challenge Tracker slots to match whatever you're currently doing, so you never have
to re-track challenges by hand.

## Features

- **Activity-aware tracking** — mine, chop, fish, cook, smith, etc. and the matching challenge
  (plus its skill XP and Total XP) is tracked automatically.
- **Combat-aware** — tracks the Hit/Damage challenges for your active combat style, and briefly
  tracks a dying enemy's Kill challenge so the kill always counts (including Superior/Golden/
  Corrupted variants).
- **Pin Total XP** so it's always in a slot (optional; auto-drops during combat if you want).
- **Respect Auto Progress** — only auto-tracks a challenge as deep as your purchased Auto Progress
  upgrade covers, so the mod never hands out free progression.
- **Lock slots** you want left alone, and exclude any skills you don't want touched.
- Manual picks are never evicted unless you opt in.

## Hotkeys

| Key | Action |
|-----|--------|
| **F8** | Toggle the mod on/off |
| **F9** | Lock/unlock the challenges currently in your slots |

## Requirements

- [BepInEx 5.4.23.2 (x64, Mono)](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.2)
- Optional: [ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager) to
  tweak settings in-game with **F1**.

## Install

1. Install BepInEx 5.4.23.2 (x64) into your game folder and launch the game once to generate the
   `BepInEx/plugins` folder.
2. Download `AutoChallengeSwap.dll` from the [latest release](../../releases/latest).
3. Drop it into `Bloobs Adventure Idle/BepInEx/plugins/`.
4. Launch the game.

## Building

The `.csproj` references the game's assemblies via a `GameDir` property. Edit `GameDir` in
`AutoChallengeSwap.csproj` to point at your install, then:

```
dotnet build -c Release
```

The DLL is produced in `bin/Release/`.

## License

MIT — see [LICENSE](LICENSE).
