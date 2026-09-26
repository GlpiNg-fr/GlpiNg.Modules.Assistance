using System.Globalization;
using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.Assistance.Models;
using GlpiNg.Modules.Assistance.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

namespace GlpiNg.Modules.Assistance.Components.Pages.Statistics;

/// <summary>
/// Statistiques de l'assistance (<c>Statistiques</c> de GLPI, vue globale) pour les tickets, les
/// problèmes ou les changements : chiffres de la période, évolution ouverts/résolus/clos, et
/// répartitions. Les calculs vivent dans <see cref="ItilStatistics"/> ; cette page charge, choisit
/// la période et dessine.
/// </summary>
public partial class Index : ComponentBase, IDisposable
{
    [Inject]
    private IDbContextFactory<DbContext> DbFactory { get; set; } = null!;

    [Inject]
    private IPrincipalDirectory Directory { get; set; } = null!;

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    // Géométrie du graphique, en pixels : le SVG est dessiné à la largeur réelle de sa carte,
    // mesurée par glping.observeWidth, pour que le texte ne soit jamais étiré avec lui.
    private int _chartWidth = 960;
    private const int ChartHeight = 260;
    private const int PlotLeft = 44;
    private const int PlotTop = 14;
    private const int PlotBottom = 228;

    /// <summary>Place laissée à droite pour les étiquettes de fin de courbe (« Résolus 12 »).</summary>
    private int PlotRight => _chartWidth - 84;

    private int ChartWidth => _chartWidth;

    private ElementReference _chartRef;
    private DotNetObjectReference<Index>? _selfRef;

    /// <summary>« tickets », « problems » ou « changes ».</summary>
    private string _itemType = "tickets";

    /// <summary>Période prédéfinie, ou « custom » dès qu'une date est touchée à la main.</summary>
    private string _preset = "12m";

    private DateTime _from;
    private DateTime _to;

    private List<ItilStatItem> _items = [];
    private StatSummary? _summary;
    private List<StatBucket> _buckets = [];
    private List<StatBreakdownRow> _byCategory = [];
    private List<StatBreakdownRow> _byTechnician = [];
    private List<StatBreakdownRow> _byPriority = [];
    private List<StatBreakdownRow> _byType = [];
    private List<(string Label, int Count)> _backlogByStatus = [];
    private IReadOnlyList<PrincipalOption> _users = [];

    private int? _hovered;
    private bool _showTable;
    private int _yMax = 1;
    private List<int> _yTicks = [];

    private bool IsTickets => _itemType == "tickets";

    /// <summary>Fin de période exclusive : la date « au » saisie est incluse jusqu'à minuit.</summary>
    private DateTime ToExclusive => _to.Date.AddDays(1);

    private string ItemsLabel => _itemType switch
    {
        "problems" => "problèmes",
        "changes" => "changements",
        _ => "tickets",
    };

    protected override async Task OnInitializedAsync()
    {
        ApplyPreset();
        _users = await Directory.GetAsync(PrincipalKind.User);
        await LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Le graphique disparaît derrière la vue tableau et revient : l'appel est idempotent par
        // élément, il suffit de le refaire à chaque rendu où le graphique est affiché.
        if (_summary is null || _showTable)
        {
            return;
        }

        _selfRef ??= DotNetObjectReference.Create(this);

        try
        {
            await JS.InvokeVoidAsync("glping.observeWidth", _chartRef, _selfRef, nameof(OnChartResized));
        }
        catch (JSDisconnectedException)
        {
        }
    }

    [JSInvokable]
    public void OnChartResized(int width)
    {
        // Sous 480 px il n'y a plus de quoi lire douze mois : on garde un plancher, quitte à défiler.
        _chartWidth = Math.Max(480, width);
        StateHasChanged();
    }

    public void Dispose() => _selfRef?.Dispose();

    private void ApplyPreset()
    {
        DateTime today = Display.ToUserTime(DateTime.UtcNow).Date;

        (_from, _to) = _preset switch
        {
            "30d" => (today.AddDays(-29), today),
            "3m" => (new DateTime(today.Year, today.Month, 1).AddMonths(-2), today),
            "year" => (new DateTime(today.Year, 1, 1), today),
            "lastyear" => (new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year - 1, 12, 31)),

            // Douze mois pleins, le mois en cours compris : c'est la vue d'ensemble par défaut.
            _ => (new DateTime(today.Year, today.Month, 1).AddMonths(-11), today),
        };
    }

    private async Task OnItemTypeChangedAsync(ChangeEventArgs args)
    {
        _itemType = args.Value?.ToString() ?? "tickets";
        await LoadAsync();
    }

    private void OnPresetChanged(ChangeEventArgs args)
    {
        _preset = args.Value?.ToString() ?? "12m";
        if (_preset != "custom")
        {
            ApplyPreset();
            Compute();
        }
    }

    private void OnFromChanged(ChangeEventArgs args)
    {
        if (DateTime.TryParse(args.Value?.ToString(), CultureInfo.InvariantCulture, out DateTime value))
        {
            _from = value.Date;
            _preset = "custom";
            Compute();
        }
    }

    private void OnToChanged(ChangeEventArgs args)
    {
        if (DateTime.TryParse(args.Value?.ToString(), CultureInfo.InvariantCulture, out DateTime value))
        {
            _to = value.Date;
            _preset = "custom";
            Compute();
        }
    }

    private async Task LoadAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _items = _itemType switch
        {
            "problems" => [.. (await db.Set<Problem>().AsNoTracking().Include(item => item.Category).ToListAsync())
                .Select(item => new ItilStatItem(
                    UserTime(item.OpenedAt), UserTime(item.SolvedAt), UserTime(item.ClosedAt),
                    item.IsOpen, item.IsOverdue, ProblemLabels.For(item.Status), item.Category?.Name,
                    item.AssignedUserId, item.Priority, null, null))],

            "changes" => [.. (await db.Set<Change>().AsNoTracking().Include(item => item.Category).ToListAsync())
                .Select(item => new ItilStatItem(
                    UserTime(item.OpenedAt), UserTime(item.SolvedAt), UserTime(item.ClosedAt),
                    item.IsOpen, item.IsOverdue, ChangeLabels.For(item.Status), item.Category?.Name,
                    item.AssignedUserId, item.Priority, null, null))],

            _ => [.. (await db.Set<Ticket>().AsNoTracking().Include(item => item.Category).ToListAsync())
                .Select(item => new ItilStatItem(
                    UserTime(item.OpenedAt), UserTime(item.SolvedAt), UserTime(item.ClosedAt),
                    item.IsOpen, item.IsOverdue, TicketLabels.For(item.Status), item.Category?.Name,
                    item.AssignedUserId, item.Priority, TicketLabels.For(item.Type),
                    // Engagement de résolution tenu ou non : seuls les tickets résolus qui en
                    // portaient un comptent, les autres n'ont rien à respecter.
                    item.TimeToResolve is { } deadline && item.SolvedAt is { } solved ? solved <= deadline : null))],
        };

        Compute();
    }

    private void Compute()
    {
        if (_to < _from)
        {
            (_from, _to) = (_to, _from);
        }

        DateTime to = ToExclusive;

        _summary = ItilStatistics.Summarize(_items, _from, to);
        _buckets = ItilStatistics.Buckets(_items, _from, to);

        _byCategory = ItilStatistics.Breakdown(_items, _from, to, item => item.Category ?? "Sans catégorie");
        _byTechnician = ItilStatistics.Breakdown(_items, _from, to, item => NameOfUser(item.AssignedUserId));
        _byPriority = ItilStatistics.Breakdown(_items, _from, to, item => ItilLabels.For(item.Priority),
            [.. Enum.GetValues<ItilLevel>().Reverse().Select(ItilLabels.For)]);
        _byType = IsTickets
            ? ItilStatistics.Breakdown(_items, _from, to, item => item.TypeLabel ?? "?",
                [TicketLabels.For(TicketType.Incident), TicketLabels.For(TicketType.Request)])
            : [];

        _backlogByStatus = [.. _items
            .Where(item => item.IsOpen)
            .GroupBy(item => item.StatusLabel)
            .Select(group => (group.Key, group.Count()))
            .OrderByDescending(entry => entry.Item2)];

        int max = _buckets.Count == 0 ? 0 : _buckets.Max(bucket => Math.Max(bucket.Opened, Math.Max(bucket.Solved, bucket.Closed)));
        (_yMax, _yTicks) = NiceScale(max);
        _hovered = null;
    }

    /// <summary>
    /// Ouverture, résolution et clôture sont posées en UTC : on les ramène dans le fuseau de
    /// l'utilisateur, celui dans lequel il dit « ouvert en septembre » ou « résolu lundi ».
    /// </summary>
    private DateTime UserTime(DateTime utc) => Display.ToUserTime(utc);

    private DateTime? UserTime(DateTime? utc) => utc is { } value ? Display.ToUserTime(value) : null;

    private string NameOfUser(int? id) => id is { } value
        ? _users.FirstOrDefault(user => user.Id == value)?.Name ?? $"#{value} (supprimé)"
        : "Non attribué";

    /// <summary>Plafond de l'axe arrondi à 1, 2 ou 5 × 10ⁿ, avec des graduations entières.</summary>
    private static (int Max, List<int> Ticks) NiceScale(int max)
    {
        if (max <= 0)
        {
            return (4, [0, 1, 2, 3, 4]);
        }

        double rawStep = max / 4.0;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        double step = new[] { 1, 2, 5, 10 }.Select(factor => factor * magnitude).First(candidate => candidate >= rawStep);
        int intStep = Math.Max(1, (int)Math.Ceiling(step));
        int top = (int)Math.Ceiling(max / (double)intStep) * intStep;

        List<int> ticks = [];
        for (int tick = 0; tick <= top; tick += intStep)
        {
            ticks.Add(tick);
        }

        return (top, ticks);
    }

    // ---- Géométrie du graphique -------------------------------------------------------------

    private double X(int index) => _buckets.Count <= 1
        ? (PlotLeft + PlotRight) / 2.0
        : PlotLeft + index * (PlotRight - PlotLeft) / (double)(_buckets.Count - 1);

    private double Y(int value) => PlotBottom - value * (PlotBottom - PlotTop) / (double)_yMax;

    private string Points(Func<StatBucket, int> value) => string.Join(" ",
        _buckets.Select((bucket, index) => FormattableString.Invariant($"{X(index):0.#},{Y(value(bucket)):0.#}")));

    /// <summary>Largeur de la zone de survol d'un pas : plus large que la marque, pour ne pas viser au pixel.</summary>
    private double SlotWidth => _buckets.Count <= 1 ? PlotRight - PlotLeft : (PlotRight - PlotLeft) / (double)(_buckets.Count - 1);

    /// <summary>Une étiquette d'axe sur combien : une douzaine au plus, pour qu'elles ne se chevauchent pas.</summary>
    private int LabelStep => Math.Max(1, (int)Math.Ceiling(_buckets.Count / 12.0));

    /// <summary>
    /// Étiquettes de fin de courbe, seulement si elles ne se chevauchent pas : trois courbes qui
    /// finissent au même niveau feraient trois étiquettes empilées et illisibles. La légende et
    /// l'infobulle prennent alors le relais.
    /// </summary>
    private List<(string Label, int Value, double Y)> EndLabels
    {
        get
        {
            if (_buckets.Count == 0)
            {
                return [];
            }

            StatBucket last = _buckets[^1];
            List<(string Label, int Value, double Y)> labels =
            [
                ("Ouverts", last.Opened, Y(last.Opened)),
                ("Résolus", last.Solved, Y(last.Solved)),
                ("Clos", last.Closed, Y(last.Closed)),
            ];

            List<double> ys = [.. labels.Select(label => label.Y).Order()];
            bool collide = ys.Zip(ys.Skip(1), (a, b) => b - a).Any(gap => gap < 14);

            return collide ? [] : labels;
        }
    }

    private string TooltipStyle(int index)
    {
        double percent = X(index) / ChartWidth * 100;

        // Passé le milieu, l'infobulle s'ouvre à gauche du point pour ne pas sortir de la carte.
        return percent > 55
            ? FormattableString.Invariant($"right: {100 - percent + 1.5:0.##}%;")
            : FormattableString.Invariant($"left: {percent + 1.5:0.##}%;");
    }

    private static string Percent(int part, int total) =>
        total == 0 ? "—" : (part * 100.0 / total).ToString("0", French) + " %";

    private static string BarWidth(int value, int max) =>
        max == 0 ? "0%" : FormattableString.Invariant($"{value * 100.0 / max:0.#}%");
}
