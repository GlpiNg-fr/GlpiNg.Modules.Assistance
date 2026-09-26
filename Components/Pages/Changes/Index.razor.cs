using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.Abstractions.Items;
using GlpiNg.Modules.Abstractions.Notifications;
using GlpiNg.Modules.Assistance.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

namespace GlpiNg.Modules.Assistance.Components.Pages.Changes;

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

    private List<Change> _changes = [];
    private List<Change> _filtered = [];
    private List<TicketCategory> _categories = [];
    private IReadOnlyList<PrincipalOption> _users = [];
    private readonly HashSet<int> _selectedIds = [];

    /// <summary>Changements dont une approbation attend l'utilisateur connecté.</summary>
    private HashSet<int> _awaitingMyApproval = [];

    private string _searchTerm = string.Empty;

    /// <summary>« open » (par défaut), « all », ou la valeur numérique d'un statut précis.</summary>
    private string _statusFilter = "open";

    private bool _mineOnly;
    private bool _toApproveOnly;

    private Change _newChange = NewBlank();
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

        _changes = await db.Set<Change>()
            .AsNoTracking()
            .Include(change => change.Category)
            // Les changements ouverts d'abord, puis du plus récent au plus ancien.
            .OrderByDescending(change => change.Status != ChangeStatus.Solved
                && change.Status != ChangeStatus.Closed
                && change.Status != ChangeStatus.Canceled)
            .ThenByDescending(change => change.OpenedAt)
            .ToListAsync();

        _awaitingMyApproval = _currentUserId is { } userId
            ? [.. await db.Set<ChangeValidation>()
                .AsNoTracking()
                .Where(validation => validation.ValidatorUserId == userId && validation.Status == ChangeValidationStatus.Waiting)
                .Select(validation => validation.ChangeId)
                .Distinct()
                .ToListAsync()]
            : [];

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

    private string AssigneeOf(Change change) =>
        change.AssignedUserId is { } id ? NameOfUser(id) : string.Empty;

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

    private void OnToApproveOnlyChanged(bool value)
    {
        _toApproveOnly = value;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<Change> query = _changes;

        query = _statusFilter switch
        {
            "open" => query.Where(change => change.IsOpen),
            "all" => query,
            _ when int.TryParse(_statusFilter, out int status) => query.Where(change => (int)change.Status == status),
            _ => query,
        };

        if (_mineOnly)
        {
            // Sans utilisateur identifié, « qui me sont attribués » ne peut rien vouloir dire :
            // mieux vaut une liste vide qu'une liste fausse.
            query = _currentUserId is { } userId
                ? query.Where(change => change.AssignedUserId == userId)
                : [];
        }

        if (_toApproveOnly)
        {
            query = query.Where(change => _awaitingMyApproval.Contains(change.Id));
        }

        string term = _searchTerm.Trim();

        if (term.Length > 0)
        {
            query = query.Where(change =>
                change.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (change.Content?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (change.ImpactContent?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (change.RolloutPlanContent?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (change.BackoutPlanContent?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (change.Category?.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        _filtered = [.. query];
        _selectedIds.IntersectWith(_filtered.Select(change => change.Id));
    }

    private void ToggleSelectAll(bool selectAll)
    {
        _selectedIds.Clear();
        if (selectAll)
        {
            foreach (Change change in _filtered)
            {
                _selectedIds.Add(change.Id);
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

        List<Change> toDelete = await db.Set<Change>()
            .Where(change => _selectedIds.Contains(change.Id))
            .ToListAsync();

        // Suivis, tâches et historique sont polymorphes, donc sans clé étrangère : ils partent ici.
        // Approbations et rattachements partent en cascade — les tickets et problèmes rattachés,
        // eux, restent : abandonner un changement ne dit rien de ce qui l'avait motivé.
        db.Set<ItilFollowup>().RemoveRange(await db.Set<ItilFollowup>()
            .Where(entry => entry.ItemType == ItemTypes.Change && _selectedIds.Contains(entry.ItemId))
            .ToListAsync());

        db.Set<ItilTask>().RemoveRange(await db.Set<ItilTask>()
            .Where(entry => entry.ItemType == ItemTypes.Change && _selectedIds.Contains(entry.ItemId))
            .ToListAsync());

        db.Set<AssistanceHistoryEntry>().RemoveRange(await db.Set<AssistanceHistoryEntry>()
            .Where(entry => entry.ItemType == ItemTypes.Change && _selectedIds.Contains(entry.ItemId))
            .ToListAsync());

        db.Set<Change>().RemoveRange(toDelete);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(_newChange.Name))
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _newChange.Name = _newChange.Name.Trim();
        _newChange.CategoryId = _newCategoryId == 0 ? null : _newCategoryId;
        _newChange.AuthorUserId = _currentUserId;
        _newChange.Priority = ItilPriorityMatrix.Compute(_newChange.Urgency, _newChange.Impact);

        db.Set<Change>().Add(_newChange);
        await db.SaveChangesAsync();

        db.Set<AssistanceHistoryEntry>().Add(new AssistanceHistoryEntry
        {
            ItemType = ItemTypes.Change,
            ItemId = _newChange.Id,
            User = _currentUserName,
            Field = "Changement",
            Description = "Ouverture du changement",
        });

        await db.SaveChangesAsync();

        await NotifyAsync(_newChange);

        int createdId = _newChange.Id;

        _newChange = NewBlank();
        _newCategoryId = 0;

        await JS.InvokeVoidAsync("glpiNg.hideModal", "newChangeModal");

        // On ouvre la fiche : c'est là que le changement se prépare (analyse, plans, approbations).
        Navigation.NavigateTo($"/assistance/changes/{createdId}");
    }

    private async Task NotifyAsync(Change change)
    {
        Dictionary<string, string?> variables = new(StringComparer.Ordinal)
        {
            ["change.id"] = change.Id.ToString(),
            ["change.title"] = change.Name,
            ["change.status"] = ChangeLabels.For(change.Status),
            ["change.priority"] = ItilLabels.For(change.Priority),
            ["change.author"] = _currentUserName,
            ["change.category"] = _categories.FirstOrDefault(category => category.Id == change.CategoryId)?.Name,
            ["change.url"] = $"/assistance/changes/{change.Id}",
        };

        await Notifications.PublishAsync(ItemTypes.Change, "new", change.Id, variables);
    }

    private static Change NewBlank() => new() { Name = string.Empty };
}
