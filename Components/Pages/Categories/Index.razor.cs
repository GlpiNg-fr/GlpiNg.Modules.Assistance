using GlpiNg.Modules.Assistance.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

namespace GlpiNg.Modules.Assistance.Components.Pages.Categories;

public partial class Index : ComponentBase
{
    [Inject]
    private IDbContextFactory<DbContext> DbFactory { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    private List<TicketCategory> _categories = [];
    private List<TicketCategory> _filtered = [];

    /// <summary>Nombre de tickets par catégorie : ce qui dit si une catégorie sert, et ce qu'on perdrait à la supprimer.</summary>
    private Dictionary<int, int> _ticketCounts = [];

    private readonly HashSet<int> _selectedIds = [];
    private string _searchTerm = string.Empty;
    private bool _showInactive;
    private string? _error;

    private TicketCategory _edited = NewBlank();
    private int _editedParentId;

    private bool AllSelected => _filtered.Count > 0 && _selectedIds.Count == _filtered.Count;

    /// <summary>
    /// Parents possibles : tout sauf la catégorie éditée et sa descendance, faute de quoi un cycle
    /// (« A sous B, B sous A ») rendrait l'arbre impossible à parcourir.
    /// </summary>
    private IEnumerable<TicketCategory> ParentCandidates
    {
        get
        {
            if (_edited.Id == 0)
            {
                return _categories;
            }

            HashSet<int> excluded = [_edited.Id];

            // La descendance se déduit en repassant sur la liste tant qu'elle grandit : les arbres
            // de catégories tiennent en quelques dizaines de lignes, déjà toutes en mémoire.
            bool grown = true;
            while (grown)
            {
                grown = false;
                foreach (TicketCategory category in _categories)
                {
                    if (category.ParentId is { } parentId && excluded.Contains(parentId) && excluded.Add(category.Id))
                    {
                        grown = true;
                    }
                }
            }

            return _categories.Where(category => !excluded.Contains(category.Id));
        }
    }

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _categories = await db.Set<TicketCategory>()
            .AsNoTracking()
            .Include(category => category.Parent)
            .OrderBy(category => category.Name)
            .ToListAsync();

        _ticketCounts = await db.Set<Ticket>()
            .AsNoTracking()
            .Where(ticket => ticket.CategoryId != null)
            .GroupBy(ticket => ticket.CategoryId!.Value)
            .Select(group => new { CategoryId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.CategoryId, entry => entry.Count);

        _selectedIds.Clear();
        ApplyFilter();
    }

    private static string Path(TicketCategory category) =>
        category.Parent is { } parent ? $"{parent.Name} > {category.Name}" : category.Name;

    private void OnSearchChanged(KeyboardEventArgs args) => ApplyFilter();

    private void OnShowInactiveChanged(bool value)
    {
        _showInactive = value;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<TicketCategory> query = _showInactive
            ? _categories
            : _categories.Where(category => category.IsActive);

        string term = _searchTerm.Trim();

        if (term.Length > 0)
        {
            query = query.Where(category =>
                category.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (category.Comment?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        _filtered = [.. query];
        _selectedIds.IntersectWith(_filtered.Select(category => category.Id));
    }

    private void ToggleSelectAll(bool selectAll)
    {
        _selectedIds.Clear();
        if (selectAll)
        {
            foreach (TicketCategory category in _filtered)
            {
                _selectedIds.Add(category.Id);
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

    private async Task StartCreate()
    {
        _error = null;
        _edited = NewBlank();
        _editedParentId = 0;

        await JS.InvokeVoidAsync("glpiNg.showModal", "categoryModal");
    }

    private async Task StartEdit(TicketCategory category)
    {
        _error = null;

        // Copie : éditer l'instance de la liste montrerait les saisies en cours dans le tableau,
        // et les laisserait affichées si l'enregistrement était abandonné.
        _edited = new TicketCategory
        {
            Id = category.Id,
            Name = category.Name,
            ParentId = category.ParentId,
            Comment = category.Comment,
            IsActive = category.IsActive,
        };

        _editedParentId = category.ParentId ?? 0;

        await JS.InvokeVoidAsync("glpiNg.showModal", "categoryModal");
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_edited.Name))
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        if (_edited.Id == 0)
        {
            db.Set<TicketCategory>().Add(new TicketCategory
            {
                Name = _edited.Name.Trim(),
                ParentId = _editedParentId == 0 ? null : _editedParentId,
                Comment = _edited.Comment,
                IsActive = _edited.IsActive,
            });
        }
        else
        {
            TicketCategory? stored = await db.Set<TicketCategory>()
                .FirstOrDefaultAsync(category => category.Id == _edited.Id);

            if (stored is null)
            {
                return;
            }

            stored.Name = _edited.Name.Trim();
            stored.ParentId = _editedParentId == 0 ? null : _editedParentId;
            stored.Comment = _edited.Comment;
            stored.IsActive = _edited.IsActive;
            stored.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();

        await JS.InvokeVoidAsync("glpiNg.hideModal", "categoryModal");
        await LoadAsync();
    }

    private async Task DeleteEditedAsync()
    {
        if (_edited.Id == 0)
        {
            return;
        }

        await JS.InvokeVoidAsync("glpiNg.hideModal", "categoryModal");
        await DeleteAsync([_edited.Id]);
    }

    private async Task DeleteSelectedAsync() => await DeleteAsync(_selectedIds);

    /// <summary>
    /// Une catégorie utilisée ou parente n'est pas supprimée : la supprimer quand même laisserait
    /// des tickets sans catégorie ou des filles orphelines, sans que personne l'ait demandé. La
    /// désactiver est le geste prévu pour la retirer de la circulation.
    /// </summary>
    private async Task DeleteAsync(IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        List<int> toDelete = [.. ids];

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        List<TicketCategory> stored = await db.Set<TicketCategory>()
            .Where(category => toDelete.Contains(category.Id))
            .ToListAsync();

        List<int> used = [.. stored
            .Where(category => _ticketCounts.GetValueOrDefault(category.Id) > 0)
            .Select(category => category.Id)];

        List<int> withChildren = await db.Set<TicketCategory>()
            .Where(category => category.ParentId != null && toDelete.Contains(category.ParentId.Value))
            .Select(category => category.ParentId!.Value)
            .Distinct()
            .ToListAsync();

        HashSet<int> refused = [.. used, .. withChildren];

        if (refused.Count > 0)
        {
            _error = "Catégorie(s) non supprimée(s), encore utilisée(s) par des tickets ou portant "
                + $"des sous-catégories : {string.Join(", ", stored.Where(category => refused.Contains(category.Id)).Select(category => category.Name))}.";
        }
        else
        {
            _error = null;
        }

        List<TicketCategory> removable = [.. stored.Where(category => !refused.Contains(category.Id))];

        if (removable.Count > 0)
        {
            db.Set<TicketCategory>().RemoveRange(removable);
            await db.SaveChangesAsync();
        }

        await LoadAsync();
    }

    private static TicketCategory NewBlank() => new() { Name = string.Empty };
}
