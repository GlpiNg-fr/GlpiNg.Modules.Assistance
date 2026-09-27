using GlpiNg.Modules.Assistance.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using GlpiNg.Modules.Abstractions.Localization;

namespace GlpiNg.Modules.Assistance.Components.Pages.ServiceLevels;

public partial class Index : ComponentBase
{
    [Inject]
    private IDbContextFactory<DbContext> DbFactory { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    private List<ServiceLevel> _levels = [];
    private List<ServiceLevel> _filtered = [];
    private List<Calendar> _calendars = [];

    /// <summary>Nombre de tickets engagés par niveau : ce qu'on casserait en le supprimant.</summary>
    private Dictionary<int, int> _ticketCounts = [];

    private string _searchTerm = string.Empty;
    private string? _error;

    private ServiceLevel _newLevel = NewBlank();
    private int _newCalendarId;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _levels = await db.Set<ServiceLevel>()
            .AsNoTracking()
            .Include(level => level.Calendar)
            .Include(level => level.Agreements)
            .AsSplitQuery()
            .OrderBy(level => level.Name)
            .ToListAsync();

        _calendars = await db.Set<Calendar>()
            .AsNoTracking()
            .OrderBy(calendar => calendar.Name)
            .ToListAsync();

        // Un ticket est « engagé » par un niveau dès qu'il porte l'un de ses engagements, quel
        // qu'il soit. Les quatre colonnes sont comptées séparément puis réunies : un SelectMany
        // sur un tableau de navigations n'est pas traduisible en SQL, et EF refuse la requête.
        List<AgreementCount> counts = await TicketCountsByAgreementAsync(db);

        Dictionary<int, int> levelOf = _levels
            .SelectMany(level => level.Agreements)
            .ToDictionary(agreement => agreement.Id, agreement => agreement.ServiceLevelId);

        _ticketCounts = [];

        foreach (AgreementCount entry in counts)
        {
            if (levelOf.TryGetValue(entry.AgreementId, out int levelId))
            {
                _ticketCounts[levelId] = _ticketCounts.GetValueOrDefault(levelId) + entry.Count;
            }
        }

        ApplyFilter();
    }

    /// <summary>Nombre de tickets portant un engagement donné, tel que la base le compte.</summary>
    internal sealed record AgreementCount(int AgreementId, int Count);

    /// <summary>
    /// Compte les tickets par engagement. Les quatre colonnes (SLA/OLA × prise en charge/résolution)
    /// sont comptées par quatre requêtes groupées distinctes, puis réunies ici : un même ticket peut
    /// compter dans plusieurs d'entre elles, et ni un SelectMany sur les quatre colonnes ni un
    /// Concat de projections ne sont traduisibles en SQL — les deux ont été essayés.
    ///
    /// Quatre agrégats renvoient au plus quatre lignes par engagement, quel que soit le nombre de
    /// tickets : ce sont les tickets qui restent en base, pas le comptage qui remonte en mémoire.
    /// </summary>
    internal static async Task<List<AgreementCount>> TicketCountsByAgreementAsync(DbContext db)
    {
        IQueryable<Ticket> tickets = db.Set<Ticket>().AsNoTracking();

        List<AgreementCount> counts = [];

        async Task AccumulateAsync(IQueryable<int> engaged)
        {
            List<AgreementCount> rows = await engaged
                .GroupBy(id => id)
                .Select(group => new { AgreementId = group.Key, Count = group.Count() })
                .Select(row => new AgreementCount(row.AgreementId, row.Count))
                .ToListAsync();

            counts.AddRange(rows);
        }

        await AccumulateAsync(tickets.Where(t => t.SlaTimeToOwnId != null).Select(t => t.SlaTimeToOwnId!.Value));
        await AccumulateAsync(tickets.Where(t => t.SlaTimeToResolveId != null).Select(t => t.SlaTimeToResolveId!.Value));
        await AccumulateAsync(tickets.Where(t => t.OlaTimeToOwnId != null).Select(t => t.OlaTimeToOwnId!.Value));
        await AccumulateAsync(tickets.Where(t => t.OlaTimeToResolveId != null).Select(t => t.OlaTimeToResolveId!.Value));

        return counts;
    }

    private static int CountOf(ServiceLevel level, ServiceLevelKind kind) =>
        level.Agreements.Count(agreement => agreement.Kind == kind);

    private void OnSearchChanged(KeyboardEventArgs args) => ApplyFilter();

    private void ApplyFilter()
    {
        string term = _searchTerm.Trim();

        _filtered = term.Length == 0
            ? _levels
            : [.. _levels.Where(level =>
                level.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (level.Comment?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))];
    }

    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(_newLevel.Name))
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _newLevel.Name = _newLevel.Name.Trim();
        _newLevel.CalendarId = _newCalendarId == 0 ? null : _newCalendarId;

        db.Set<ServiceLevel>().Add(_newLevel);
        await db.SaveChangesAsync();

        _newLevel = NewBlank();
        _newCalendarId = 0;

        await JS.InvokeVoidAsync("glping.hideModal", "newServiceLevelModal");
        await LoadAsync();
    }

    /// <summary>
    /// Un niveau encore porté par des tickets ne se supprime pas. Les échéances déjà calculées
    /// resteraient affichées, mais plus rien ne dirait d'où elles viennent, et l'escalade cesserait
    /// sans un mot.
    /// </summary>
    private async Task DeleteAsync(int levelId)
    {
        if (_ticketCounts.GetValueOrDefault(levelId) > 0)
        {
            _error = Tr.T("Niveau de service non supprimé : des tickets portent encore ses engagements. ")
                + "Retirez-les de ces tickets d'abord.";

            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        ServiceLevel? stored = await db.Set<ServiceLevel>().FirstOrDefaultAsync(level => level.Id == levelId);

        if (stored is null)
        {
            return;
        }

        _error = null;

        // Engagements, niveaux d'escalade et leurs actions partent en cascade.
        db.Set<ServiceLevel>().Remove(stored);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private static ServiceLevel NewBlank() => new() { Name = string.Empty };
}
