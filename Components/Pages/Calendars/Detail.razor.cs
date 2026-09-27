using System.Globalization;
using BlazorBootstrap;
using GlpiNg.Modules.Assistance.Models;
using GlpiNg.Modules.Assistance.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using GlpiNg.Modules.Abstractions.Localization;

// System.Globalization porte aussi un type « Calendar » : sans cet alias, chaque mention du nôtre
// serait ambiguë dans ce fichier, qui a besoin des deux espaces de noms.
using Calendar = GlpiNg.Modules.Assistance.Models.Calendar;

namespace GlpiNg.Modules.Assistance.Components.Pages.Calendars;

public partial class Detail : ComponentBase
{
    [Parameter]
    public int CalendarId { get; set; }

    [Inject]
    private IDbContextFactory<DbContext> DbFactory { get; set; } = null!;

    [Inject]
    private ToastService ToastService { get; set; } = null!;

    /// <summary>La semaine commence le lundi ici, pas le dimanche comme <see cref="DayOfWeek"/>.</summary>
    private static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    // Noms de jours et de mois dans la langue de l'interface (voir Tr), pas dans celle du serveur.
    private static CultureInfo UiCulture => CultureInfo.CurrentUICulture;

    private Calendar? _calendar;
    private List<ServiceLevel> _usedBy = [];

    private string _activeTab = "fiche";
    private bool _isSaving;
    private string? _error;

    private int _newSegmentDay = (int)DayOfWeek.Monday;
    private TimeOnly _newSegmentStart = new(9, 0);
    private TimeOnly _newSegmentEnd = new(18, 0);
    private string? _segmentError;

    private string _newHolidayName = string.Empty;
    private DateTime? _newHolidayStart = DateTime.Today;
    private DateTime? _newHolidayEnd = DateTime.Today;
    private bool _newHolidayPerpetual;
    private string? _holidayError;

    private DateTime? _probeStart = DateTime.Now;
    private int _probeValue = 4;
    private ServiceLevelUnit _probeUnit = ServiceLevelUnit.Hour;
    private string? _probeResult;
    private bool _probeImpossible;

    private IEnumerable<(string Key, string Label, string Icon, int? Count)> Tabs
    {
        get
        {
            yield return ("fiche", "Fiche", "ti-calendar-time", null);
            yield return ("plages", "Plages horaires", "ti-clock", _calendar?.Segments.Count);
            yield return ("fermetures", "Fermetures", "ti-beach", _calendar?.Holidays.Count);
            yield return ("essai", "Essai", "ti-player-play", null);
        }
    }

    private IEnumerable<CalendarSegment> OrderedSegments =>
        _calendar is null
            ? []
            : _calendar.Segments
                .OrderBy(segment => Array.IndexOf(WeekOrder, segment.DayOfWeek))
                .ThenBy(segment => segment.StartTime);

    protected override async Task OnParametersSetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _calendar = await db.Set<Calendar>()
            .AsNoTracking()
            .Include(calendar => calendar.Segments)
            .Include(calendar => calendar.Holidays)
            .AsSplitQuery()
            .FirstOrDefaultAsync(calendar => calendar.Id == CalendarId);

        if (_calendar is null)
        {
            return;
        }

        _usedBy = await db.Set<ServiceLevel>()
            .AsNoTracking()
            .Where(level => level.CalendarId == CalendarId)
            .OrderBy(level => level.Name)
            .ToListAsync();
    }

    private static string DayLabel(DayOfWeek day) =>
        UiCulture.DateTimeFormat.GetDayName(day) is { Length: > 0 } name
            ? char.ToUpper(name[0], UiCulture) + name[1..]
            : day.ToString();

    private string WeeklyHours()
    {
        double hours = _calendar?.Segments.Sum(segment => segment.Duration.TotalHours) ?? 0;

        return hours == 0 ? "—" : $"{hours:0.#} h";
    }

    private async Task SaveAsync()
    {
        if (_calendar is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_calendar.Name))
        {
            _error = Tr.T("Le nom est obligatoire.");
            _activeTab = "fiche";
            return;
        }

        _error = null;
        _isSaving = true;

        try
        {
            await using DbContext db = await DbFactory.CreateDbContextAsync();

            Calendar? stored = await db.Set<Calendar>().FirstOrDefaultAsync(calendar => calendar.Id == CalendarId);
            if (stored is null)
            {
                return;
            }

            stored.Name = _calendar.Name.Trim();
            stored.Comment = _calendar.Comment;
            stored.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            ToastService.Notify(new ToastMessage(ToastType.Success, Tr.T("Calendrier enregistré.")));
            await LoadAsync();
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <summary>
    /// Les plages sont écrites tout de suite, sans attendre l'enregistrement de la fiche : ce sont
    /// des lignes, pas des champs, et les laisser en suspens ferait perdre la saisie au moindre
    /// changement d'onglet.
    /// </summary>
    private async Task AddSegmentAsync()
    {
        if (_newSegmentEnd <= _newSegmentStart)
        {
            _segmentError = Tr.T("La fin doit suivre le début.");
            return;
        }

        DayOfWeek day = (DayOfWeek)_newSegmentDay;

        // Deux plages qui se chevauchent compteraient deux fois les mêmes heures : une durée de
        // quatre heures ouvrées s'épuiserait en deux heures réelles.
        bool overlaps = _calendar?.Segments.Any(segment =>
            segment.DayOfWeek == day
            && _newSegmentStart < segment.EndTime
            && segment.StartTime < _newSegmentEnd) ?? false;

        if (overlaps)
        {
            _segmentError = Tr.T("Cette plage en chevauche une autre le {0}.", DayLabel(day).ToLower(UiCulture));
            return;
        }

        _segmentError = null;

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        db.Set<CalendarSegment>().Add(new CalendarSegment
        {
            CalendarId = CalendarId,
            DayOfWeek = day,
            StartTime = _newSegmentStart,
            EndTime = _newSegmentEnd,
        });

        await TouchAsync(db);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private async Task DeleteSegmentAsync(int segmentId)
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        CalendarSegment? segment = await db.Set<CalendarSegment>()
            .FirstOrDefaultAsync(entry => entry.Id == segmentId && entry.CalendarId == CalendarId);

        if (segment is null)
        {
            return;
        }

        db.Set<CalendarSegment>().Remove(segment);
        await TouchAsync(db);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private async Task AddHolidayAsync()
    {
        if (string.IsNullOrWhiteSpace(_newHolidayName) || _newHolidayStart is null)
        {
            return;
        }

        DateOnly start = DateOnly.FromDateTime(_newHolidayStart.Value);
        DateOnly end = _newHolidayEnd is { } value ? DateOnly.FromDateTime(value) : start;

        if (end < start)
        {
            _holidayError = Tr.T("La date de fin précède la date de début.");
            return;
        }

        _holidayError = null;

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        db.Set<CalendarHoliday>().Add(new CalendarHoliday
        {
            CalendarId = CalendarId,
            Name = _newHolidayName.Trim(),
            StartDate = start,
            EndDate = end,
            IsPerpetual = _newHolidayPerpetual,
        });

        await TouchAsync(db);
        await db.SaveChangesAsync();

        _newHolidayName = string.Empty;
        _newHolidayPerpetual = false;

        await LoadAsync();
    }

    private async Task DeleteHolidayAsync(int holidayId)
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        CalendarHoliday? holiday = await db.Set<CalendarHoliday>()
            .FirstOrDefaultAsync(entry => entry.Id == holidayId && entry.CalendarId == CalendarId);

        if (holiday is null)
        {
            return;
        }

        db.Set<CalendarHoliday>().Remove(holiday);
        await TouchAsync(db);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    /// <summary>
    /// Essai à blanc du calendrier. Le même calcul que celui des échéances de ticket, pour qu'un
    /// administrateur voie ce qu'il configure avant qu'un incident réel en dépende.
    /// </summary>
    private void Probe()
    {
        if (_calendar is null || _probeStart is null || _probeValue <= 0)
        {
            return;
        }

        TimeSpan duration = _probeUnit switch
        {
            ServiceLevelUnit.Minute => TimeSpan.FromMinutes(_probeValue),
            ServiceLevelUnit.Day => TimeSpan.FromDays(_probeValue),
            _ => TimeSpan.FromHours(_probeValue),
        };

        DateTime? deadline = WorkingTimeCalculator.Add(_probeStart.Value, duration, _calendar);

        if (deadline is null)
        {
            _probeImpossible = true;
            _probeResult = Tr.T("Ce calendrier n'ouvre jamais assez pour absorber cette durée : ")
                + "l'échéance n'arriverait pas. Vérifiez les plages et les fermetures.";

            return;
        }

        _probeImpossible = false;

        string elapsed = Tr.T("{0} à {1}", _probeStart.Value.ToString("dddd d MMMM yyyy", UiCulture), _probeStart.Value.ToString("HH:mm", UiCulture));
        string due = Tr.T("{0} à {1}", deadline.Value.ToString("dddd d MMMM yyyy", UiCulture), deadline.Value.ToString("HH:mm", UiCulture));

        _probeResult = Tr.T("Départ le {0} → échéance le {1} ", elapsed, due)
            + $"(soit {(deadline.Value - _probeStart.Value).TotalHours:0.#} h réelles).";
    }

    private async Task TouchAsync(DbContext db)
    {
        Calendar? stored = await db.Set<Calendar>().FirstOrDefaultAsync(calendar => calendar.Id == CalendarId);

        if (stored is not null)
        {
            stored.UpdatedAt = DateTime.UtcNow;
        }
    }
}
