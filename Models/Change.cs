using GlpiNg.Modules.Abstractions.Entities;
using GlpiNg.Modules.Abstractions.Localization;

namespace GlpiNg.Modules.Assistance.Models;

/// <summary>
/// Cycle de vie d'un changement, repris de GLPI (<c>glpi_changes.status</c>) avec ses valeurs
/// numériques. Plus long que celui du problème parce qu'un changement se décide avant de se faire :
/// évaluation, approbation, puis test et qualification avant l'application, et une revue après.
/// </summary>
public enum ChangeStatus
{
    New = 1,

    /// <summary>En attente d'un tiers : le changement est suspendu.</summary>
    Waiting = 4,

    /// <summary>« Appliqué » dans GLPI : le changement est en place.</summary>
    Solved = 5,

    Closed = 6,

    /// <summary>Approuvé, prêt à être préparé.</summary>
    Accepted = 7,

    /// <summary>« Revue » dans GLPI : appliqué, on vérifie après coup qu'il a produit l'effet voulu.</summary>
    Observed = 8,

    /// <summary>On mesure ce qu'il coûte et ce qu'il risque.</summary>
    Evaluation = 9,

    /// <summary>Soumis à approbation (voir <see cref="ChangeValidation"/>).</summary>
    Approval = 10,

    Test = 11,
    Qualification = 12,

    /// <summary>Abandonné sans avoir été appliqué — distinct de « Clos », qui dit qu'il a abouti.</summary>
    Canceled = 13,
}

/// <summary>
/// État d'une approbation, et de l'approbation globale d'un changement — mêmes valeurs que
/// <c>CommonITILValidation</c> dans GLPI.
/// </summary>
public enum ChangeValidationStatus
{
    /// <summary>Aucune approbation demandée (n'a de sens que pour l'état global).</summary>
    None = 1,

    Waiting = 2,
    Accepted = 3,
    Refused = 4,
}

/// <summary>
/// Changement : une modification planifiée de l'infrastructure ou du service (<c>glpi_changes</c>).
/// Là où le problème cherche une cause, le changement la traite — et parce qu'il touche à ce qui
/// marche, il se prépare : on dit ce qu'il impacte, comment on le déploie, comment on revient en
/// arrière, et on le fait approuver avant.
///
/// Même socle que le problème (catégorie, acteurs, urgence/impact, échéance, solution), avec ce qui
/// fait sa raison d'être :
/// <list type="bullet">
/// <item>l'analyse (<c>impactcontent</c>, <c>controlistcontent</c>) et les plans
/// (<c>rolloutplancontent</c>, <c>backoutplancontent</c>, <c>checklistcontent</c>) ;</item>
/// <item>les approbations (<see cref="ChangeValidation"/>) et leur état global ;</item>
/// <item>les tickets et problèmes qui l'ont motivé (<see cref="ChangeTicket"/>, <see cref="ChangeProblem"/>).</item>
/// </list>
/// </summary>
public class Change : IEntityScoped
{
    /// <inheritdoc />
    public int? EntityId { get; set; }

    /// <inheritdoc />
    public bool IsRecursive { get; set; }

    public int Id { get; set; }

    /// <summary>Titre du changement, ce qui s'affiche partout ailleurs que sur sa fiche.</summary>
    public required string Name { get; set; }

    /// <summary>Description : ce qu'on veut changer, et pourquoi.</summary>
    public string? Content { get; set; }

    public ChangeStatus Status { get; set; } = ChangeStatus.New;

    public ItilLevel Urgency { get; set; } = ItilLevel.Medium;

    public ItilLevel Impact { get; set; } = ItilLevel.Medium;

    /// <inheritdoc cref="Ticket.Priority"/>
    public ItilLevel Priority { get; set; } = ItilLevel.Medium;

    /// <inheritdoc cref="Ticket.IsPriorityManual"/>
    public bool IsPriorityManual { get; set; }

    public int? CategoryId { get; set; }
    public TicketCategory? Category { get; set; }

    /// <summary>Qui a ouvert le changement (identifiant côté hôte, voir la remarque de <see cref="Ticket"/>).</summary>
    public int? AuthorUserId { get; set; }

    /// <summary>Technicien chargé du changement.</summary>
    public int? AssignedUserId { get; set; }

    /// <summary>Groupe chargé du changement.</summary>
    public int? AssignedGroupId { get; set; }

    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Échéance, saisie à la main.</summary>
    public DateTime? DueDate { get; set; }

    public DateTime? SolvedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Impacts : ce que le changement touche (services, utilisateurs, dépendances).</summary>
    public string? ImpactContent { get; set; }

    /// <summary>Liste de contrôle : ce qu'on vérifie pour savoir que le changement est sans risque.</summary>
    public string? ControlListContent { get; set; }

    /// <summary>Plan de déploiement : comment on l'applique, dans quel ordre.</summary>
    public string? RolloutPlanContent { get; set; }

    /// <summary>
    /// Plan de retour arrière : comment on revient à l'état d'avant si ça tourne mal. C'est le
    /// champ qui distingue le plus un changement préparé d'un changement subi.
    /// </summary>
    public string? BackoutPlanContent { get; set; }

    /// <summary>Checklist : les étapes à cocher le jour de l'application.</summary>
    public string? ChecklistContent { get; set; }

    /// <summary>
    /// État global des approbations, déduit des approbations elles-mêmes (voir
    /// <see cref="ChangeValidationRules.Global"/>) et stocké pour être filtrable en liste.
    /// </summary>
    public ChangeValidationStatus GlobalValidation { get; set; } = ChangeValidationStatus.None;

    /// <summary>Compte rendu d'application, saisi au passage en « Appliqué ».</summary>
    public string? Solution { get; set; }

    /// <summary>Nature de la solution (« Mise à jour », « Changement de configuration »...).</summary>
    public string? SolutionType { get; set; }

    /// <summary>Un changement appliqué, clos ou annulé ne compte plus dans la charge en cours.</summary>
    public bool IsOpen => Status is not (ChangeStatus.Solved or ChangeStatus.Closed or ChangeStatus.Canceled);

    /// <inheritdoc cref="Ticket.IsOverdue"/>
    public bool IsOverdue => IsOpen && DueDate is { } due && due < DateTime.Now;
}

/// <summary>
/// Approbation d'un changement demandée à un utilisateur (<c>glpi_changevalidations</c>). Une ligne
/// par avis demandé : c'est ce qui trace qui a validé quoi, quand, et avec quelle réserve.
/// </summary>
public class ChangeValidation
{
    public int Id { get; set; }

    public int ChangeId { get; set; }
    public Change? Change { get; set; }

    /// <summary>Approbateur (identifiant côté hôte) : seul lui peut répondre.</summary>
    public int ValidatorUserId { get; set; }

    public ChangeValidationStatus Status { get; set; } = ChangeValidationStatus.Waiting;

    /// <summary>Ce qui est demandé à l'approbateur.</summary>
    public string? RequestComment { get; set; }

    /// <summary>Sa réponse : la raison d'un refus, ou la réserve d'un accord.</summary>
    public string? ValidationComment { get; set; }

    /// <summary>Qui a demandé l'approbation, figé à l'écriture comme l'auteur d'un suivi.</summary>
    public string? RequesterName { get; set; }

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ValidatedAt { get; set; }
}

/// <summary>Rattachement d'un ticket à un changement (<c>glpi_changes_tickets</c>).</summary>
public class ChangeTicket
{
    public int Id { get; set; }

    public int ChangeId { get; set; }
    public Change? Change { get; set; }

    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }
}

/// <summary>
/// Rattachement d'un problème à un changement (<c>glpi_changes_problems</c>) : « ce changement
/// traite cette cause ». C'est le chaînon entre l'analyse et le correctif.
/// </summary>
public class ChangeProblem
{
    public int Id { get; set; }

    public int ChangeId { get; set; }
    public Change? Change { get; set; }

    public int ProblemId { get; set; }
    public Problem? Problem { get; set; }
}

/// <summary>Règle qui déduit l'état global des approbations d'un changement.</summary>
public static class ChangeValidationRules
{
    /// <summary>
    /// Un refus suffit à refuser, et il faut l'accord de tous pour accepter : un changement touche
    /// ce qui marche, et un approbateur qui n'a pas encore répondu n'a pas dit oui. C'est la règle
    /// de GLPI avec son réglage le plus prudent (100 % des approbations requises) ; un seuil
    /// réglable viendra avec l'écran de configuration qui le porterait.
    /// </summary>
    public static ChangeValidationStatus Global(IEnumerable<ChangeValidationStatus> validations)
    {
        List<ChangeValidationStatus> all = [.. validations];

        if (all.Count == 0)
        {
            return ChangeValidationStatus.None;
        }

        if (all.Contains(ChangeValidationStatus.Refused))
        {
            return ChangeValidationStatus.Refused;
        }

        return all.All(status => status == ChangeValidationStatus.Accepted)
            ? ChangeValidationStatus.Accepted
            : ChangeValidationStatus.Waiting;
    }
}

/// <summary>Libellés et couleurs propres au changement — voir <see cref="TicketLabels"/> pour le principe.</summary>
public static class ChangeLabels
{
    public static string For(ChangeStatus status) => status switch
    {
        ChangeStatus.New => Tr.T("Nouveau"),
        ChangeStatus.Evaluation => Tr.T("Évaluation"),
        ChangeStatus.Approval => Tr.T("Approbation"),
        ChangeStatus.Accepted => Tr.T("Accepté"),
        ChangeStatus.Waiting => Tr.T("En attente"),
        ChangeStatus.Test => Tr.T("Test"),
        ChangeStatus.Qualification => Tr.T("Qualification"),
        ChangeStatus.Solved => Tr.T("Appliqué"),
        ChangeStatus.Observed => Tr.T("Revue"),
        ChangeStatus.Closed => Tr.T("Clos"),
        ChangeStatus.Canceled => Tr.T("Annulé"),
        _ => status.ToString(),
    };

    public static string BadgeFor(ChangeStatus status) => status switch
    {
        ChangeStatus.New => "bg-azure-lt",
        ChangeStatus.Evaluation or ChangeStatus.Approval => "bg-indigo-lt",
        ChangeStatus.Accepted or ChangeStatus.Test or ChangeStatus.Qualification => "bg-blue-lt",
        ChangeStatus.Waiting => "bg-warning-lt",
        ChangeStatus.Solved => "bg-success-lt",

        // Revue : appliqué, pas encore confirmé — même logique que « Sous observation » du problème.
        ChangeStatus.Observed => "bg-purple-lt",

        ChangeStatus.Canceled => "bg-dark-lt",
        _ => "bg-secondary-lt",
    };

    public static string For(ChangeValidationStatus status) => status switch
    {
        ChangeValidationStatus.None => Tr.T("Non soumis"),
        ChangeValidationStatus.Waiting => Tr.T("En attente"),
        ChangeValidationStatus.Accepted => Tr.T("Accepté"),
        ChangeValidationStatus.Refused => Tr.T("Refusé"),
        _ => status.ToString(),
    };

    public static string BadgeFor(ChangeValidationStatus status) => status switch
    {
        ChangeValidationStatus.Waiting => "bg-warning-lt",
        ChangeValidationStatus.Accepted => "bg-success-lt",
        ChangeValidationStatus.Refused => "bg-danger-lt",
        _ => "bg-secondary-lt",
    };

    /// <summary>
    /// Ordre du cycle de vie pour les listes déroulantes : les valeurs numériques de GLPI, ajoutées
    /// au fil des versions, rangeraient « Évaluation » et « Test » après « Clos ».
    /// </summary>
    public static readonly IReadOnlyList<ChangeStatus> Lifecycle =
    [
        ChangeStatus.New,
        ChangeStatus.Evaluation,
        ChangeStatus.Approval,
        ChangeStatus.Accepted,
        ChangeStatus.Waiting,
        ChangeStatus.Test,
        ChangeStatus.Qualification,
        ChangeStatus.Solved,
        ChangeStatus.Observed,
        ChangeStatus.Closed,
        ChangeStatus.Canceled,
    ];
}
