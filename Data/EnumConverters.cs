using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BackendApi.Data;

// SQL Server has no native enum type — Npgsql's HasPostgresEnum<T>()/MapEnum<T>() mapped
// each C# enum straight onto a Postgres CREATE TYPE ... AS ENUM whose labels were always the
// snake_case form of the member name (e.g. AdminTier -> 'admin_tier'; see
// db/init/01_schema.sql's enum block and Data/Entities/Enums.cs). This reproduces that same
// PascalCase <-> snake_case mapping as an explicit string conversion so every existing row
// value, the seed data in db/init/02_seed_roles_and_permissions.sql, and any raw-SQL literal
// comparison keep working unchanged — a bare HasConversion<string>() would instead write the
// C# member name ("AdminTier"), silently breaking all three.
public static class EnumConverters
{
    public static ValueConverter<TEnum, string> SnakeCase<TEnum>() where TEnum : struct, Enum =>
        new(
            v => ToSnakeCase(v.ToString()),
            v => (TEnum)Enum.Parse(typeof(TEnum), ToPascalCase(v)));

    private static string ToSnakeCase(string pascalCase)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string ToPascalCase(string snakeCase)
    {
        var sb = new StringBuilder();
        var capitalizeNext = true;
        foreach (var c in snakeCase)
        {
            if (c == '_')
            {
                capitalizeNext = true;
                continue;
            }
            sb.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
            capitalizeNext = false;
        }
        return sb.ToString();
    }
}
