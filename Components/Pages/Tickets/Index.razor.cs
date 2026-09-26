using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.Abstractions.Items;
using GlpiNg.Modules.Abstractions.Notifications;
using GlpiNg.Modules.Assistance.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

namespace GlpiNg.Modules.Assistance.Components.Pages.Tickets;

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

    private List<Ticket> _tickets = [];
    private List<Ticket> _filtered = [];
    private List<TicketCategory> _categories = [];
    private IReadOnlyList<PrincipalOption> _users = [];
    private readonly HashSet<int> _selectedIds = [];

    private string _searchTerm = string.Empty;

    /// <summary>« open » (par défaut), « all », ou la valeur numérique d'un statut précis.</summary>
    private string _statusFilter = "open";

    private bool _mineOnly;
    private bool _overdueOnly;

    private Ticket _newTicket = NewBlank();
    private int _newCategoryId;
    private int _newRequesterId;

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

        _tickets = await db.Set<Ticket>()
            .AsNoTracking()
            .Include(ticket => ticket.Category)
            // Les tickets ouverts d'abord, puis du plus récent au plus ancien : c'est l'ordre dans
            // lequel on les traite, et il évite d'avoir à trier à chaque ouverture de la liste.
            .OrderByDescending(ticket => ticket.Status < TicketStatus.Solved)
            .ThenByDescending(ticket => ticket.OpenedAt)
            .ToListAsync();

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

    private string RequesterOf(Ticket ticket)
    {
        if (!string.IsNullOrWhiteSpace(ticket.RequesterName))
        {
            return ticket.RequesterName;
        }

        return ticket.RequesterUserId is { } id ? NameOfUser(id) : string.Empty;
    }

    private string AssigneeOf(Ticket ticket) =>
        ticket.AssignedUserId is { } id ? NameOfUser(id) : string.Empty;

    /// <summary>
    /// Un acteur supprimé depuis l'ouverture du ticket laisse un identifiant sans nom : le dire
    /// vaut mieux qu'afficher un numéro ou une case vide, qui laisserait croire à un oubli.
    /// </summary>
    private string NameOfUser(int id) =>
        _users.FirstOrDefault(user => user.Id == id)?.Name ?? $"#{id} (supprimé)";

    private void OnSearchChanged(KeyboardEventArgs args) => ApplyFilter();

    private void OnMineOnlyChanged(bool value)
    {
        _mineOnly = value;
        ApplyFilter();
    }

    private void OnOverdueOnlyChanged(bool value)
    {
        _overdueOnly = value;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<Ticket> query = _tickets;

        query = _statusFilter switch
        {
            "open" => query.Where(ticket => ticket.IsOpen),
            "all" => query,
            _ when int.TryParse(_statusFilter, out int status) => query.Where(ticket => (int)ticket.Status == status),
            _ => query,
        };

        if (_mineOnly)
        {
            // Sans utilisateur identifié, « qui me sont attribués » ne peut rien vouloir dire :
            // mieux vaut une liste vide qu'une liste fausse.
            query = _currentUserId is { } userId
                ? query.Where(ticket => ticket.AssignedUserId == userId)
                : [];
        }

        if (_overdueOnly)
        {
            query = query.Where(ticket => ticket.IsOverdue);
        }

        string term = _searchTerm.Trim();

        if (term.Length > 0)
        {
            query = query.Where(ticket =>
                ticket.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (ticket.Content?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || RequesterOf(ticket).Contains(term, StringComparison.OrdinalIgnoreCase)
                || (ticket.Category?.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        _filtered = [.. query];
        _selectedIds.IntersectWith(_filtered.Select(ticket => ticket.Id));
    }

    private void ToggleSelectAll(bool selectAll)
    {
        _selectedIds.Clear();
        if (selectAll)
        {
            foreach (Ticket ticket in _filtered)
            {
                _selectedIds.Add(ticket.Id);
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

        List<Ticket> toDelete = await db.Set<Ticket>()
            .Where(ticket => _selectedIds.Contains(ticket.Id))
            .ToListAsync();

        // Suivis, tâches et historique sont dans des tables polymorphes, donc sans clé étrangère
        // vers le ticket : rien ne part en cascade, tout part ici. Un ticket supprimé qui laisse
        // ses suivis derrière lui les rendrait à un futur ticket portant le même identifiant.
        db.Set<ItilFollowup>().RemoveRange(await db.Set<ItilFollowup>()
            .Where(entry => entry.ItemType == ItemTypes.Ticket && _selectedIds.Contains(entry.ItemId))
            .ToListAsync());

        db.Set<ItilTask>().RemoveRange(await db.Set<ItilTask>()
            .Where(entry => entry.ItemType == ItemTypes.Ticket && _selectedIds.Contains(entry.ItemId))
            .ToListAsync());

        db.Set<AssistanceHistoryEntry>().RemoveRange(await db.Set<AssistanceHistoryEntry>()
            .Where(entry => entry.ItemType == ItemTypes.Ticket && _selectedIds.Contains(entry.ItemId))
            .ToListAsync());

        // Le rattachement à un problème, lui, a bien une clé étrangère : il part en cascade.
        db.Set<Ticket>().RemoveRange(toDelete);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(_newTicket.Name))
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _newTicket.Name = _newTicket.Name.Trim();
        _newTicket.CategoryId = _newCategoryId == 0 ? null : _newCategoryId;
        _newTicket.RequesterUserId = _newRequesterId == 0 ? null : _newRequesterId;
        _newTicket.Priority = ItilPriorityMatrix.Compute(_newTicket.Urgency, _newTicket.Impact);

        db.Set<Ticket>().Add(_newTicket);
        await db.SaveChangesAsync();

        // L'ouverture est tracée comme une modification : l'onglet Historique commence donc par
        // dire qui a ouvert le ticket, et non par une page vide.
        db.Set<AssistanceHistoryEntry>().Add(new AssistanceHistoryEntry
        {
            ItemType = ItemTypes.Ticket,
            ItemId = _newTicket.Id,
            User = _currentUserName,
            Field = "Ticket",
            Description = "Ouverture du ticket",
        });

        await db.SaveChangesAsync();

        await NotifyAsync(_newTicket);

        int createdId = _newTicket.Id;

        _newTicket = NewBlank();
        _newCategoryId = 0;
        _newRequesterId = 0;

        await JS.InvokeVoidAsync("glping.hideModal", "newTicketModal");

        // On ouvre la fiche : un ticket qu'on vient de saisir se complète tout de suite
        // (attribution, impact, échéance), et revenir à la liste obligerait à l'y rechercher.
        Navigation.NavigateTo($"/assistance/tickets/{createdId}");
    }

    private async Task NotifyAsync(Ticket ticket)
    {
        Dictionary<string, string?> variables = new(StringComparer.Ordinal)
        {
            ["ticket.id"] = ticket.Id.ToString(),
            ["ticket.title"] = ticket.Name,
            ["ticket.type"] = TicketLabels.For(ticket.Type),
            ["ticket.status"] = TicketLabels.For(ticket.Status),
            ["ticket.priority"] = ItilLabels.For(ticket.Priority),
            ["ticket.requester"] = RequesterOf(ticket),
            ["ticket.category"] = _categories.FirstOrDefault(category => category.Id == ticket.CategoryId)?.Name,
            ["ticket.url"] = $"/assistance/tickets/{ticket.Id}",
        };

        await Notifications.PublishAsync(ItemTypes.Ticket, "new", ticket.Id, variables);
    }

    private static Ticket NewBlank() => new() { Name = string.Empty };
}
