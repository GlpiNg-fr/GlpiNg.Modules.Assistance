using GlpiNg.Modules.Abstractions.Localization;
namespace GlpiNg.Modules.Assistance.Models;

/// <summary>
/// Libellés et couleurs propres au ticket, au même endroit : la liste, la fiche et les filtres les
/// affichent tous, et trois jeux de libellés finiraient par ne plus dire la même chose. L'échelle
/// urgence/impact/priorité, elle, est commune à toute l'assistance — voir <see cref="ItilLabels"/>.
/// </summary>
public static class TicketLabels
{
    public static string For(TicketType type) => type switch
    {
        TicketType.Incident => Tr.T("Incident"),
        TicketType.Request => Tr.T("Demande"),
        _ => type.ToString(),
    };

    public static string For(TicketStatus status) => status switch
    {
        TicketStatus.New => Tr.T("Nouveau"),
        TicketStatus.Assigned => Tr.T("En cours (attribué)"),
        TicketStatus.Planned => Tr.T("En cours (planifié)"),
        TicketStatus.Waiting => Tr.T("En attente"),
        TicketStatus.Solved => Tr.T("Résolu"),
        TicketStatus.Closed => Tr.T("Clos"),
        _ => status.ToString(),
    };

    /// <summary>Classe du badge de statut : le vert dit « fini », le gris « clos », l'orange « bloqué ».</summary>
    public static string BadgeFor(TicketStatus status) => status switch
    {
        TicketStatus.New => "bg-azure-lt",
        TicketStatus.Assigned or TicketStatus.Planned => "bg-blue-lt",
        TicketStatus.Waiting => "bg-warning-lt",
        TicketStatus.Solved => "bg-success-lt",
        TicketStatus.Closed => "bg-secondary-lt",
        _ => "bg-secondary-lt",
    };
}
