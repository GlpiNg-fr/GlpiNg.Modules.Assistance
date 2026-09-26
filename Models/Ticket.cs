using GlpiNg.Modules.Abstractions.Entities;

namespace GlpiNg.Modules.Assistance.Models;

/// <summary>Nature de la demande (<c>type</c> côté GLPI : 1 incident, 2 demande).</summary>
public enum TicketType
{
    Incident = 1,
    Request = 2,
}

/// <summary>
/// Cycle de vie d'un ticket, repris de GLPI (<c>glpi_tickets.status</c>) avec ses valeurs
/// numériques, pour qu'un import puisse un jour recopier la colonne.
/// </summary>
public enum TicketStatus
{
    New = 1,
    Assigned = 2,
    Planned = 3,

    /// <summary>En attente d'un tiers (demandeur, fournisseur) : le compteur de traitement s'arrête.</summary>
    Waiting = 4,

    Solved = 5,
    Closed = 6,
}

/// <summary>
/// Demande d'assistance : incident à réparer ou demande de service (<c>glpi_tickets</c>).
///
/// Les acteurs (demandeur, technicien, groupe) sont désignés par leur identifiant, sans clé
/// étrangère : utilisateurs, groupes et entités vivent chez l'hôte, qu'un module ne référence pas —
/// c'est <c>IPrincipalDirectory</c> qui en donne les listes et les noms. Un acteur supprimé laisse
/// donc un identifiant orphelin, que l'affichage rend « (supprimé) » plutôt que de perdre le
/// ticket avec lui.
///
/// Suivis et tâches ne sont pas des collections de navigation : ils vivent dans les tables
/// polymorphes <see cref="ItilFollowup"/> / <see cref="ItilTask"/>, lues par le couple
/// type/identifiant comme l'historique.
/// </summary>
public class Ticket : IEntityScoped
{
    /// <inheritdoc />
    public int? EntityId { get; set; }

    /// <inheritdoc />
    public bool IsRecursive { get; set; }

    public int Id { get; set; }

    /// <summary>Titre du ticket, ce qui s'affiche partout ailleurs que sur sa fiche.</summary>
    public required string Name { get; set; }

    /// <summary>Description initiale, telle que le demandeur l'a formulée.</summary>
    public string? Content { get; set; }

    public TicketType Type { get; set; } = TicketType.Incident;

    public TicketStatus Status { get; set; } = TicketStatus.New;

    /// <summary>Gêne subie par le demandeur.</summary>
    public ItilLevel Urgency { get; set; } = ItilLevel.Medium;

    /// <summary>Étendue des conséquences (une personne, un service, toute l'entité).</summary>
    public ItilLevel Impact { get; set; } = ItilLevel.Medium;

    /// <summary>
    /// Priorité de traitement. Calculée à partir de l'urgence et de l'impact
    /// (<see cref="ItilPriorityMatrix"/>) tant que <see cref="IsPriorityManual"/> est faux ;
    /// une priorité forcée à la main ne doit pas se faire écraser au prochain enregistrement.
    /// </summary>
    public ItilLevel Priority { get; set; } = ItilLevel.Medium;

    /// <inheritdoc cref="Priority"/>
    public bool IsPriorityManual { get; set; }

    public int? CategoryId { get; set; }
    public TicketCategory? Category { get; set; }

    /// <summary>Utilisateur demandeur (identifiant côté hôte — voir la remarque de classe).</summary>
    public int? RequesterUserId { get; set; }

    /// <summary>Demandeur quand ce n'est pas un compte de l'application (appelant externe).</summary>
    public string? RequesterName { get; set; }

    /// <summary>Technicien à qui le ticket est attribué.</summary>
    public int? AssignedUserId { get; set; }

    /// <summary>Groupe à qui le ticket est attribué, quand il l'est collectivement.</summary>
    public int? AssignedGroupId { get; set; }

    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Échéance de traitement saisie à la main. Elle coexiste avec celles des niveaux de service
    /// ci-dessous plutôt que d'être remplacée par elles : tout ticket n'a pas de SLA, et une date
    /// posée à la main reste le moyen de s'engager sur un cas particulier.
    /// </summary>
    public DateTime? DueDate { get; set; }

    // Niveaux de service. Quatre engagements possibles, deux par deux : envers le demandeur (SLA)
    // et en interne (OLA), sur la prise en charge (TTO) et sur la résolution (TTR). Les échéances
    // sont calculées une fois, à la pose de l'engagement, et stockées : les recalculer à chaque
    // affichage les ferait bouger au gré des modifications du calendrier, et une échéance qui se
    // déplace n'engage plus personne.

    public int? SlaTimeToOwnId { get; set; }
    public ServiceLevelAgreement? SlaTimeToOwn { get; set; }

    public int? SlaTimeToResolveId { get; set; }
    public ServiceLevelAgreement? SlaTimeToResolve { get; set; }

    public int? OlaTimeToOwnId { get; set; }
    public ServiceLevelAgreement? OlaTimeToOwn { get; set; }

    public int? OlaTimeToResolveId { get; set; }
    public ServiceLevelAgreement? OlaTimeToResolve { get; set; }

    /// <summary>Échéance de prise en charge due au demandeur.</summary>
    public DateTime? TimeToOwn { get; set; }

    /// <summary>Échéance de résolution due au demandeur.</summary>
    public DateTime? TimeToResolve { get; set; }

    /// <summary>Échéance interne de prise en charge.</summary>
    public DateTime? InternalTimeToOwn { get; set; }

    /// <summary>Échéance interne de résolution.</summary>
    public DateTime? InternalTimeToResolve { get; set; }

    /// <summary>
    /// Moment où le ticket a été pris en charge — première attribution à un technicien. C'est ce
    /// qui arrête le compteur de prise en charge ; une fois posé, il ne bouge plus, un ticket
    /// réattribué ayant déjà été pris en charge.
    /// </summary>
    public DateTime? TakenIntoAccountAt { get; set; }

    /// <summary>
    /// Départ des compteurs internes (OLA). L'engagement interne court depuis l'attribution, pas
    /// depuis l'ouverture : c'est ce qui le distingue du SLA.
    /// </summary>
    public DateTime? OlaStartedAt { get; set; }

    public DateTime? SolvedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Solution apportée, saisie au passage en « Résolu ».</summary>
    public string? Solution { get; set; }

    /// <summary>Nature de la solution (« Correctif », « Contournement », « Sans suite »...).</summary>
    public string? SolutionType { get; set; }

    /// <summary>Un ticket résolu ou clos ne compte plus dans la charge en cours.</summary>
    public bool IsOpen => Status is not (TicketStatus.Solved or TicketStatus.Closed);

    /// <summary>
    /// Échéance dépassée sur un ticket encore ouvert : c'est ce qui doit sauter aux yeux dans une
    /// liste, un ticket clos en retard n'étant plus actionnable. Les engagements de niveau de
    /// service comptent au même titre que l'échéance saisie à la main.
    /// </summary>
    /// <remarks>
    /// <see cref="DueDate"/> se compare à l'heure locale, et non en UTC comme les échéances de
    /// niveau de service : elle est saisie à la main dans un champ « datetime-local » et stockée
    /// telle quelle. Deux référentiels pour deux provenances, tant que les dates saisies ne sont
    /// pas converties à l'entrée.
    /// </remarks>
    public bool IsOverdue => IsOpen && (
        (DueDate is { } due && due < DateTime.Now)
        || IsTimeToOwnBreached
        || IsTimeToResolveBreached);

    /// <summary>
    /// Prise en charge manquée : l'échéance est passée et personne n'avait encore pris le ticket.
    /// Un ticket pris en charge à temps ne redevient jamais en faute, d'où la comparaison avec
    /// <see cref="TakenIntoAccountAt"/> plutôt qu'avec l'heure courante seule.
    ///
    /// Comparaison en UTC, comme toutes celles qui suivent : les échéances de niveau de service et
    /// les dates de prise en charge et de résolution sont posées en UTC. Les confronter à
    /// <c>DateTime.Now</c> décalerait le constat de retard du fuseau — deux heures en été.
    /// </summary>
    public bool IsTimeToOwnBreached =>
        TimeToOwn is { } deadline && (TakenIntoAccountAt ?? DateTime.UtcNow) > deadline;

    /// <inheritdoc cref="IsTimeToOwnBreached"/>
    public bool IsInternalTimeToOwnBreached =>
        InternalTimeToOwn is { } deadline && (TakenIntoAccountAt ?? DateTime.UtcNow) > deadline;

    /// <summary>
    /// Résolution manquée. Le ticket résolu est jugé sur sa date de résolution, celui encore
    /// ouvert sur l'heure courante.
    /// </summary>
    public bool IsTimeToResolveBreached =>
        TimeToResolve is { } deadline && (SolvedAt ?? DateTime.UtcNow) > deadline;

    /// <inheritdoc cref="IsTimeToResolveBreached"/>
    public bool IsInternalTimeToResolveBreached =>
        InternalTimeToResolve is { } deadline && (SolvedAt ?? DateTime.UtcNow) > deadline;

    /// <summary>Le ticket porte-t-il un engagement, quel qu'il soit ?</summary>
    public bool HasServiceLevel =>
        SlaTimeToOwnId is not null || SlaTimeToResolveId is not null
        || OlaTimeToOwnId is not null || OlaTimeToResolveId is not null;
}

/// <summary>
/// Catégorie ITIL (<c>glpi_itilcategories</c>), hiérarchique : « Matériel », puis « Matériel &gt;
/// Imprimante ». La hiérarchie est portée par un simple parent, la profondeur des arbres de
/// catégories restant de deux ou trois niveaux en pratique.
///
/// Commune aux tickets et aux problèmes, comme dans GLPI : un problème se range sous la même
/// catégorie que les incidents qu'il explique, faute de quoi le rapprochement serait impossible.
/// </summary>
public class TicketCategory : IEntityScoped
{
    /// <inheritdoc />
    public int? EntityId { get; set; }

    /// <inheritdoc />
    public bool IsRecursive { get; set; }

    public int Id { get; set; }

    public required string Name { get; set; }

    public int? ParentId { get; set; }
    public TicketCategory? Parent { get; set; }

    public string? Comment { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Modification d'un objet de l'assistance, une ligne par champ touché. Polymorphe (type +
/// identifiant) comme l'historique du module Gestion et comme <c>glpi_logs</c> : les problèmes s'y
/// rangent déjà, les changements à venir s'y rangeront sans nouvelle table.
/// </summary>
public class AssistanceHistoryEntry
{
    public int Id { get; set; }

    public required string ItemType { get; set; }

    public int ItemId { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public string? User { get; set; }

    public required string Field { get; set; }

    public string? Description { get; set; }
}
