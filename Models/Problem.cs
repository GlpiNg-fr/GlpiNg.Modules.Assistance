using GlpiNg.Modules.Abstractions.Entities;
using GlpiNg.Modules.Abstractions.Localization;

namespace GlpiNg.Modules.Assistance.Models;

/// <summary>
/// Cycle de vie d'un problème, repris de GLPI (<c>glpi_problems.status</c>) avec ses valeurs
/// numériques. Deux statuts de plus que le ticket, et ce ne sont pas des doublons : « Accepté » dit
/// qu'un problème soumis est retenu pour analyse, « Sous observation » qu'un correctif est posé
/// mais pas encore éprouvé — l'étape où l'on attend de voir si les incidents cessent.
/// </summary>
public enum ProblemStatus
{
    New = 1,
    Assigned = 2,
    Planned = 3,

    /// <summary>En attente d'un tiers (éditeur, fournisseur) : le compteur d'analyse s'arrête.</summary>
    Waiting = 4,

    Solved = 5,
    Closed = 6,

    /// <summary>Retenu pour analyse.</summary>
    Accepted = 7,

    /// <summary>Correctif posé, effet pas encore confirmé.</summary>
    Observed = 8,
}

/// <summary>
/// Problème : la cause commune d'un ou plusieurs incidents (<c>glpi_problems</c>). Là où un ticket
/// répare un cas, un problème cherche pourquoi il se répète.
///
/// Même structure que le ticket pour ce qui est commun (catégorie, acteurs, urgence/impact,
/// échéance, solution), avec trois différences qui font sa raison d'être :
/// <list type="bullet">
/// <item>pas de demandeur mais un rédacteur : un problème est ouvert par le service, pas subi ;</item>
/// <item>l'analyse en trois temps — symptômes, cause, conséquences (<c>symptomcontent</c>,
/// <c>causecontent</c>, <c>impactcontent</c> de GLPI) ;</item>
/// <item>les tickets qu'il explique (<see cref="ProblemTicket"/>).</item>
/// </list>
/// </summary>
public class Problem : IEntityScoped
{
    /// <inheritdoc />
    public int? EntityId { get; set; }

    /// <inheritdoc />
    public bool IsRecursive { get; set; }

    public int Id { get; set; }

    /// <summary>Titre du problème, ce qui s'affiche partout ailleurs que sur sa fiche.</summary>
    public required string Name { get; set; }

    /// <summary>Énoncé du problème, tel qu'il a été posé à l'ouverture.</summary>
    public string? Content { get; set; }

    public ProblemStatus Status { get; set; } = ProblemStatus.New;

    /// <summary>Gêne causée tant que le problème dure.</summary>
    public ItilLevel Urgency { get; set; } = ItilLevel.Medium;

    /// <summary>Étendue des conséquences (un service, une entité, tout le parc).</summary>
    public ItilLevel Impact { get; set; } = ItilLevel.Medium;

    /// <inheritdoc cref="Ticket.Priority"/>
    public ItilLevel Priority { get; set; } = ItilLevel.Medium;

    /// <inheritdoc cref="Priority"/>
    public bool IsPriorityManual { get; set; }

    public int? CategoryId { get; set; }
    public TicketCategory? Category { get; set; }

    /// <summary>Qui a ouvert le problème (identifiant côté hôte, voir la remarque de <see cref="Ticket"/>).</summary>
    public int? AuthorUserId { get; set; }

    /// <summary>Technicien chargé de l'analyse.</summary>
    public int? AssignedUserId { get; set; }

    /// <summary>Groupe chargé de l'analyse, quand elle l'est collectivement.</summary>
    public int? AssignedGroupId { get; set; }

    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Échéance d'analyse, saisie à la main.</summary>
    public DateTime? DueDate { get; set; }

    public DateTime? SolvedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Ce qui se constate : les manifestations rapportées par les incidents.</summary>
    public string? SymptomContent { get; set; }

    /// <summary>Ce qui l'explique : la cause racine, une fois trouvée.</summary>
    public string? CauseContent { get; set; }

    /// <summary>Ce que ça coûte : périmètre touché, tant que le problème dure.</summary>
    public string? ImpactContent { get; set; }

    /// <summary>
    /// Contournement : ce qui rend le service acceptable sans traiter la cause. Propre au problème —
    /// c'est ce qu'un technicien applique sur les incidents en attendant le correctif.
    /// </summary>
    public string? Workaround { get; set; }

    /// <summary>Solution de fond, saisie au passage en « Résolu ».</summary>
    public string? Solution { get; set; }

    /// <summary>Nature de la solution (« Correctif éditeur », « Changement de configuration »...).</summary>
    public string? SolutionType { get; set; }

    /// <summary>Un problème résolu ou clos ne compte plus dans la charge en cours.</summary>
    public bool IsOpen => Status is not (ProblemStatus.Solved or ProblemStatus.Closed);

    /// <inheritdoc cref="Ticket.IsOverdue"/>
    public bool IsOverdue => IsOpen && DueDate is { } due && due < DateTime.Now;
}

/// <summary>
/// Rattachement d'un ticket à un problème (<c>glpi_problems_tickets</c>) : « cet incident a cette
/// cause ». C'est le lien qui justifie le module — sans lui, un problème n'est qu'un ticket au nom
/// différent.
/// </summary>
public class ProblemTicket
{
    public int Id { get; set; }

    public int ProblemId { get; set; }
    public Problem? Problem { get; set; }

    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }
}

/// <summary>Libellés et couleurs propres au problème — voir <see cref="TicketLabels"/> pour le principe.</summary>
public static class ProblemLabels
{
    public static string For(ProblemStatus status) => status switch
    {
        ProblemStatus.New => Tr.T("Nouveau"),
        ProblemStatus.Accepted => Tr.T("Accepté"),
        ProblemStatus.Assigned => Tr.T("En cours (attribué)"),
        ProblemStatus.Planned => Tr.T("En cours (planifié)"),
        ProblemStatus.Waiting => Tr.T("En attente"),
        ProblemStatus.Solved => Tr.T("Résolu"),
        ProblemStatus.Observed => Tr.T("Sous observation"),
        ProblemStatus.Closed => Tr.T("Clos"),
        _ => status.ToString(),
    };

    public static string BadgeFor(ProblemStatus status) => status switch
    {
        ProblemStatus.New => "bg-azure-lt",
        ProblemStatus.Accepted or ProblemStatus.Assigned or ProblemStatus.Planned => "bg-blue-lt",
        ProblemStatus.Waiting => "bg-warning-lt",

        // Sous observation : résolu sur le papier, pas encore confirmé — d'où une couleur qui n'est
        // ni celle du traitement en cours ni le vert de ce qui est acquis.
        ProblemStatus.Observed => "bg-purple-lt",

        ProblemStatus.Solved => "bg-success-lt",
        ProblemStatus.Closed => "bg-secondary-lt",
        _ => "bg-secondary-lt",
    };

    /// <summary>
    /// Ordre d'affichage des statuts dans les listes déroulantes. Les valeurs numériques de GLPI
    /// mettraient « Accepté » et « Sous observation » en fin de liste, loin de l'étape qu'ils
    /// suivent : on les remet dans l'ordre du cycle de vie.
    /// </summary>
    public static readonly IReadOnlyList<ProblemStatus> Lifecycle =
    [
        ProblemStatus.New,
        ProblemStatus.Accepted,
        ProblemStatus.Assigned,
        ProblemStatus.Planned,
        ProblemStatus.Waiting,
        ProblemStatus.Solved,
        ProblemStatus.Observed,
        ProblemStatus.Closed,
    ];
}
