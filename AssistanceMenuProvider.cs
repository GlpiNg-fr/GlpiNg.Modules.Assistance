using GlpiNg.Modules.Abstractions.Menu;

namespace GlpiNg.Modules.Assistance;

/// <summary>
/// Contribue les entrées du groupe « Assistance » que ce module met en service, là où l'hôte les
/// affichait sans lien faute de page derrière — même montage qu'<c>InventoryMenuProvider</c>.
///
/// Changements, Planning et Statistiques restent déclarés par l'hôte, sans adresse : ils se voient
/// mais ne mènent nulle part, ce qui dit ce qu'il reste à écrire.
/// </summary>
public sealed class AssistanceMenuProvider : IMenuProvider
{
    public IReadOnlyList<MenuGroup> GetMenuGroups() =>
    [
        new("assistance", "ti-headset", "Assistance",
        [
            new("Tickets", "/assistance/tickets", "ti-ticket"),
            new("Problèmes", "/assistance/problems", "ti-bulb"),
            new("Catégories ITIL", "/assistance/categories", "ti-category"),
        ]),
    ];
}
