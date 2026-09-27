using GlpiNg.Modules.Assistance.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using GlpiNg.Modules.Abstractions.Localization;

namespace GlpiNg.Modules.Assistance.Components.Pages.Calendars;

public partial class Index : ComponentBase
{
    [Inject]
    private IDbContextFactory<DbContext> DbFactory { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    private List<Calendar> _calendars = [];
    private List<Calendar> _filtered = [];
    private string _searchTerm = string.Empty;
    private string? _error;

    private Calendar _newCalendar = NewBlank();
    private bool _seedStandardWeek = true;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _calendars = await db.Set<Calendar>()
            .AsNoTracking()
            .Include(calendar => calendar.Segments)
            .Include(calendar => calendar.Holidays)
            .AsSplitQuery()
            .OrderBy(calendar => calendar.Name)
            .ToListAsync();

        ApplyFilter();
    }

    /// <summary>Total hebdomadaire des plages, ce qui dit d'un coup d'œil si le calendrier tient debout.</summary>
    private static string WeeklyHours(Calendar calendar)
    {
        double hours = calendar.Segments.Sum(segment => segment.Duration.TotalHours);

        return hours == 0 ? "—" : $"{hours:0.#} h";
    }

    private void OnSearchChanged(KeyboardEventArgs args) => ApplyFilter();

    private void ApplyFilter()
    {
        string term = _searchTerm.Trim();

        _filtered = term.Length == 0
            ? _calendars
            : [.. _calendars.Where(calendar =>
                calendar.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (calendar.Comment?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))];
    }

    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(_newCalendar.Name))
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _newCalendar.Name = _newCalendar.Name.Trim();

        if (_seedStandardWeek)
        {
            DayOfWeek[] weekdays =
            [
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday,
            ];

            _newCalendar.Segments =
            [
                .. weekdays.Select(day => new CalendarSegment
                {
                    DayOfWeek = day,
                    StartTime = new TimeOnly(9, 0),
                    EndTime = new TimeOnly(18, 0),
                })
            ];
        }

        db.Set<Calendar>().Add(_newCalendar);
        await db.SaveChangesAsync();

        _newCalendar = NewBlank();
        _seedStandardWeek = true;

        await JS.InvokeVoidAsync("glping.hideModal", "newCalendarModal");
        await LoadAsync();
    }

    /// <summary>
    /// Un calendrier encore porté par un niveau de service ne se supprime pas : les échéances déjà
    /// calculées resteraient, mais les suivantes se compteraient soudain en temps réel, sans que
    /// rien ne le signale.
    /// </summary>
    private async Task DeleteAsync(int calendarId)
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        List<string> used = await db.Set<ServiceLevel>()
            .AsNoTracking()
            .Where(level => level.CalendarId == calendarId)
            .Select(level => level.Name)
            .ToListAsync();

        if (used.Count > 0)
        {
            _error = Tr.T("Calendrier non supprimé : il est utilisé par le(s) niveau(x) de service ")
                + $"{string.Join(", ", used)}.";

            return;
        }

        Calendar? stored = await db.Set<Calendar>().FirstOrDefaultAsync(calendar => calendar.Id == calendarId);

        if (stored is null)
        {
            return;
        }

        _error = null;

        // Plages et fermetures partent en cascade avec leur calendrier.
        db.Set<Calendar>().Remove(stored);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private static Calendar NewBlank() => new() { Name = string.Empty };
}
