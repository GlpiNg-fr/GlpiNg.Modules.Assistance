using BlazorBootstrap;
using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.Assistance.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

namespace GlpiNg.Modules.Assistance.Components.Pages.ServiceLevels;

public partial class AgreementDetail : ComponentBase
{
    [Parameter]
    public int AgreementId { get; set; }

    [Inject]
    private IDbContextFactory<DbContext> DbFactory { get; set; } = null!;

    [Inject]
    private IPrincipalDirectory Directory { get; set; } = null!;

    [Inject]
    private ToastService ToastService { get; set; } = null!;

    private ServiceLevelAgreement? _agreement;
    private string _levelName = string.Empty;
    private string? _calendarName;
    private int _ticketCount;

    private IReadOnlyList<PrincipalOption> _users = [];
    private IReadOnlyList<PrincipalOption> _groups = [];

    private string _activeTab = "fiche";
    private bool _isSaving;
    private string? _error;

    private string _newEscalationName = string.Empty;
    private int _newEscalationOffset = -60;
    private string? _escalationError;

    // Le formulaire d'action est répété sous chaque niveau : sa saisie est donc tenue par niveau,
    // sans quoi remplir l'un viderait l'autre.
    private readonly Dictionary<int, EscalationActionType> _actionTypes = [];
    private readonly Dictionary<int, string?> _actionValues = [];

    private IEnumerable<(string Key, string Label, string Icon, int? Count)> Tabs
    {
        get
        {
            yield return ("fiche", "Fiche", "ti-clock-check", null);
            yield return ("escalades", "Escalades", "ti-stairs-up", _agreement?.Escalations.Count);
        }
    }

    /// <summary>Du plus tôt au plus tard : c'est l'ordre dans lequel les niveaux se déclenchent.</summary>
    private IEnumerable<ServiceLevelEscalation> OrderedEscalations =>
        _agreement is null ? [] : _agreement.Escalations.OrderBy(escalation => escalation.OffsetMinutes);

    protected override async Task OnParametersSetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _agreement = await db.Set<ServiceLevelAgreement>()
            .AsNoTracking()
            .Include(agreement => agreement.ServiceLevel!)
                .ThenInclude(level => level.Calendar)
            .Include(agreement => agreement.Escalations)
                .ThenInclude(escalation => escalation.Actions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(agreement => agreement.Id == AgreementId);

        if (_agreement is null)
        {
            return;
        }

        _levelName = _agreement.ServiceLevel?.Name ?? string.Empty;
        _calendarName = _agreement.ServiceLevel?.Calendar?.Name;

        _ticketCount = await db.Set<Ticket>()
            .AsNoTracking()
            .CountAsync(ticket =>
                ticket.SlaTimeToOwnId == AgreementId || ticket.SlaTimeToResolveId == AgreementId
                || ticket.OlaTimeToOwnId == AgreementId || ticket.OlaTimeToResolveId == AgreementId);

        _users = await Directory.GetAsync(PrincipalKind.User);
        _groups = await Directory.GetAsync(PrincipalKind.Group);
    }

    private EscalationActionType ActionTypeFor(int escalationId) =>
        _actionTypes.GetValueOrDefault(escalationId, EscalationActionType.SetPriority);

    private string? ActionValueFor(int escalationId) => _actionValues.GetValueOrDefault(escalationId);

    private void SetActionType(int escalationId, string? raw)
    {
        if (int.TryParse(raw, out int value) && Enum.IsDefined(typeof(EscalationActionType), value))
        {
            _actionTypes[escalationId] = (EscalationActionType)value;

            // Changer de type rend la valeur précédente absurde (un identifiant d'utilisateur
            // pour un rang de priorité) : on repart de vide.
            _actionValues[escalationId] = null;
        }
    }

    private void SetActionValue(int escalationId, string? value) => _actionValues[escalationId] = value;

    /// <summary>Rend lisible la valeur d'une action, qui est stockée en texte brut.</summary>
    private string DescribeValue(ServiceLevelEscalationAction action) => action.ActionType switch
    {
        EscalationActionType.SetPriority when int.TryParse(action.Value, out int rank)
            && Enum.IsDefined(typeof(ItilLevel), rank) => ItilLabels.For((ItilLevel)rank),

        EscalationActionType.SetStatus when int.TryParse(action.Value, out int rank)
            && Enum.IsDefined(typeof(TicketStatus), rank) => TicketLabels.For((TicketStatus)rank),

        EscalationActionType.AssignUser when int.TryParse(action.Value, out int id) =>
            _users.FirstOrDefault(user => user.Id == id)?.Name ?? $"#{id} (supprimé)",

        EscalationActionType.AssignGroup when int.TryParse(action.Value, out int id) =>
            _groups.FirstOrDefault(group => group.Id == id)?.Name ?? $"#{id} (supprimé)",

        EscalationActionType.Notify => "Événement « Escalade d'un ticket »",

        _ => action.Value ?? "—",
    };

    private async Task SaveAsync()
    {
        if (_agreement is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_agreement.Name))
        {
            _error = "Le nom est obligatoire.";
            _activeTab = "fiche";
            return;
        }

        if (_agreement.DurationValue <= 0)
        {
            _error = "La durée doit être d'au moins une unité.";
            _activeTab = "fiche";
            return;
        }

        _error = null;
        _isSaving = true;

        try
        {
            await using DbContext db = await DbFactory.CreateDbContextAsync();

            ServiceLevelAgreement? stored = await db.Set<ServiceLevelAgreement>()
                .FirstOrDefaultAsync(agreement => agreement.Id == AgreementId);

            if (stored is null)
            {
                return;
            }

            stored.Name = _agreement.Name.Trim();
            stored.DurationValue = _agreement.DurationValue;
            stored.DurationUnit = _agreement.DurationUnit;
            stored.EndOfWorkingDay = _agreement.EndOfWorkingDay;
            stored.Comment = _agreement.Comment;
            stored.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            ToastService.Notify(new ToastMessage(ToastType.Success,
                _ticketCount > 0
                    ? $"Engagement enregistré. Les {_ticketCount} ticket(s) déjà engagés gardent leur échéance."
                    : "Engagement enregistré."));

            await LoadAsync();
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task AddEscalationAsync()
    {
        if (string.IsNullOrWhiteSpace(_newEscalationName))
        {
            return;
        }

        _escalationError = null;

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        db.Set<ServiceLevelEscalation>().Add(new ServiceLevelEscalation
        {
            ServiceLevelAgreementId = AgreementId,
            Name = _newEscalationName.Trim(),
            OffsetMinutes = _newEscalationOffset,
        });

        await db.SaveChangesAsync();

        _newEscalationName = string.Empty;

        await LoadAsync();
    }

    private async Task ToggleEscalationAsync(int escalationId, bool active)
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        ServiceLevelEscalation? stored = await db.Set<ServiceLevelEscalation>()
            .FirstOrDefaultAsync(escalation =>
                escalation.Id == escalationId && escalation.ServiceLevelAgreementId == AgreementId);

        if (stored is null)
        {
            return;
        }

        stored.IsActive = active;
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private async Task DeleteEscalationAsync(int escalationId)
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        ServiceLevelEscalation? stored = await db.Set<ServiceLevelEscalation>()
            .FirstOrDefaultAsync(escalation =>
                escalation.Id == escalationId && escalation.ServiceLevelAgreementId == AgreementId);

        if (stored is null)
        {
            return;
        }

        // Actions et traces d'exécution partent en cascade. Les traces perdues signifient qu'un
        // niveau recréé à l'identique se rejouerait : c'est le comportement attendu, on a
        // explicitement défait ce qui avait été fait.
        db.Set<ServiceLevelEscalation>().Remove(stored);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private async Task AddActionAsync(int escalationId)
    {
        EscalationActionType type = ActionTypeFor(escalationId);
        string? value = ActionValueFor(escalationId);

        // Toute action sauf « Notifier » a besoin d'une valeur : l'enregistrer vide donnerait un
        // niveau qui se déclenche et ne fait rien.
        if (type != EscalationActionType.Notify && string.IsNullOrWhiteSpace(value))
        {
            _escalationError = $"L'action « {ServiceLevelLabels.For(type)} » a besoin d'une valeur.";
            return;
        }

        _escalationError = null;

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        bool belongs = await db.Set<ServiceLevelEscalation>()
            .AnyAsync(escalation => escalation.Id == escalationId && escalation.ServiceLevelAgreementId == AgreementId);

        if (!belongs)
        {
            return;
        }

        db.Set<ServiceLevelEscalationAction>().Add(new ServiceLevelEscalationAction
        {
            ServiceLevelEscalationId = escalationId,
            ActionType = type,
            Value = type == EscalationActionType.Notify ? null : value?.Trim(),
        });

        await db.SaveChangesAsync();

        _actionValues[escalationId] = null;

        await LoadAsync();
    }

    private async Task DeleteActionAsync(int actionId)
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        ServiceLevelEscalationAction? stored = await db.Set<ServiceLevelEscalationAction>()
            .Include(action => action.Escalation)
            .FirstOrDefaultAsync(action => action.Id == actionId);

        if (stored?.Escalation is null || stored.Escalation.ServiceLevelAgreementId != AgreementId)
        {
            return;
        }

        db.Set<ServiceLevelEscalationAction>().Remove(stored);
        await db.SaveChangesAsync();

        await LoadAsync();
    }
}
