using GlpiNg.Modules.Abstractions.Cron;
using GlpiNg.Modules.Abstractions.Items;
using GlpiNg.Modules.Abstractions.Notifications;
using GlpiNg.Modules.Assistance.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GlpiNg.Modules.Assistance.Services;

/// <summary>
/// Applique les niveaux d'escalade des engagements de service aux tickets qui les portent
/// (équivalent de la tâche <c>slaticket</c> de GLPI).
///
/// Un niveau ne s'exécute qu'une fois par ticket : la trace est prise dans
/// <see cref="TicketEscalation"/>, sans quoi chaque passage de la tâche rejouerait le même niveau
/// — un suivi et une notification toutes les cinq minutes.
///
/// Un engagement déjà tenu n'escalade pas : inutile de réveiller quelqu'un pour un délai de prise
/// en charge sur un ticket déjà pris en charge, ou un délai de résolution sur un ticket résolu.
/// </summary>
public sealed class ServiceLevelEscalationCronTask(
    IDbContextFactory<DbContext> dbFactory,
    INotificationPublisher notifications,
    ILogger<ServiceLevelEscalationCronTask> logger) : ICronTask
{
    /// <summary>Auteur porté par les suivis que l'escalade ajoute : ce n'est personne.</summary>
    private const string EscalationUser = "Escalade";

    public string Key => "sla_escalation";

    public string Name => "Escalade des niveaux de service";

    public string Description =>
        "Applique aux tickets les niveaux d'escalade de leurs engagements de service (SLA et OLA) "
        + "dont le moment de déclenchement est passé : changement de priorité ou de statut, "
        + "attribution, suivi interne, notification. Chaque niveau n'est appliqué qu'une fois par "
        + "ticket, et un engagement déjà tenu n'escalade pas.";

    /// <summary>
    /// Toutes les cinq minutes. Une escalade qui arrive une heure après son heure ne sert à rien ;
    /// la tâche ne coûte qu'une requête filtrée quand aucun ticket ne porte d'engagement.
    /// </summary>
    public int DefaultFrequencyMinutes => 5;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using DbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Seuls les tickets ouverts portant au moins un engagement sont candidats. Le filtre est
        // posé en base : sur un parc fourni, l'écrasante majorité des tickets n'a rien à escalader.
        List<Ticket> tickets = await db.Set<Ticket>()
            .Where(ticket => ticket.Status != TicketStatus.Solved && ticket.Status != TicketStatus.Closed)
            .Where(ticket =>
                ticket.SlaTimeToOwnId != null || ticket.SlaTimeToResolveId != null
                || ticket.OlaTimeToOwnId != null || ticket.OlaTimeToResolveId != null)
            .ToListAsync(cancellationToken);

        if (tickets.Count == 0)
        {
            return;
        }

        List<int> agreementIds =
        [
            .. tickets
                .SelectMany(ticket => new[]
                {
                    ticket.SlaTimeToOwnId, ticket.SlaTimeToResolveId,
                    ticket.OlaTimeToOwnId, ticket.OlaTimeToResolveId,
                })
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .Distinct()
        ];

        List<ServiceLevelAgreement> agreements = await db.Set<ServiceLevelAgreement>()
            .AsNoTracking()
            .Where(agreement => agreementIds.Contains(agreement.Id))
            .Include(agreement => agreement.Escalations.Where(escalation => escalation.IsActive))
                .ThenInclude(escalation => escalation.Actions)
            .Include(agreement => agreement.ServiceLevel!)
                .ThenInclude(level => level.Calendar!)
                    .ThenInclude(calendar => calendar.Segments)
            .Include(agreement => agreement.ServiceLevel!)
                .ThenInclude(level => level.Calendar!)
                    .ThenInclude(calendar => calendar.Holidays)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        List<int> ticketIds = [.. tickets.Select(ticket => ticket.Id)];

        // Ce qui a déjà été appliqué, chargé en une fois : une requête par ticket et par niveau
        // ferait des centaines d'aller-retours pour, le plus souvent, ne rien trouver.
        HashSet<(int TicketId, int EscalationId)> alreadyDone =
        [
            .. (await db.Set<TicketEscalation>()
                .AsNoTracking()
                .Where(entry => ticketIds.Contains(entry.TicketId))
                .Select(entry => new { entry.TicketId, entry.ServiceLevelEscalationId })
                .ToListAsync(cancellationToken))
                .Select(entry => (entry.TicketId, entry.ServiceLevelEscalationId))
        ];

        DateTime now = DateTime.UtcNow;
        int applied = 0;
        List<(int TicketId, string Escalation)> notifiable = [];

        foreach (Ticket ticket in tickets)
        {
            foreach ((int? agreementId, DateTime? deadline, bool satisfied) in Obligations(ticket))
            {
                if (agreementId is null || deadline is null || satisfied)
                {
                    continue;
                }

                ServiceLevelAgreement? agreement = agreements.FirstOrDefault(entry => entry.Id == agreementId);

                if (agreement is null)
                {
                    continue;
                }

                foreach (ServiceLevelEscalation escalation in agreement.Escalations)
                {
                    if (alreadyDone.Contains((ticket.Id, escalation.Id)))
                    {
                        continue;
                    }

                    DateTime? triggersAt = TriggerTime(deadline.Value, escalation, agreement);

                    if (triggersAt is null || triggersAt > now)
                    {
                        continue;
                    }

                    Apply(db, ticket, escalation, notifiable);

                    db.Set<TicketEscalation>().Add(new TicketEscalation
                    {
                        TicketId = ticket.Id,
                        ServiceLevelEscalationId = escalation.Id,
                        ExecutedAt = now,
                    });

                    alreadyDone.Add((ticket.Id, escalation.Id));
                    applied++;
                }
            }
        }

        if (applied == 0)
        {
            return;
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Escalade des niveaux de service : {Count} niveau(x) appliqué(s).", applied);

        // Les notifications partent après la sauvegarde : une escalade annoncée puis perdue par un
        // échec d'écriture serait pire que pas d'annonce du tout.
        foreach ((int ticketId, string escalationName) in notifiable)
        {
            await PublishAsync(ticketId, escalationName, cancellationToken);
        }
    }

    /// <summary>
    /// Les quatre obligations d'un ticket : l'engagement, son échéance, et si elle est déjà tenue.
    /// Une prise en charge faite ou une résolution rendue ferme le sujet, même en retard — la
    /// faute est constatée ailleurs, elle n'a plus à être escaladée.
    /// </summary>
    private static IEnumerable<(int? AgreementId, DateTime? Deadline, bool Satisfied)> Obligations(Ticket ticket)
    {
        bool taken = ticket.TakenIntoAccountAt is not null;
        bool solved = ticket.SolvedAt is not null;

        yield return (ticket.SlaTimeToOwnId, ticket.TimeToOwn, taken);
        yield return (ticket.SlaTimeToResolveId, ticket.TimeToResolve, solved);
        yield return (ticket.OlaTimeToOwnId, ticket.InternalTimeToOwn, taken);
        yield return (ticket.OlaTimeToResolveId, ticket.InternalTimeToResolve, solved);
    }

    /// <summary>
    /// Moment de déclenchement d'un niveau, compté en temps ouvré depuis l'échéance : un « 2 h
    /// avant » sur une échéance du lundi matin tombe le vendredi après-midi, quand il reste
    /// quelqu'un pour agir.
    /// </summary>
    private static DateTime? TriggerTime(DateTime deadline, ServiceLevelEscalation escalation, ServiceLevelAgreement agreement)
    {
        if (escalation.OffsetMinutes == 0)
        {
            return deadline;
        }

        Calendar? calendar = agreement.ServiceLevel?.Calendar;
        DateTime local = ServiceLevelService.ToLocal(deadline);
        TimeSpan offset = TimeSpan.FromMinutes(Math.Abs(escalation.OffsetMinutes));

        DateTime? moment = escalation.OffsetMinutes < 0
            ? WorkingTimeCalculator.Subtract(local, offset, calendar)
            : WorkingTimeCalculator.Add(local, offset, calendar);

        return moment is null ? null : ServiceLevelService.ToUtc(moment.Value);
    }

    private static void Apply(
        DbContext db,
        Ticket ticket,
        ServiceLevelEscalation escalation,
        List<(int TicketId, string Escalation)> notifiable)
    {
        foreach (ServiceLevelEscalationAction action in escalation.Actions)
        {
            switch (action.ActionType)
            {
                case EscalationActionType.SetPriority when TryLevel(action.Value, out ItilLevel priority):
                    ticket.Priority = priority;

                    // Une priorité posée par l'escalade est une décision, pas un calcul : la
                    // laisser suivre la matrice la ferait écraser au prochain enregistrement.
                    ticket.IsPriorityManual = true;
                    break;

                case EscalationActionType.SetStatus when TryStatus(action.Value, out TicketStatus status):
                    ticket.Status = status;
                    break;

                case EscalationActionType.AssignUser when TryId(action.Value, out int userId):
                    ticket.AssignedUserId = userId;
                    break;

                case EscalationActionType.AssignGroup when TryId(action.Value, out int groupId):
                    ticket.AssignedGroupId = groupId;
                    break;

                case EscalationActionType.AddFollowup when !string.IsNullOrWhiteSpace(action.Value):
                    db.Set<ItilFollowup>().Add(new ItilFollowup
                    {
                        ItemType = ItemTypes.Ticket,
                        ItemId = ticket.Id,
                        Content = action.Value,
                        AuthorName = EscalationUser,
                        IsPrivate = true,
                    });
                    break;

                case EscalationActionType.Notify:
                    notifiable.Add((ticket.Id, escalation.Name));
                    break;
            }
        }

        ticket.UpdatedAt = DateTime.UtcNow;

        db.Set<AssistanceHistoryEntry>().Add(new AssistanceHistoryEntry
        {
            ItemType = ItemTypes.Ticket,
            ItemId = ticket.Id,
            User = EscalationUser,
            Field = "Niveau de service",
            Description = $"Escalade « {escalation.Name} » appliquée ({escalation.OffsetLabel})",
        });
    }

    private async Task PublishAsync(int ticketId, string escalationName, CancellationToken cancellationToken)
    {
        Dictionary<string, string?> variables = new(StringComparer.Ordinal)
        {
            ["ticket.id"] = ticketId.ToString(),
            ["escalation.name"] = escalationName,
            ["ticket.url"] = $"/assistance/tickets/{ticketId}",
        };

        try
        {
            await notifications.PublishAsync(ItemTypes.Ticket, "escalation", ticketId, variables, cancellationToken);
        }
        catch (Exception exception)
        {
            // Une notification qui échoue ne doit pas faire échouer l'escalade : les actions sont
            // déjà écrites, et la tâche ne les rejouera pas.
            logger.LogWarning(exception,
                "Notification d'escalade non publiée pour le ticket {TicketId}.", ticketId);
        }
    }

    /// <summary>
    /// Une valeur d'action est du texte libre en base : une valeur illisible ou hors énumération
    /// fait passer l'action, plutôt que de faire échouer toute l'escalade.
    /// </summary>
    private static bool TryLevel(string? value, out ItilLevel level)
    {
        level = default;

        if (!int.TryParse(value, out int rank) || !Enum.IsDefined(typeof(ItilLevel), rank))
        {
            return false;
        }

        level = (ItilLevel)rank;
        return true;
    }

    /// <inheritdoc cref="TryLevel"/>
    private static bool TryStatus(string? value, out TicketStatus status)
    {
        status = default;

        if (!int.TryParse(value, out int rank) || !Enum.IsDefined(typeof(TicketStatus), rank))
        {
            return false;
        }

        status = (TicketStatus)rank;
        return true;
    }

    private static bool TryId(string? value, out int id) => int.TryParse(value, out id) && id > 0;
}
