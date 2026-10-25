using GlpiNg.Modules.Abstractions.Items;
using GlpiNg.Modules.Assistance.Models;
using Microsoft.EntityFrameworkCore;

namespace GlpiNg.Modules.Assistance.Services;

/// <summary>Tickets, problèmes et changements créés par un utilisateur — voir <see cref="IUserItemSource"/>.</summary>
public sealed class UserItemsSource(IDbContextFactory<DbContext> dbFactory) : IUserItemSource
{
    public IReadOnlyCollection<string> Kinds { get; } = [UserItemKinds.Tickets, UserItemKinds.Problems, UserItemKinds.Changes];

    public async Task<IReadOnlyList<UserItem>> GetAsync(string kind, int userId, string userName, CancellationToken cancellationToken = default)
    {
        await using DbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return kind switch
        {
            UserItemKinds.Tickets => [.. (await db.Set<Ticket>().AsNoTracking().Where(x => x.RequesterUserId == userId)
                .OrderByDescending(x => x.Id).Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken))
                .Select(x => new UserItem("Ticket", x.Id, x.Name, $"/assistance/tickets/{x.Id}"))],
            UserItemKinds.Problems => [.. (await db.Set<Problem>().AsNoTracking().Where(x => x.AuthorUserId == userId)
                .OrderByDescending(x => x.Id).Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken))
                .Select(x => new UserItem("Problem", x.Id, x.Name, $"/assistance/problems/{x.Id}"))],
            UserItemKinds.Changes => [.. (await db.Set<Change>().AsNoTracking().Where(x => x.AuthorUserId == userId)
                .OrderByDescending(x => x.Id).Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken))
                .Select(x => new UserItem("Change", x.Id, x.Name, $"/assistance/changes/{x.Id}"))],
            _ => [],
        };
    }
}
