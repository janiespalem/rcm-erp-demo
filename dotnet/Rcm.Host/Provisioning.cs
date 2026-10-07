using Microsoft.EntityFrameworkCore;
using Npgsql;
using Rcm.Crm;

namespace Rcm.Host;

internal static class Provisioning
{
    public static async Task Run(IServiceProvider services, string[] args)
    {
        string Arg(string key) => Array.IndexOf(args, key) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"Missing {key}");
        var revoke = args.Contains("--revoke-user");
        var userId = long.Parse(Arg(revoke ? "--revoke-user" : "--grant-user"));
        if (userId <= 0) throw new ArgumentException("An explicit existing user ID is required.");
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDb>();
        var existing = await db.Memberships.SingleOrDefaultAsync(m => m.UserId == userId);
        if (revoke)
        {
            if (existing != null) { db.Memberships.Remove(existing); await db.SaveChangesAsync(); }
            Console.WriteLine($"CRM membership revoked for user ID {userId}."); return;
        }
        var teamId = Guid.Parse(Arg("--team-id"));
        var teamName = Arg("--team-name").Trim();
        if (teamId == Guid.Empty || teamName.Length is < 1 or > 160) throw new ArgumentException("Explicit team ID and name are required.");
        if (existing is not null && existing.TeamId != teamId) throw new InvalidOperationException("Revoke the existing team assignment explicitly before moving a user.");
        await db.Database.OpenConnectionAsync();
        await using var check = new NpgsqlCommand("SELECT id FROM public.users WHERE id = @id AND pin_hash IS NOT NULL AND role IN ('biuro','technolog','ceo','produkcja','crm')", (NpgsqlConnection)db.Database.GetDbConnection());
        check.Parameters.AddWithValue("id", userId);
        if (await check.ExecuteScalarAsync() is null) throw new InvalidOperationException("No login-enabled legacy user with that ID.");
        var team = await db.Teams.FindAsync(teamId);
        if (team is not null && team.Name != teamName) throw new InvalidOperationException("Team name does not match the supplied ID.");
        if (team is null) db.Teams.Add(new() { Id = teamId, Name = teamName });
        if (existing is null) db.Memberships.Add(new() { UserId = userId, TeamId = teamId });
        await db.SaveChangesAsync();
        Console.WriteLine($"CRM membership assigned: user ID {userId}, team ID {teamId}.");
    }
}
