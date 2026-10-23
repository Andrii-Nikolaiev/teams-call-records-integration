using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamsCallRecords.Infrastructure.Data.Migrations
{
    /// <summary>Creates the schema from the embedded InitialCreate.sql (TRP 3.5, 3.11.6).</summary>
    public partial class InitialCreate : Migration
    {
        private const string ResourceName = "TeamsCallRecords.Infrastructure.Data.Migrations.InitialCreate.sql";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            using var stream = typeof(InitialCreate).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("Embedded resource not found: " + ResourceName);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            migrationBuilder.Sql(reader.ReadToEnd());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE error_log;");
            migrationBuilder.Sql("DROP TABLE subscription;");
            migrationBuilder.Sql("DROP TABLE outbox;");
            migrationBuilder.Sql("DROP TABLE audit_log;");
            migrationBuilder.Sql("DROP TABLE rejected;");
            migrationBuilder.Sql("DROP TABLE inbox;");
            migrationBuilder.Sql("DROP TABLE source_state;");
            migrationBuilder.Sql("DROP TABLE cdr;");
        }
    }
}
