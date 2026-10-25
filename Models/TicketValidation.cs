namespace GlpiNg.Modules.Assistance.Models;

/// <summary>
/// Demande d'approbation d'un ticket (<c>glpi_ticketvalidations</c>). Même cycle qu'une
/// <see cref="ChangeValidation"/> — en attente, acceptée, refusée —, dont elle reprend le statut.
/// Version légère : pas d'état global rattaché au ticket ni de notification, seulement qui a
/// demandé quoi à qui, et ce qu'il a répondu.
/// </summary>
public class TicketValidation
{
    public int Id { get; set; }

    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>Approbateur (identifiant côté hôte) : seul lui peut répondre.</summary>
    public int ValidatorUserId { get; set; }

    public ChangeValidationStatus Status { get; set; } = ChangeValidationStatus.Waiting;

    /// <summary>Ce qui est demandé à l'approbateur.</summary>
    public string? RequestComment { get; set; }

    /// <summary>Sa réponse : la raison d'un refus, ou la réserve d'un accord.</summary>
    public string? ValidationComment { get; set; }

    /// <summary>Qui a demandé l'approbation, figé à l'écriture.</summary>
    public string? RequesterName { get; set; }

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ValidatedAt { get; set; }
}
