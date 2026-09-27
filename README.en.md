# GlpiNg.Modules.Assistance

*[Version française](README.md)*

GlpiNg's Assistance module: tickets, problems and changes, modelled on GLPI.

> **Disclaimer** — GlpiNg is an independent project. It is not affiliated with, endorsed,
> supported or sponsored by Teclib' or the GLPI project. "GLPI" and "GLPI-Agent" are trademarks
> of their respective owners; they are mentioned here only to describe GlpiNg's compatibility
> with the GLPI-Agent protocol and import from a GLPI database.

## Contents

- Tickets, problems, changes and the links between them
- Followups, tasks, solutions and approvals
- Categories, urgency × impact matrix
- Calendars and service levels (due dates, escalation)
- Task planning and statistics

## Usage

This repository is a submodule of [GlpiNg](https://github.com/GlpiNg-fr/GlpiNg), under
`src/GlpiNg.Modules.Assistance`. It does not build on its own: it references `GlpiNg.Modules.Abstractions` by relative path.

```bash
git clone --recurse-submodules https://github.com/GlpiNg-fr/GlpiNg.git
```

The host registers it with `services.AddAssistanceModule()` (see `Program.cs`).

## License

[GNU Affero General Public License v3.0](LICENSE).
