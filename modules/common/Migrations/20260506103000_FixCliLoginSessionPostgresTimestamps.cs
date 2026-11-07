using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Minicloud.Common.Data;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MinicloudDbContext))]
    [Migration("20260506103000_FixCliLoginSessionPostgresTimestamps")]
    public partial class FixCliLoginSessionPostgresTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE cli_login_sessions
                ALTER COLUMN created_at TYPE timestamp with time zone USING created_at::timestamp with time zone,
                ALTER COLUMN expires_at TYPE timestamp with time zone USING expires_at::timestamp with time zone,
                ALTER COLUMN approved_at TYPE timestamp with time zone USING approved_at::timestamp with time zone,
                ALTER COLUMN exchanged_at TYPE timestamp with time zone USING exchanged_at::timestamp with time zone;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE cli_login_sessions
                ALTER COLUMN created_at TYPE text USING created_at::text,
                ALTER COLUMN expires_at TYPE text USING expires_at::text,
                ALTER COLUMN approved_at TYPE text USING approved_at::text,
                ALTER COLUMN exchanged_at TYPE text USING exchanged_at::text;
                """);
        }
    }
}
