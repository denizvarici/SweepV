# SweepV

Open-source disk cleanup tool for Windows — built for end users with no technical
background who want to reclaim space on their C: drive safely, without risking
their system.

## Why SweepV?

Most disk analysis tools (like WizTree) show you what's taking up space, but leave
the "is this safe to delete?" decision entirely up to you. SweepV aims to go a step
further: categorize what it finds by safety level, and guide non-technical users
toward safe cleanup decisions instead of just raw data.

## Status

🚧 Early development. Core scanning engine and basic UI are functional. Not yet
ready for general use.

## Tech stack

- **.NET 10** (LTS)
- **WPF** with MVVM (CommunityToolkit.Mvvm)
- **xUnit** for testing

## Project structure
src/
├── SweepV.App/ # WPF UI (Views, ViewModels)
├── SweepV.Core/ # Core logic (scanning, models) — UI-independent
└── SweepV.Core.Tests/ # Unit tests for Core

## Getting started

1. Clone the repo
2. Open `SweepV.sln` in Visual Studio 2022+ (or run `dotnet build` from the root)
3. Run the `SweepV.App` project

## Roadmap

- [ ] Safe-cleanup-target database (known safe locations: Temp, Windows.old, etc.)
- [ ] Treemap visualization
- [ ] Recycle Bin integration with undo support
- [ ] MFT-based fast scanning
- [ ] AI-assisted file explanation (v2, opt-in, user-provided API key)

## Contributing

The easiest way to help is adding cleanup locations: the whole Quick Clean list is a
single JSON file, no C# needed. See
[Contributing cleanup locations](docs/CONTRIBUTING-CLEANUP-TARGETS.md).

Issues and discussions are welcome too.

## License

MIT