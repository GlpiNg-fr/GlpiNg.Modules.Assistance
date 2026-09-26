using BlazorBootstrap;
using GlpiNg.Modules.Assistance.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

namespace GlpiNg.Modules.Assistance.Components.Pages.ServiceLevels;

public partial class Detail : ComponentBase
{
    [Parameter]
    public int ServiceLevelId { get; set; }

    [Inject]
    private IDbContextFactory<DbContext> DbFactory { get; set; } = null!;

    [Inject]
    private ToastService ToastService { get; set; } = null!;

    private ServiceLevel? _level;
    private List<Calendar> _calendars = [];

    /// <summary>Nombre de tickets portant chaque engagement : ce qu'on casserait en le supprimant.</summary>
    private Dictionary<int, int> _ticketCounts = [];

    private int _calendarId;
    private string _activeTab = "fiche";
    private bool _isSaving;
    private string? _error;

    private ServiceLevelAgreement _newAgreement = NewBlankAgreement();
    private string? _agreementError;

    private IEnumerable<(string Key, string Label, string Icon, int? Count)> Tabs
    {
        get
        {
            yield return ("fiche", "Fiche", "ti-clipboard-check", null);
            yield return ("engagements", "Engagements", "ti-clock-check", _level?.Agreements.Count);
        }
    }

    /// <summary>SLA d'abord, puis OLA ; à l'intérieur, prise en charge avant résolution.</summary>
    private IEnumerable<ServiceLevelAgreement> OrderedAgreements =>
        _level is null
            ? []
            : _level.Agreements
                .OrderBy(agreement => agreement.Kind)
                .ThenByDescending(agreement => agreement.Target)
                .ThenBy(agreement => agreement.Name);

    protected override async Task OnParametersSetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _level = await db.Set<ServiceLevel>()
            .AsNoTracking()
            .Include(level => level.Agreements)
                .ThenInclude(agreement => agreement.Escalations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(level => level.Id == ServiceLevelId);

        if (_level is null)
        {
            return;
        }

        _calendarId = _level.CalendarId ?? 0;

        _calendars = await db.Set<Calendar>()
            .AsNoTracking()
            .OrderBy(calendar => calendar.Name)
            .ToListAsync();

        HashSet<int> agreementIds = [.. _level.Agreements.Select(agreement => agreement.Id)];

        // Même comptage que sur la liste des niveaux, restreint aux engagements de celui-ci.
        _ticketCounts = [];

        foreach (Index.AgreementCount entry in await Index.TicketCountsByAgreementAsync(db))
        {
            if (agreementIds.Contains(entry.AgreementId))
            {
                _ticketCounts[entry.AgreementId] = _ticketCounts.GetValueOrDefault(entry.AgreementId) + entry.Count;
            }
        }
    }

    private async Task SaveAsync()
    {
        if (_level is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_level.Name))
        {
            _error = "Le nom est obligatoire.";
            _activeTab = "fiche";
            return;
        }

        _error = null;
        _isSaving = true;

        try
        {
            await using DbContext db = await DbFactory.CreateDbContextAsync();

            ServiceLevel? stored = await db.Set<ServiceLevel>()
                .FirstOrDefaultAsync(level => level.Id == ServiceLevelId);

            if (stored is null)
            {
                return;
            }

            stored.Name = _level.Name.Trim();
            stored.Comment = _level.Comment;
            stored.CalendarId = _calendarId == 0 ? null : _calendarId;
            stored.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            ToastService.Notify(new ToastMessage(ToastType.Success, "Niveau de service enregistré."));
            await LoadAsync();
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task AddAgreementAsync()
    {
        if (string.IsNullOrWhiteSpace(_newAgreement.Name))
        {
            return;
        }

        if (_newAgreement.DurationValue <= 0)
        {
            _agreementError = "La durée doit être d'au moins une unité.";
            return;
        }

        // Un ticket ne porte qu'un engagement par couple (envers, objet) : proposer d'en créer un
        // second laisserait croire que les deux s'appliquent, alors que la fiche n'en retient qu'un.
        bool duplicate = _level?.Agreements.Any(agreement =>
            agreement.Kind == _newAgreement.Kind && agreement.Target == _newAgreement.Target) ?? false;

        if (duplicate)
        {
            _agreementError = $"Ce niveau a déjà un {ServiceLevelLabels.ShortFor(_newAgreement.Kind)} sur "
                + $"« {ServiceLevelLabels.For(_newAgreement.Target).ToLowerInvariant()} ». "
                + "Modifiez-le plutôt que d'en ajouter un second.";

            return;
        }

        _agreementError = null;

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _newAgreement.Name = _newAgreement.Name.Trim();
        _newAgreement.ServiceLevelId = ServiceLevelId;

        db.Set<ServiceLevelAgreement>().Add(_newAgreement);
        await db.SaveChangesAsync();

        _newAgreement = NewBlankAgreement();

        await LoadAsync();
    }

    private async Task DeleteAgreementAsync(int agreementId)
    {
        if (_ticketCounts.GetValueOrDefault(agreementId) > 0)
        {
            _agreementError = "Engagement non supprimé : des tickets le portent encore. "
                + "Retirez-le de ces tickets d'abord.";

            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        ServiceLevelAgreement? stored = await db.Set<ServiceLevelAgreement>()
            .FirstOrDefaultAsync(agreement => agreement.Id == agreementId && agreement.ServiceLevelId == ServiceLevelId);

        if (stored is null)
        {
            return;
        }

        _agreementError = null;

        // Niveaux d'escalade, leurs actions et les traces d'exécution partent en cascade.
        db.Set<ServiceLevelAgreement>().Remove(stored);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private static ServiceLevelAgreement NewBlankAgreement() => new() { Name = string.Empty };
}
