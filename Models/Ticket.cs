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

    /// <summary>Échéance de traitement. Saisie à la main : les niveaux de service (SLA) n'existent pas encore.</summary>
    public DateTime? DueDate { get; set; }

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
    /// liste, un ticket clos en retard n'étant plus actionnable.
    /// </summary>
    public bool IsOverdue => IsOpen && DueDate is { } due && due < DateTime.Now;
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
