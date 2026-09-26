using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.Abstractions.Items;
using GlpiNg.Modules.Abstractions.Notifications;
using GlpiNg.Modules.Assistance.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

namespace GlpiNg.Modules.Assistance.Components.Pages.Problems;

public partial class Index : ComponentBase
{
    [Inject]
    private IDbContextFactory<DbContext> DbFactory { get; set; } = null!;

    [Inject]
    private IPrincipalDirectory Directory { get; set; } = null!;

    [Inject]
    private INotificationPublisher Notifications { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    [CascadingParameter]
    private Task<AuthenticationState>? AuthStateTask { get; set; }

    private List<Problem> _problems = [];
    private List<Problem> _filtered = [];
    private List<TicketCategory> _categories = [];
    private IReadOnlyList<PrincipalOption> _users = [];
    private readonly HashSet<int> _selectedIds = [];

    /// <summary>Nombre d'incidents rattachés par problème, compté en base plutôt que ligne à ligne.</summary>
    private Dictionary<int, int> _ticketCounts = [];

    private string _searchTerm = string.Empty;

    /// <summary>« open » (par défaut), « all », ou la valeur numérique d'un statut précis.</summary>
    private string _statusFilter = "open";

    private bool _mineOnly;
    private bool _unlinkedOnly;

    private Problem _newProblem = NewBlank();
    private int _newCategoryId;

    private string _currentUserName = "?";
    private int? _currentUserId;

    private bool AllSelected => _filtered.Count > 0 && _selectedIds.Count == _filtered.Count;

    protected override async Task OnInitializedAsync()
    {
        if (AuthStateTask is not null)
        {
            AuthenticationState authState = await AuthStateTask;
            _currentUserName = authState.User.Identity?.Name ?? "?";

            string? claim = authState.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            _currentUserId = int.TryParse(claim, out int userId) ? userId : null;
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _problems = await db.Set<Problem>()
            .AsNoTracking()
            .Include(problem => problem.Category)
            // Les problèmes ouverts d'abord, puis du plus récent au plus ancien.
            .OrderByDescending(problem => problem.Status != ProblemStatus.Solved && problem.Status != ProblemStatus.Closed)
            .ThenByDescending(problem => problem.OpenedAt)
            .ToListAsync();

        _ticketCounts = await db.Set<ProblemTicket>()
            .AsNoTracking()
            .GroupBy(link => link.ProblemId)
            .Select(group => new { ProblemId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.ProblemId, entry => entry.Count);

        _categories = await db.Set<TicketCategory>()
            .AsNoTracking()
            .Include(category => category.Parent)
            .Where(category => category.IsActive)
            .OrderBy(category => category.Name)
            .ToListAsync();

        _users = await Directory.GetAsync(PrincipalKind.User);

        _selectedIds.Clear();
        ApplyFilter();
    }

    private static string CategoryPath(TicketCategory category) =>
        category.Parent is { } parent ? $"{parent.Name} > {category.Name}" : category.Name;

    private string AssigneeOf(Problem problem) =>
        problem.AssignedUserId is { } id ? NameOfUser(id) : string.Empty;

    /// <summary>
    /// Un acteur supprimé depuis l'ouverture laisse un identifiant sans nom : le dire vaut mieux
    /// qu'afficher un numéro ou une case vide, qui laisserait croire à un oubli.
    /// </summary>
    private string NameOfUser(int id) =>
        _users.FirstOrDefault(user => user.Id == id)?.Name ?? $"#{id} (supprimé)";

    private void OnSearchChanged(KeyboardEventArgs args) => ApplyFilter();

    private void OnMineOnlyChanged(bool value)
    {
        _mineOnly = value;
        ApplyFilter();
    }

    private void OnUnlinkedOnlyChanged(bool value)
    {
        _unlinkedOnly = value;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<Problem> query = _problems;

        query = _statusFilter switch
        {
            "open" => query.Where(problem => problem.IsOpen),
            "all" => query,
            _ when int.TryParse(_statusFilter, out int status) => query.Where(problem => (int)problem.Status == status),
            _ => query,
        };

        if (_mineOnly)
        {
            // Sans utilisateur identifié, « qui me sont attribués » ne peut rien vouloir dire :
            // mieux vaut une liste vide qu'une liste fausse.
            query = _currentUserId is { } userId
                ? query.Where(problem => problem.AssignedUserId == userId)
                : [];
        }

        if (_unlinkedOnly)
        {
            query = query.Where(problem => _ticketCounts.GetValueOrDefault(problem.Id) == 0);
        }

        string term = _searchTerm.Trim();

        if (term.Length > 0)
        {
            query = query.Where(problem =>
                problem.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (problem.Content?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (problem.CauseContent?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (problem.SymptomContent?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (problem.Category?.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        _filtered = [.. query];
        _selectedIds.IntersectWith(_filtered.Select(problem => problem.Id));
    }

    private void ToggleSelectAll(bool selectAll)
    {
        _selectedIds.Clear();
        if (selectAll)
        {
            foreach (Problem problem in _filtered)
            {
                _selectedIds.Add(problem.Id);
            }
        }
    }

    private void ToggleSelect(int id, bool selected)
    {
        if (selected)
        {
            _selectedIds.Add(id);
        }
        else
        {
            _selectedIds.Remove(id);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (_selectedIds.Count == 0)
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        List<Problem> toDelete = await db.Set<Problem>()
            .Where(problem => _selectedIds.Contains(problem.Id))
            .ToListAsync();

        // Suivis, tâches et historique sont polymorphes, donc sans clé étrangère : ils partent ici.
        // Le rattachement aux incidents, lui, part en cascade — et les incidents eux-mêmes restent,
        // un problème abandonné ne devant pas emporter les tickets qu'il expliquait.
        db.Set<ItilFollowup>().RemoveRange(await db.Set<ItilFollowup>()
            .Where(entry => entry.ItemType == ItemTypes.Problem && _selectedIds.Contains(entry.ItemId))
            .ToListAsync());

        db.Set<ItilTask>().RemoveRange(await db.Set<ItilTask>()
            .Where(entry => entry.ItemType == ItemTypes.Problem && _selectedIds.Contains(entry.ItemId))
            .ToListAsync());

        db.Set<AssistanceHistoryEntry>().RemoveRange(await db.Set<AssistanceHistoryEntry>()
            .Where(entry => entry.ItemType == ItemTypes.Problem && _selectedIds.Contains(entry.ItemId))
            .ToListAsync());

        db.Set<Problem>().RemoveRange(toDelete);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(_newProblem.Name))
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _newProblem.Name = _newProblem.Name.Trim();
        _newProblem.CategoryId = _newCategoryId == 0 ? null : _newCategoryId;
        _newProblem.AuthorUserId = _currentUserId;
        _newProblem.Priority = ItilPriorityMatrix.Compute(_newProblem.Urgency, _newProblem.Impact);

        db.Set<Problem>().Add(_newProblem);
        await db.SaveChangesAsync();

        db.Set<AssistanceHistoryEntry>().Add(new AssistanceHistoryEntry
        {
            ItemType = ItemTypes.Problem,
            ItemId = _newProblem.Id,
            User = _currentUserName,
            Field = "Problème",
            Description = "Ouverture du problème",
        });

        await db.SaveChangesAsync();

        await NotifyAsync(_newProblem);

        int createdId = _newProblem.Id;

        _newProblem = NewBlank();
        _newCategoryId = 0;

        await JS.InvokeVoidAsync("glping.hideModal", "newProblemModal");

        // On ouvre la fiche : c'est là que se fait le travail (analyse, rattachement des incidents),
        // et revenir à la liste obligerait à l'y rechercher.
        Navigation.NavigateTo($"/assistance/problems/{createdId}");
    }

    private async Task NotifyAsync(Problem problem)
    {
        Dictionary<string, string?> variables = new(StringComparer.Ordinal)
        {
            ["problem.id"] = problem.Id.ToString(),
            ["problem.title"] = problem.Name,
            ["problem.status"] = ProblemLabels.For(problem.Status),
            ["problem.priority"] = ItilLabels.For(problem.Priority),
            ["problem.author"] = _currentUserName,
            ["problem.category"] = _categories.FirstOrDefault(category => category.Id == problem.CategoryId)?.Name,
            ["problem.url"] = $"/assistance/problems/{problem.Id}",
        };

        await Notifications.PublishAsync(ItemTypes.Problem, "new", problem.Id, variables);
    }

    private static Problem NewBlank() => new() { Name = string.Empty };
}
