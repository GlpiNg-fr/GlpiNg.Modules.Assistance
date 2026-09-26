using GlpiNg.Modules.Abstractions.Menu;

namespace GlpiNg.Modules.Assistance;

/// <summary>
/// Contribue les entrées du groupe « Assistance » que ce module met en service, là où l'hôte les
/// affichait sans lien faute de page derrière — même montage qu'<c>InventoryMenuProvider</c>.
///
/// Tout le groupe « Assistance » vient désormais d'ici : l'hôte n'en déclare plus aucune entrée.
/// </summary>
public sealed class AssistanceMenuProvider : IMenuProvider
{
    public IReadOnlyList<MenuGroup> GetMenuGroups() =>
    [
        new("assistance", "ti-headset", "Assistance",
        [
            new("Tickets", "/assistance/tickets", "ti-ticket"),
            new("Problèmes", "/assistance/problems", "ti-bulb"),
            new("Changements", "/assistance/changes", "ti-replace"),
            new("Planning", "/assistance/planning", "ti-calendar"),
            new("Statistiques", "/assistance/statistics", "ti-chart-bar"),
            new("Catégories ITIL", "/assistance/categories", "ti-category"),
        ]),

        // Les niveaux de service se définissent en Configuration, et s'appliquent en Assistance :
        // un module peut contribuer à plusieurs groupes, l'hôte les fusionnant par clé.
        new("configuration", "ti-settings", "Configuration",
        [
            new("Niveaux de services", "/config/service-levels", "ti-clipboard-check"),
            new("Calendriers", "/config/calendars", "ti-calendar-time"),
        ]),
    ];
}
