using GlpiNg.Modules.Abstractions.Items;
using GlpiNg.Modules.Assistance.Models;
using Microsoft.EntityFrameworkCore;
using Types = GlpiNg.Modules.Abstractions.Items.ItemTypes;

namespace GlpiNg.Modules.Assistance.Services;

/// <summary>Tickets, problèmes et changements comme cibles de rattachement — voir <see cref="IRelationTargetSource"/>.</summary>
public sealed class RelationTargetSource(IDbContextFactory<DbContext> dbFactory) : IRelationTargetSource
{
    public IReadOnlyCollection<string> SupportedTypes { get; } = [Types.Ticket, Types.Problem, Types.Change];

    private sealed record Row(int Id, string Name);

    public async Task<IReadOnlyList<RelationTarget>> SearchAsync(string itemType, string? term, int limit, CancellationToken cancellationToken = default)
    {
        await using DbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        string like = term?.Trim() ?? string.Empty;

        return itemType switch
        {
            Types.Ticket => Map(itemType, "tickets", await db.Set<Ticket>().AsNoTracking().Where(x => x.Name.Contains(like))
                .OrderByDescending(x => x.Id).Take(limit).Select(x => new Row(x.Id, x.Name)).ToListAsync(cancellationToken)),
            Types.Problem => Map(itemType, "problems", await db.Set<Problem>().AsNoTracking().Where(x => x.Name.Contains(like))
                .OrderByDescending(x => x.Id).Take(limit).Select(x => new Row(x.Id, x.Name)).ToListAsync(cancellationToken)),
            Types.Change => Map(itemType, "changes", await db.Set<Change>().AsNoTracking().Where(x => x.Name.Contains(like))
                .OrderByDescending(x => x.Id).Take(limit).Select(x => new Row(x.Id, x.Name)).ToListAsync(cancellationToken)),
            _ => [],
        };
    }

    public async Task<IReadOnlyList<RelationTarget>> ResolveAsync(string itemType, IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
    {
        await using DbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

        return itemType switch
        {
            Types.Ticket => Map(itemType, "tickets", await db.Set<Ticket>().AsNoTracking().Where(x => ids.Contains(x.Id))
                .Select(x => new Row(x.Id, x.Name)).ToListAsync(cancellationToken)),
            Types.Problem => Map(itemType, "problems", await db.Set<Problem>().AsNoTracking().Where(x => ids.Contains(x.Id))
                .Select(x => new Row(x.Id, x.Name)).ToListAsync(cancellationToken)),
            Types.Change => Map(itemType, "changes", await db.Set<Change>().AsNoTracking().Where(x => ids.Contains(x.Id))
                .Select(x => new Row(x.Id, x.Name)).ToListAsync(cancellationToken)),
            _ => [],
        };
    }

    private static List<RelationTarget> Map(string itemType, string route, List<Row> rows) =>
        [.. rows.Select(r => new RelationTarget(itemType, r.Id, r.Name, $"/assistance/{route}/{r.Id}"))];
}
