using GlpiNg.Modules.Abstractions.Entities;

namespace GlpiNg.Modules.Assistance.Models;

/// <summary>
/// Ce que l'engagement mesure : la prise en charge, ou la résolution
/// (<c>type</c> de <c>glpi_slas</c> : 1 = TTO, 0 = TTR).
/// </summary>
public enum ServiceLevelTarget
{
    /// <summary>Time To Own : délai avant qu'un technicien prenne le ticket en charge.</summary>
    TimeToOwn = 1,

    /// <summary>Time To Resolve : délai avant que le ticket soit résolu.</summary>
    TimeToResolve = 0,
}

/// <summary>
/// À qui l'engagement est pris. GLPI en fait deux tables jumelles (<c>glpi_slas</c>,
/// <c>glpi_olas</c>) ; elles ont la même forme au champ près, d'où une seule table et ce
/// discriminant — deux tables identiques obligeraient à écrire deux fois chaque écran, chaque
/// calcul et chaque escalade.
/// </summary>
public enum ServiceLevelKind
{
    /// <summary>SLA : engagement pris envers le demandeur. Le compteur part de l'ouverture du ticket.</summary>
    Sla = 1,

    /// <summary>
    /// OLA : engagement interne, entre équipes. Le compteur part de l'attribution, pas de
    /// l'ouverture — c'est toute la différence, et c'est pourquoi un ticket porte les deux
    /// échéances sans qu'elles se confondent.
    /// </summary>
    Ola = 2,
}

/// <summary>Unité de la durée d'un engagement (<c>definition_time</c> de GLPI).</summary>
public enum ServiceLevelUnit
{
    Minute = 1,
    Hour = 2,
    Day = 3,
}

/// <summary>
/// Niveau de service (<c>glpi_slms</c>) : le contenant qui regroupe les engagements pris dans un
/// même cadre (« Support standard », « Support étendu ») et porte le calendrier sur lequel ils se
/// comptent.
/// </summary>
public class ServiceLevel : IEntityScoped
{
    /// <inheritdoc />
    public int? EntityId { get; set; }

    /// <inheritdoc />
    public bool IsRecursive { get; set; }

    public int Id { get; set; }

    public required string Name { get; set; }

    public string? Comment { get; set; }

    /// <summary>
    /// Calendrier appliqué à tous les engagements du niveau. Absent, les durées se comptent en
    /// temps réel — voir <see cref="Services.WorkingTimeCalculator"/>.
    /// </summary>
    public int? CalendarId { get; set; }
    public Calendar? Calendar { get; set; }

    public List<ServiceLevelAgreement> Agreements { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Engagement de délai : un SLA ou un OLA, sur la prise en charge ou sur la résolution
/// (<c>glpi_slas</c> / <c>glpi_olas</c>).
/// </summary>
public class ServiceLevelAgreement
{
    public int Id { get; set; }

    public int ServiceLevelId { get; set; }
    public ServiceLevel? ServiceLevel { get; set; }

    public required string Name { get; set; }

    public ServiceLevelKind Kind { get; set; } = ServiceLevelKind.Sla;

    public ServiceLevelTarget Target { get; set; } = ServiceLevelTarget.TimeToResolve;

    /// <summary>Nombre d'unités : le « 4 » de « 4 heures ».</summary>
    public int DurationValue { get; set; } = 4;

    public ServiceLevelUnit DurationUnit { get; set; } = ServiceLevelUnit.Hour;

    /// <summary>
    /// Reporte l'échéance à la fin de la journée ouvrée qui la contient
    /// (<c>end_of_working_day</c>). Utile pour un engagement « à la journée » : ce qui compte est
    /// que ce soit fait avant la fermeture, pas à 14 h 37 précises.
    /// </summary>
    public bool EndOfWorkingDay { get; set; }

    public string? Comment { get; set; }

    public List<ServiceLevelEscalation> Escalations { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Durée de l'engagement, telle que le calcul d'échéance la consomme.</summary>
    public TimeSpan Duration => DurationUnit switch
    {
        ServiceLevelUnit.Minute => TimeSpan.FromMinutes(DurationValue),
        ServiceLevelUnit.Hour => TimeSpan.FromHours(DurationValue),
        ServiceLevelUnit.Day => TimeSpan.FromDays(DurationValue),
        _ => TimeSpan.FromHours(DurationValue),
    };
}

/// <summary>Ce qu'un niveau d'escalade fait quand il se déclenche.</summary>
public enum EscalationActionType
{
    /// <summary>Force la priorité du ticket.</summary>
    SetPriority = 1,

    /// <summary>Force le statut du ticket.</summary>
    SetStatus = 2,

    /// <summary>Attribue le ticket à un technicien.</summary>
    AssignUser = 3,

    /// <summary>Attribue le ticket à un groupe.</summary>
    AssignGroup = 4,

    /// <summary>Consigne un suivi interne sur le ticket.</summary>
    AddFollowup = 5,

    /// <summary>Publie l'événement d'escalade, que les notifications et webhooks peuvent porter.</summary>
    Notify = 6,
}

/// <summary>
/// Niveau d'escalade (<c>glpi_slalevels</c> / <c>glpi_olalevels</c>) : ce qui se produit à un
/// moment donné par rapport à l'échéance, quand le ticket n'est toujours pas traité.
///
/// Contrairement à GLPI, les critères de déclenchement ne sont pas ceux du moteur de règles : un
/// niveau s'applique dès que le ticket est encore ouvert au moment dit. Une condition sur d'autres
/// champs demanderait le moteur de règles, qui n'existe pas encore pour l'assistance.
/// </summary>
public class ServiceLevelEscalation
{
    public int Id { get; set; }

    public int ServiceLevelAgreementId { get; set; }
    public ServiceLevelAgreement? Agreement { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// Décalage par rapport à l'échéance, en minutes de temps ouvré : négatif avant (« 60 minutes
    /// avant l'échéance »), positif après (« 120 minutes après »), zéro à l'échéance même.
    /// </summary>
    public int OffsetMinutes { get; set; }

    public bool IsActive { get; set; } = true;

    public List<ServiceLevelEscalationAction> Actions { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>« 1 h avant l'échéance », « à l'échéance », « 2 h après » — pour l'affichage.</summary>
    public string OffsetLabel => OffsetMinutes switch
    {
        0 => "à l'échéance",
        < 0 => $"{Humanise(-OffsetMinutes)} avant l'échéance",
        _ => $"{Humanise(OffsetMinutes)} après l'échéance",
    };

    private static string Humanise(int minutes) => minutes switch
    {
        < 60 => $"{minutes} min",
        _ when minutes % 60 == 0 => $"{minutes / 60} h",
        _ => $"{minutes / 60} h {minutes % 60:00}",
    };
}

/// <summary>Une action d'un niveau d'escalade. Plusieurs actions par niveau sont permises.</summary>
public class ServiceLevelEscalationAction
{
    public int Id { get; set; }

    public int ServiceLevelEscalationId { get; set; }
    public ServiceLevelEscalation? Escalation { get; set; }

    public EscalationActionType ActionType { get; set; }

    /// <summary>
    /// Valeur de l'action, interprétée selon <see cref="ActionType"/> : un rang de priorité ou de
    /// statut, un identifiant d'utilisateur ou de groupe, un texte de suivi. Stockée en texte
    /// plutôt qu'en colonnes typées, faute de quoi il faudrait une colonne par type d'action et
    /// une migration à chaque type ajouté.
    /// </summary>
    public string? Value { get; set; }
}

/// <summary>
/// Trace d'un niveau d'escalade déjà appliqué à un ticket (équivalent de
/// <c>glpi_slalevels_tickets</c>) : sans elle, la tâche cron rejouerait le même niveau à chacun de
/// ses passages, ajoutant un suivi et une notification toutes les cinq minutes.
/// </summary>
public class TicketEscalation
{
    public int Id { get; set; }

    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public int ServiceLevelEscalationId { get; set; }
    public ServiceLevelEscalation? Escalation { get; set; }

    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Libellés des énumérations de niveau de service — voir <see cref="TicketLabels"/> pour le principe.</summary>
public static class ServiceLevelLabels
{
    public static string For(ServiceLevelKind kind) => kind switch
    {
        ServiceLevelKind.Sla => "SLA (envers le demandeur)",
        ServiceLevelKind.Ola => "OLA (interne)",
        _ => kind.ToString(),
    };

    public static string ShortFor(ServiceLevelKind kind) => kind switch
    {
        ServiceLevelKind.Sla => "SLA",
        ServiceLevelKind.Ola => "OLA",
        _ => kind.ToString(),
    };

    public static string For(ServiceLevelTarget target) => target switch
    {
        ServiceLevelTarget.TimeToOwn => "Prise en charge",
        ServiceLevelTarget.TimeToResolve => "Résolution",
        _ => target.ToString(),
    };

    public static string For(ServiceLevelUnit unit) => unit switch
    {
        ServiceLevelUnit.Minute => "minute(s)",
        ServiceLevelUnit.Hour => "heure(s)",
        ServiceLevelUnit.Day => "jour(s)",
        _ => unit.ToString(),
    };

    public static string For(EscalationActionType action) => action switch
    {
        EscalationActionType.SetPriority => "Changer la priorité",
        EscalationActionType.SetStatus => "Changer le statut",
        EscalationActionType.AssignUser => "Attribuer à un technicien",
        EscalationActionType.AssignGroup => "Attribuer à un groupe",
        EscalationActionType.AddFollowup => "Ajouter un suivi interne",
        EscalationActionType.Notify => "Notifier",
        _ => action.ToString(),
    };

    /// <summary>« 4 heure(s) — Résolution », tel qu'une liste l'affiche.</summary>
    public static string Summarise(ServiceLevelAgreement agreement) =>
        $"{agreement.DurationValue} {For(agreement.DurationUnit)} — {For(agreement.Target)}";
}
