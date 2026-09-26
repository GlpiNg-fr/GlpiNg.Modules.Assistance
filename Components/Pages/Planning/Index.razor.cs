using System.Globalization;
using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.Abstractions.Items;
using GlpiNg.Modules.Assistance.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

namespace GlpiNg.Modules.Assistance.Components.Pages.Planning;

/// <summary>
/// Planning de l'assistance (<c>Planning</c> de GLPI) : les tâches planifiées des tickets, problèmes
/// et changements, en semaine, par technicien. C'est aussi là qu'on planifie — une tâche sans date
/// attend dans « À planifier », et un clic sur n'importe quelle tâche ouvre sa planification.
///
/// Les dates de tâche sont des heures <b>locales</b> : elles sont saisies dans un champ
/// « datetime-local » et stockées telles quelles, comme l'échéance d'un ticket (voir
/// <see cref="Ticket.IsOverdue"/>). Elles ne passent donc jamais par <c>ToLocalTime()</c>, qui les
/// prendrait pour de l'UTC et les décalerait du fuseau.
/// </summary>
public partial class Index : ComponentBase
{
    [Inject]
    private IDbContextFactory<DbContext> DbFactory { get; set; } = null!;

    [Inject]
    private IPrincipalDirectory Directory { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    [CascadingParameter]
    private Task<AuthenticationState>? AuthStateTask { get; set; }

    /// <summary>Plage affichée par défaut ; elle s'élargit d'elle-même pour une tâche hors de ces heures.</summary>
    private const int DefaultFirstHour = 8;
    private const int DefaultLastHour = 19;

    /// <summary>Hauteur d'une heure dans la grille, en pixels — la seule unité de la mise en page.</summary>
    private const int HourHeight = 48;

    /// <summary>Durée supposée d'une tâche planifiée sans fin : GLPI la dessine sur une heure aussi.</summary>
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Posée à l'initialisation, une fois le fuseau de l'utilisateur connu (voir <see cref="Today"/>).</summary>
    private DateTime _weekStart;

    /// <summary>
    /// L'heure qu'il est pour l'utilisateur, dans son fuseau (préférences) et non celui du serveur :
    /// les tâches sont des heures murales, la ligne « maintenant » doit parler le même langage.
    /// </summary>
    private DateTime Now => Display.ToUserTime(DateTime.UtcNow);

    private DateTime Today => Now.Date;

    /// <summary>« me », « all », ou l'identifiant d'un technicien.</summary>
    private string _technicianFilter = "me";

    private bool _showDone = true;

    private List<PlannedTask> _planned = [];
    private List<UnplannedTask> _unplanned = [];
    private IReadOnlyList<PrincipalOption> _users = [];
    private int _firstHour = DefaultFirstHour;
    private int _lastHour = DefaultLastHour;

    private int? _currentUserId;
    private string _currentUserName = "?";

    // Fenêtre de planification d'une tâche.
    private ItilTask? _editing;
    private string _editingParent = string.Empty;
    private DateTime? _editStart;
    private DateTime? _editEnd;
    private int _editUserId;
    private bool _editDone;
    private string? _editError;

    private IEnumerable<DateTime> Days => Enumerable.Range(0, 7).Select(offset => _weekStart.AddDays(offset));

    private string WeekLabel
    {
        get
        {
            DateTime end = _weekStart.AddDays(6);

            // « du 21 au 27 septembre », mais « du 28 septembre au 4 octobre » à cheval sur deux mois.
            string from = _weekStart.Month == end.Month
                ? _weekStart.Day.ToString(French)
                : _weekStart.ToString("d MMMM", French);

            return $"Semaine du {from} au {end.ToString("d MMMM yyyy", French)}";
        }
    }

    protected override async Task OnInitializedAsync()
    {
        _weekStart = StartOfWeek(Today);

        if (AuthStateTask is not null)
        {
            AuthenticationState authState = await AuthStateTask;
            _currentUserName = authState.User.Identity?.Name ?? "?";

            string? claim = authState.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            _currentUserId = int.TryParse(claim, out int userId) ? userId : null;
        }

        // Sans utilisateur identifié, « mon planning » serait vide sans raison apparente.
        if (_currentUserId is null)
        {
            _technicianFilter = "all";
        }

        _users = await Directory.GetAsync(PrincipalKind.User);
        await LoadAsync();
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        // Semaine du lundi au dimanche, comme le calendrier français (DayOfWeek met dimanche à 0).
        int offset = ((int)date.DayOfWeek + 6) % 7;
        return date.Date.AddDays(-offset);
    }

    private async Task PreviousWeekAsync()
    {
        _weekStart = _weekStart.AddDays(-7);
        await LoadAsync();
    }

    private async Task NextWeekAsync()
    {
        _weekStart = _weekStart.AddDays(7);
        await LoadAsync();
    }

    private async Task TodayAsync()
    {
        _weekStart = StartOfWeek(Today);
        await LoadAsync();
    }

    private async Task OnTechnicianChangedAsync(ChangeEventArgs args)
    {
        _technicianFilter = args.Value?.ToString() ?? "me";
        await LoadAsync();
    }

    private async Task OnShowDoneChangedAsync(bool value)
    {
        _showDone = value;
        await LoadAsync();
    }

    /// <summary>Le technicien filtré, ou null pour « tous » (et pour « moi » sans utilisateur connu).</summary>
    private int? FilteredUserId => _technicianFilter switch
    {
        "all" => null,
        "me" => _currentUserId,
        _ when int.TryParse(_technicianFilter, out int id) => id,
        _ => null,
    };

    private async Task LoadAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        DateTime weekEnd = _weekStart.AddDays(7);
        int? userId = FilteredUserId;

        // Une tâche commencée avant la semaine mais qui la chevauche reste à charger : on part de la
        // veille, la plus longue tâche raisonnable ne durant pas plusieurs jours.
        IQueryable<ItilTask> query = db.Set<ItilTask>()
            .AsNoTracking()
            .Where(task => task.PlannedStart != null
                && task.PlannedStart < weekEnd
                && (task.PlannedEnd ?? task.PlannedStart) >= _weekStart.AddDays(-1));

        if (userId is { } id)
        {
            query = query.Where(task => task.AssignedUserId == id);
        }

        if (!_showDone)
        {
            query = query.Where(task => task.State == ItilTaskState.ToDo);
        }

        List<ItilTask> tasks = await query.OrderBy(task => task.PlannedStart).ToListAsync();

        IQueryable<ItilTask> unplannedQuery = db.Set<ItilTask>()
            .AsNoTracking()
            .Where(task => task.PlannedStart == null && task.State == ItilTaskState.ToDo);

        if (userId is { } unplannedUserId)
        {
            unplannedQuery = unplannedQuery.Where(task => task.AssignedUserId == unplannedUserId);
        }

        List<ItilTask> unplanned = await unplannedQuery.OrderBy(task => task.CreatedAt).ToListAsync();

        Dictionary<(string, int), string> parents = await LoadParentNamesAsync(db, tasks.Concat(unplanned));

        _planned = Layout(tasks
            .Select(task => new PlannedTask(task, ParentLabel(parents, task), task.PlannedStart!.Value, EndOf(task)))
            .Where(entry => entry.End > _weekStart && entry.Start < weekEnd)
            .ToList());

        _unplanned = [.. unplanned.Select(task => new UnplannedTask(task, ParentLabel(parents, task)))];

        _firstHour = _planned.Select(entry => entry.VisibleStart.Hour).Append(DefaultFirstHour).Min();
        _lastHour = _planned.Select(entry => EndHour(entry)).Append(DefaultLastHour).Max();
    }

    /// <summary>Heure pleine qui suit la fin visible d'une tâche — 24 quand elle court jusqu'à minuit.</summary>
    private static int EndHour(PlannedTask entry)
    {
        if (entry.VisibleEnd >= entry.Day.AddDays(1))
        {
            return 24;
        }

        TimeSpan time = entry.VisibleEnd.TimeOfDay;
        return (int)Math.Ceiling(time.TotalHours);
    }

    /// <summary>
    /// Titre de l'objet parent de chaque tâche, en une requête par type : la tâche ne porte que le
    /// couple type/identifiant, et afficher « Tâche 12 » sans dire de quel ticket ne servirait à rien.
    /// </summary>
    private static async Task<Dictionary<(string, int), string>> LoadParentNamesAsync(DbContext db, IEnumerable<ItilTask> tasks)
    {
        Dictionary<(string, int), string> names = [];
        List<ItilTask> all = [.. tasks];

        List<int> ticketIds = [.. all.Where(task => task.ItemType == ItemTypes.Ticket).Select(task => task.ItemId).Distinct()];
        List<int> problemIds = [.. all.Where(task => task.ItemType == ItemTypes.Problem).Select(task => task.ItemId).Distinct()];
        List<int> changeIds = [.. all.Where(task => task.ItemType == ItemTypes.Change).Select(task => task.ItemId).Distinct()];

        foreach (var entry in await db.Set<Ticket>().AsNoTracking().Where(item => ticketIds.Contains(item.Id))
                     .Select(item => new { item.Id, item.Name }).ToListAsync())
        {
            names[(ItemTypes.Ticket, entry.Id)] = entry.Name;
        }

        foreach (var entry in await db.Set<Problem>().AsNoTracking().Where(item => problemIds.Contains(item.Id))
                     .Select(item => new { item.Id, item.Name }).ToListAsync())
        {
            names[(ItemTypes.Problem, entry.Id)] = entry.Name;
        }

        foreach (var entry in await db.Set<Change>().AsNoTracking().Where(item => changeIds.Contains(item.Id))
                     .Select(item => new { item.Id, item.Name }).ToListAsync())
        {
            names[(ItemTypes.Change, entry.Id)] = entry.Name;
        }

        return names;
    }

    /// <summary>
    /// Un parent introuvable (supprimé, ou hors du périmètre d'entités de l'utilisateur) se dit comme
    /// tel plutôt que de faire disparaître la tâche : elle occupe quand même le technicien.
    /// </summary>
    private static string ParentLabel(Dictionary<(string, int), string> names, ItilTask task) =>
        names.TryGetValue((task.ItemType, task.ItemId), out string? name)
            ? $"{TypeLabel(task.ItemType)} #{task.ItemId} — {name}"
            : $"{TypeLabel(task.ItemType)} #{task.ItemId}";

    private static DateTime EndOf(ItilTask task)
    {
        DateTime start = task.PlannedStart!.Value;
        return task.PlannedEnd is { } end && end > start ? end : start + DefaultDuration;
    }

    private static string TypeLabel(string itemType) => itemType switch
    {
        ItemTypes.Ticket => "Ticket",
        ItemTypes.Problem => "Problème",
        ItemTypes.Change => "Changement",
        _ => itemType,
    };

    private static string TypeIcon(string itemType) => itemType switch
    {
        ItemTypes.Ticket => "ti-ticket",
        ItemTypes.Problem => "ti-bulb",
        ItemTypes.Change => "ti-replace",
        _ => "ti-checklist",
    };

    private static string TypeCss(string itemType) => itemType switch
    {
        ItemTypes.Ticket => "glping-planning-ticket",
        ItemTypes.Problem => "glping-planning-problem",
        ItemTypes.Change => "glping-planning-change",
        _ => string.Empty,
    };

    private static string UrlOf(ItilTask task) => task.ItemType switch
    {
        ItemTypes.Ticket => $"/assistance/tickets/{task.ItemId}",
        ItemTypes.Problem => $"/assistance/problems/{task.ItemId}",
        ItemTypes.Change => $"/assistance/changes/{task.ItemId}",
        _ => "#",
    };

    private string NameOfUser(int? id) => id is { } value
        ? _users.FirstOrDefault(user => user.Id == value)?.Name ?? $"#{value} (supprimé)"
        : "Non attribuée";

    /// <summary>
    /// Place les tâches d'un même jour qui se chevauchent côte à côte : chaque groupe de tâches qui
    /// se recouvrent partage la largeur de la colonne en autant de couloirs qu'il en faut.
    /// </summary>
    private List<PlannedTask> Layout(List<PlannedTask> tasks)
    {
        List<PlannedTask> placed = [];

        foreach (DateTime day in Days)
        {
            DateTime dayStart = day;
            DateTime dayEnd = day.AddDays(1);

            // Une tâche qui passe minuit se dessine dans chacun des jours qu'elle couvre, coupée aux bords.
            List<PlannedTask> ofDay = [.. tasks
                .Where(entry => entry.Start < dayEnd && entry.End > dayStart)
                .Select(entry => entry with
                {
                    Day = day,
                    VisibleStart = entry.Start < dayStart ? dayStart : entry.Start,
                    VisibleEnd = entry.End > dayEnd ? dayEnd : entry.End,
                })
                .OrderBy(entry => entry.VisibleStart)
                .ThenByDescending(entry => entry.VisibleEnd)];

            // Un groupe se ferme quand une tâche commence après la fin de toutes celles du groupe.
            List<PlannedTask> cluster = [];
            DateTime clusterEnd = DateTime.MinValue;

            foreach (PlannedTask entry in ofDay)
            {
                if (cluster.Count > 0 && entry.VisibleStart >= clusterEnd)
                {
                    placed.AddRange(AssignLanes(cluster));
                    cluster = [];
                    clusterEnd = DateTime.MinValue;
                }

                cluster.Add(entry);
                if (entry.VisibleEnd > clusterEnd)
                {
                    clusterEnd = entry.VisibleEnd;
                }
            }

            placed.AddRange(AssignLanes(cluster));
        }

        return placed;
    }

    private static IEnumerable<PlannedTask> AssignLanes(List<PlannedTask> cluster)
    {
        if (cluster.Count == 0)
        {
            return [];
        }

        List<DateTime> laneEnds = [];
        List<PlannedTask> withLane = [];

        foreach (PlannedTask entry in cluster)
        {
            int lane = laneEnds.FindIndex(end => end <= entry.VisibleStart);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(entry.VisibleEnd);
            }
            else
            {
                laneEnds[lane] = entry.VisibleEnd;
            }

            withLane.Add(entry with { Lane = lane });
        }

        return withLane.Select(entry => entry with { Lanes = laneEnds.Count });
    }

    private string StyleOf(PlannedTask entry)
    {
        double top = (entry.VisibleStart - entry.Day.AddHours(_firstHour)).TotalHours * HourHeight;
        double height = Math.Max(22, (entry.VisibleEnd - entry.VisibleStart).TotalHours * HourHeight - 2);
        double width = 100.0 / entry.Lanes;
        double left = width * entry.Lane;

        return FormattableString.Invariant(
            $"top: {top:0.#}px; height: {height:0.#}px; left: calc({left:0.###}% + 2px); width: calc({width:0.###}% - 4px);");
    }

    /// <summary>
    /// Ce qu'une tâche montre dépend de la place qu'elle a : l'objet parent et le technicien sous le
    /// titre ne tiennent qu'à partir d'une heure et demie, et une tâche de moins de trois quarts
    /// d'heure met heure et titre sur une seule ligne. Couper le texte à mi-ligne serait pire que
    /// de le taire — l'infobulle et la fenêtre de planification disent tout.
    /// </summary>
    private static string SizeCss(PlannedTask entry) => (entry.VisibleEnd - entry.VisibleStart).TotalHours switch
    {
        < 0.75 => "is-tiny",
        < 1.5 => "is-short",
        _ => string.Empty,
    };

    /// <summary>Position de la ligne « maintenant », ou null si aujourd'hui n'est pas dans la semaine affichée.</summary>
    private string? NowLineStyle(DateTime day)
    {
        DateTime now = Now;
        if (day != now.Date || now.Hour < _firstHour || now.Hour >= _lastHour)
        {
            return null;
        }

        double top = (now - day.AddHours(_firstHour)).TotalHours * HourHeight;
        return FormattableString.Invariant($"top: {top:0.#}px;");
    }

    // ---- Planification d'une tâche -----------------------------------------------------------

    private async Task OpenAsync(ItilTask task, string parentLabel)
    {
        _editing = task;
        _editingParent = parentLabel;
        _editStart = task.PlannedStart;
        _editEnd = task.PlannedEnd;
        _editUserId = task.AssignedUserId ?? 0;
        _editDone = task.State == ItilTaskState.Done;
        _editError = null;

        await JS.InvokeVoidAsync("glping.showModal", "planTaskModal");
    }

    private async Task SaveTaskAsync()
    {
        if (_editing is null)
        {
            return;
        }

        if (_editStart is null && _editEnd is not null)
        {
            _editError = "Une fin sans début ne se place pas dans le planning.";
            return;
        }

        if (_editStart is { } start && _editEnd is { } end && end <= start)
        {
            _editError = "La fin doit être postérieure au début.";
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        ItilTask? stored = await db.Set<ItilTask>().FirstOrDefaultAsync(task => task.Id == _editing.Id);
        if (stored is null)
        {
            _editError = "Cette tâche n'existe plus.";
            return;
        }

        List<string> changes = [];

        if (stored.PlannedStart != _editStart || stored.PlannedEnd != _editEnd)
        {
            changes.Add($"planifiée {Describe(stored.PlannedStart, stored.PlannedEnd)} → {Describe(_editStart, _editEnd)}");
        }

        int? userId = _editUserId == 0 ? null : _editUserId;
        if (stored.AssignedUserId != userId)
        {
            changes.Add($"technicien {NameOfUser(stored.AssignedUserId)} → {NameOfUser(userId)}");
        }

        ItilTaskState state = _editDone ? ItilTaskState.Done : ItilTaskState.ToDo;
        if (stored.State != state)
        {
            changes.Add(_editDone ? "terminée" : "rouverte");
            stored.CompletedAt = _editDone ? DateTime.UtcNow : null;
        }

        stored.PlannedStart = _editStart;
        stored.PlannedEnd = _editEnd;
        stored.AssignedUserId = userId;
        stored.State = state;

        // La planification se trace sur l'objet parent, là où l'on cherchera qui a déplacé quoi.
        if (changes.Count > 0)
        {
            db.Set<AssistanceHistoryEntry>().Add(new AssistanceHistoryEntry
            {
                ItemType = stored.ItemType,
                ItemId = stored.ItemId,
                User = _currentUserName,
                Field = "Tâche",
                Description = $"« {stored.Content} » : {string.Join(", ", changes)} (depuis le planning)",
            });
        }

        await db.SaveChangesAsync();

        _editing = null;
        await JS.InvokeVoidAsync("glping.hideModal", "planTaskModal");
        await LoadAsync();
    }

    private static string Describe(DateTime? start, DateTime? end) => start switch
    {
        null => "(non planifiée)",
        { } from when end is { } to && to.Date == from.Date => $"le {from:dd/MM/yyyy HH:mm}–{to:HH:mm}",
        { } from when end is { } to => $"du {from:dd/MM/yyyy HH:mm} au {to:dd/MM/yyyy HH:mm}",
        { } from => $"le {from:dd/MM/yyyy HH:mm}",
    };

    /// <summary>Une tâche placée dans la grille : son jour, sa portion visible ce jour-là, et son couloir.</summary>
    private sealed record PlannedTask(ItilTask Task, string Parent, DateTime Start, DateTime End)
    {
        public DateTime Day { get; init; }
        public DateTime VisibleStart { get; init; } = Start;
        public DateTime VisibleEnd { get; init; } = End;
        public int Lane { get; init; }
        public int Lanes { get; init; } = 1;
    }

    private sealed record UnplannedTask(ItilTask Task, string Parent);
}
