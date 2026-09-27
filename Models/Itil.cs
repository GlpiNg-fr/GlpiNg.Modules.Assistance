using GlpiNg.Modules.Abstractions.Localization;
namespace GlpiNg.Modules.Assistance.Models;

/// <summary>
/// Échelle à cinq degrés utilisée par l'urgence, l'impact et la priorité — la même que GLPI, dont
/// les valeurs numériques sont reprises telles quelles. Partagée par tous les objets de
/// l'assistance : un incident et le problème qui l'explique se jaugent sur la même règle.
/// </summary>
public enum ItilLevel
{
    VeryLow = 1,
    Low = 2,
    Medium = 3,
    High = 4,
    VeryHigh = 5,
}

/// <summary>État d'une tâche (<c>glpi_tickettasks.state</c>).</summary>
public enum ItilTaskState
{
    ToDo = 1,
    Done = 2,
}

/// <summary>
/// Suivi : un échange consigné au fil d'un objet d'assistance (<c>glpi_itilfollowups</c>). C'est
/// l'historique de la conversation, distinct de l'historique des champs.
///
/// Polymorphe (<see cref="ItemType"/> + <see cref="ItemId"/>) comme la table de GLPI, et comme les
/// notes et les documents de GlpiNg : tickets et problèmes s'y rangent déjà, les changements s'y
/// rangeront sans nouvelle table. La contrepartie est l'absence de clé étrangère, donc la
/// suppression des suivis à la main quand leur objet disparaît — voir les pages de fiche.
/// </summary>
public class ItilFollowup
{
    public int Id { get; set; }

    public required string ItemType { get; set; }

    public int ItemId { get; set; }

    public required string Content { get; set; }

    /// <summary>Auteur, tel qu'il s'affiche : le nom est figé à l'écriture, un suivi ne se réattribue pas.</summary>
    public string? AuthorName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Suivi interne : visible des seuls intervenants, pas du demandeur. GlpiNg n'a pas encore
    /// d'interface demandeur, mais la distinction se pose dès l'écriture — la perdre obligerait à
    /// relire tous les suivis le jour où cette interface existera.
    /// </summary>
    public bool IsPrivate { get; set; }
}

/// <summary>
/// Tâche : un travail à faire, planifiable et assignable (<c>glpi_tickettasks</c>,
/// <c>glpi_problemtasks</c>). Distincte d'un suivi, qui ne fait que raconter. Polymorphe pour les
/// mêmes raisons qu'<see cref="ItilFollowup"/>.
/// </summary>
public class ItilTask
{
    public int Id { get; set; }

    public required string ItemType { get; set; }

    public int ItemId { get; set; }

    public required string Content { get; set; }

    public ItilTaskState State { get; set; } = ItilTaskState.ToDo;

    /// <summary>Technicien chargé de la tâche (identifiant côté hôte).</summary>
    public int? AssignedUserId { get; set; }

    public DateTime? PlannedStart { get; set; }
    public DateTime? PlannedEnd { get; set; }

    /// <summary>Durée réellement passée, en minutes : c'est ce qui alimentera les statistiques.</summary>
    public int? DurationMinutes { get; set; }

    public string? AuthorName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Libellés et couleurs communs aux objets de l'assistance. Les libellés propres à un type
/// (statuts d'un ticket, d'un problème) vivent chez lui.
/// </summary>
public static class ItilLabels
{
    public static string For(ItilLevel level) => level switch
    {
        ItilLevel.VeryLow => Tr.T("Très basse"),
        ItilLevel.Low => Tr.T("Basse"),
        ItilLevel.Medium => Tr.T("Moyenne"),
        ItilLevel.High => Tr.T("Haute"),
        ItilLevel.VeryHigh => Tr.T("Très haute"),
        _ => level.ToString(),
    };

    public static string For(ItilTaskState state) => state switch
    {
        ItilTaskState.ToDo => Tr.T("À faire"),
        ItilTaskState.Done => Tr.T("Terminée"),
        _ => state.ToString(),
    };

    /// <summary>
    /// Classe du badge de priorité. Seules les deux plus hautes sont colorées d'une couleur
    /// d'alerte : tout colorer revient à ne rien signaler.
    /// </summary>
    public static string BadgeFor(ItilLevel priority) => priority switch
    {
        ItilLevel.VeryHigh => "bg-danger-lt",
        ItilLevel.High => "bg-orange-lt",
        _ => "bg-secondary-lt",
    };
}

/// <summary>
/// Priorité déduite de l'urgence et de l'impact, selon la matrice par défaut de GLPI
/// (Configuration &gt; Assistance) : la diagonale donne la priorité de même rang, et les écarts se
/// rattrapent vers le centre. Une organisation qui veut sa propre matrice la réglera le jour où
/// cet écran existera ; d'ici là, mieux vaut la règle de GLPI qu'une invention maison.
/// </summary>
public static class ItilPriorityMatrix
{
    public static ItilLevel Compute(ItilLevel urgency, ItilLevel impact)
    {
        // Moyenne des deux rangs, arrondie au supérieur : c'est ce que produit la matrice par
        // défaut de GLPI, sans avoir à recopier ses vingt-cinq cases.
        int rank = (int)Math.Ceiling(((int)urgency + (int)impact) / 2.0);

        return (ItilLevel)Math.Clamp(rank, (int)ItilLevel.VeryLow, (int)ItilLevel.VeryHigh);
    }
}
