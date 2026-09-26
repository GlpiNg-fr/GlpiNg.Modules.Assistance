using GlpiNg.Modules.Assistance.Models;
using Microsoft.EntityFrameworkCore;

namespace GlpiNg.Modules.Assistance.Services;

/// <summary>
/// Pose les échéances d'un ticket à partir des engagements qu'il porte.
///
/// Deux départs différents, et c'est tout l'intérêt d'avoir les deux : le SLA court depuis
/// l'ouverture du ticket (ce que le demandeur constate), l'OLA depuis l'attribution (ce que
/// l'équipe se doit). Un ticket ouvert vendredi soir et attribué lundi matin a donc un SLA déjà
/// bien entamé et un OLA qui démarre à peine.
///
/// Les échéances sont calculées une fois et stockées. Les recalculer à l'affichage les ferait
/// bouger au moindre changement de calendrier, et une échéance qui se déplace n'engage plus rien.
/// </summary>
public sealed class ServiceLevelService(IDbContextFactory<DbContext> dbFactory)
{
    /// <summary>
    /// Recalcule les quatre échéances du ticket. À appeler après toute modification d'un
    /// engagement, de l'attribution ou de l'ouverture — la fiche et la création le font déjà.
    ///
    /// L'appelant reste maître de la sauvegarde : rien n'est écrit ici, seul l'objet est modifié.
    /// </summary>
    public async Task ApplyAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        int?[] ids =
        [
            ticket.SlaTimeToOwnId, ticket.SlaTimeToResolveId,
            ticket.OlaTimeToOwnId, ticket.OlaTimeToResolveId,
        ];

        List<int> needed = [.. ids.Where(id => id is not null).Select(id => id!.Value).Distinct()];

        if (needed.Count == 0)
        {
            ticket.TimeToOwn = null;
            ticket.TimeToResolve = null;
            ticket.InternalTimeToOwn = null;
            ticket.InternalTimeToResolve = null;
            ticket.OlaStartedAt = null;

            return;
        }

        await using DbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Le calendrier vient du niveau de service qui porte l'engagement, avec ses plages et ses
        // fermetures : sans elles, le calcul retomberait en temps réel sans le dire.
        List<ServiceLevelAgreement> agreements = await db.Set<ServiceLevelAgreement>()
            .AsNoTracking()
            .Where(agreement => needed.Contains(agreement.Id))
            .Include(agreement => agreement.ServiceLevel!)
                .ThenInclude(level => level.Calendar!)
                    .ThenInclude(calendar => calendar.Segments)
            .Include(agreement => agreement.ServiceLevel!)
                .ThenInclude(level => level.Calendar!)
                    .ThenInclude(calendar => calendar.Holidays)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        ServiceLevelAgreement? Find(int? id) =>
            id is { } value ? agreements.FirstOrDefault(agreement => agreement.Id == value) : null;

        // L'OLA démarre à l'attribution. Tant que personne n'est dessus, elle n'a pas commencé et
        // ses échéances n'existent pas — plutôt qu'une échéance courant depuis l'ouverture, qui
        // ferait porter à l'équipe un retard antérieur à sa saisine.
        ticket.OlaStartedAt = ticket.AssignedUserId is not null || ticket.AssignedGroupId is not null
            ? ticket.OlaStartedAt ?? DateTime.UtcNow
            : null;

        ticket.TimeToOwn = Deadline(Find(ticket.SlaTimeToOwnId), ticket.OpenedAt);
        ticket.TimeToResolve = Deadline(Find(ticket.SlaTimeToResolveId), ticket.OpenedAt);
        ticket.InternalTimeToOwn = Deadline(Find(ticket.OlaTimeToOwnId), ticket.OlaStartedAt);
        ticket.InternalTimeToResolve = Deadline(Find(ticket.OlaTimeToResolveId), ticket.OlaStartedAt);
    }

    /// <summary>
    /// Échéance d'un engagement à partir d'un instant de départ, ou <c>null</c> si l'engagement ne
    /// s'applique pas (pas d'engagement, pas de départ, ou calendrier qui n'ouvre jamais assez).
    /// </summary>
    public static DateTime? Deadline(ServiceLevelAgreement? agreement, DateTime? start)
    {
        if (agreement is null || start is null)
        {
            return null;
        }

        Calendar? calendar = agreement.ServiceLevel?.Calendar;

        DateTime? localDeadline = WorkingTimeCalculator.Add(ToLocal(start.Value), agreement.Duration, calendar);

        if (localDeadline is null)
        {
            return null;
        }

        if (agreement.EndOfWorkingDay)
        {
            localDeadline = EndOfDay(localDeadline.Value, calendar);
        }

        return ToUtc(localDeadline.Value);
    }

    /// <summary>
    /// Passe en heure locale une date lue en base. Le calendrier raisonne en heure locale (« du
    /// lundi au vendredi, 9 h - 18 h »), les dates sont stockées en UTC : sans conversion, le
    /// calcul se ferait sur une heure de mur fausse du décalage horaire.
    ///
    /// <c>SpecifyKind</c> est indispensable : SQL Server rend ses <c>datetime2</c> avec un
    /// <see cref="DateTimeKind.Unspecified"/>, que <c>ToLocalTime</c> prend alors pour de l'heure
    /// locale et laisse tel quel. Tester le <c>Kind</c> ne servait donc à rien — c'est ce qui
    /// faisait se déclencher une escalade « 1 h avant » trois heures trop tôt, en plein été.
    /// </summary>
    internal static DateTime ToLocal(DateTime stored) =>
        DateTime.SpecifyKind(stored, DateTimeKind.Utc).ToLocalTime();

    /// <summary>
    /// Retour en UTC d'une date produite par le calcul. Celui-ci travaille en heure de mur locale
    /// et rend des dates sans <c>Kind</c> (elles sortent d'un <see cref="DateOnly"/>), d'où le
    /// <c>SpecifyKind</c> symétrique du précédent.
    /// </summary>
    internal static DateTime ToUtc(DateTime local) =>
        DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

    /// <summary>
    /// Reporte l'échéance à la fermeture du jour qu'elle touche. Sans calendrier, « fin de journée »
    /// ne peut être que minuit — faute de savoir quand le service ferme.
    /// </summary>
    private static DateTime EndOfDay(DateTime moment, Calendar? calendar)
    {
        DateOnly date = DateOnly.FromDateTime(moment);

        if (calendar is null || calendar.Segments.Count == 0)
        {
            return date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        }

        List<CalendarSegment> daySegments =
        [
            .. calendar.Segments.Where(segment =>
                segment.DayOfWeek == date.DayOfWeek && segment.Duration > TimeSpan.Zero)
        ];

        return daySegments.Count == 0
            ? moment
            : date.ToDateTime(daySegments.Max(segment => segment.EndTime));
    }
}
