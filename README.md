# GlpiNg.Modules.Assistance

*[English version](README.en.md)*

Module Assistance de GlpiNg : tickets, problèmes et changements, sur le modèle de GLPI.

> **Avertissement** — GlpiNg est un projet indépendant. Il n'est ni affilié à, ni approuvé,
> soutenu ou sponsorisé par Teclib' ou le projet GLPI. « GLPI » et « GLPI-Agent » sont des
> marques de leurs propriétaires respectifs ; elles ne sont citées ici que pour décrire la
> compatibilité de GlpiNg avec le protocole GLPI-Agent et l'import depuis une base GLPI.

## Contenu

- Tickets, problèmes, changements et leurs liens
- Suivis, tâches, solutions et validations
- Catégories, matrice urgence × impact
- Calendriers et niveaux de service (échéances, escalade)
- Planning des tâches et statistiques

## Utilisation

Ce dépôt est un sous-module de [GlpiNg](https://github.com/GlpiNg-fr/GlpiNg), sous
`src/GlpiNg.Modules.Assistance`. Il ne se compile pas seul : il référence `GlpiNg.Modules.Abstractions` par chemin relatif.

```bash
git clone --recurse-submodules https://github.com/GlpiNg-fr/GlpiNg.git
```

L'hôte l'enregistre par `services.AddAssistanceModule()` (voir `Program.cs`).

## Licence

[GNU Affero General Public License v3.0](LICENSE).
