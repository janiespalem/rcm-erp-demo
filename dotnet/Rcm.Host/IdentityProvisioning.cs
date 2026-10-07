using System.Text.Json;
using Npgsql;

namespace Rcm.Host;

internal static class IdentityProvisioning
{
    public static async Task Run(IServiceProvider services, string[] args)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        await using var db = new NpgsqlConnection(configuration.GetConnectionString("IdentityAdmin")
            ?? throw new InvalidOperationException("A private IdentityAdmin connection is required."));
        await db.OpenAsync();
        if (args.Contains("--list-login-users"))
        {
            await using var list = new NpgsqlCommand("""
                SELECT id,role::text,username,password_hash IS NOT NULL,pin_hash IS NOT NULL
                FROM public.users WHERE role::text IN ('biuro','technolog','ceo','produkcja','crm') ORDER BY id
                """, db);
            await using var reader = await list.ExecuteReaderAsync();
            var users = new List<object>();
            while (await reader.ReadAsync()) users.Add(new { id = reader.GetInt32(0), role = reader.GetString(1),
                username = reader.IsDBNull(2) ? null : reader.GetString(2), passwordEnabled = reader.GetBoolean(3), pinEnabled = reader.GetBoolean(4) });
            Console.WriteLine(JsonSerializer.Serialize(users));
            return;
        }
        string Arg(string key) => Array.IndexOf(args, key) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"Missing {key}");
        if (!int.TryParse(Arg("--user-id"), out var id) || id <= 0) throw new ArgumentException("An explicit existing user ID is required.");
        var revoke = args.Contains("--revoke-password");
        var username = revoke ? null : NativePasswords.NormalizeUsername(Arg("--username"));
        string? hash = null;
        if (!revoke)
        {
            if (!Console.IsInputRedirected) throw new InvalidOperationException("Supply the password through private standard input.");
            var password = await Console.In.ReadLineAsync() ?? throw new ArgumentException("Password input is missing.");
            if (await Console.In.ReadLineAsync() is not null) throw new ArgumentException("Supply exactly one password line.");
            NativePasswords.ValidatePassword(password, username!);
            hash = NativePasswords.HashPassword(password);
        }
        await using var tx = await db.BeginTransactionAsync();
        await using (var guard = new NpgsqlCommand("SET LOCAL lock_timeout='5s'; SELECT public.require_identity_writer(@owner)", db, tx))
        {
            guard.Parameters.AddWithValue("owner", configuration["Identity:Mode"] == "native" ? "dotnet" : "legacy");
            await guard.ExecuteNonQueryAsync();
        }
        await using var update = new NpgsqlCommand(revoke ? """
            UPDATE public.users SET password_hash=NULL,password_version=password_version+1
            WHERE id=@id AND role::text IN ('biuro','technolog','ceo','produkcja','crm') RETURNING id
            """ : """
            UPDATE public.users SET username=@username,password_hash=@hash,password_version=password_version+1
            WHERE id=@id AND role::text IN ('biuro','technolog','ceo','produkcja','crm') RETURNING id
            """, db, tx);
        update.Parameters.AddWithValue("id", id);
        if (!revoke) { update.Parameters.AddWithValue("username", username!); update.Parameters.AddWithValue("hash", hash!); }
        try
        {
            if (await update.ExecuteScalarAsync() is null) throw new InvalidOperationException("No existing login user has that ID.");
            await tx.CommitAsync();
        }
        catch (PostgresException error) when (error.SqlState == "23505")
        { throw new InvalidOperationException("Username already belongs to another user."); }
        Console.WriteLine($"Password {(revoke ? "revoked" : "configured")} for existing user ID {id}; role and memberships retained.");
    }
}
