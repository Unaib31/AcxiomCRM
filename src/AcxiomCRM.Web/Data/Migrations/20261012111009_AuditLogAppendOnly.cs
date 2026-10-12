using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcxiomCRM.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditLogAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defence in depth: the audit trail is append-only even for direct SQL access to the database.
            migrationBuilder.Sql(@"CREATE TRIGGER TR_AuditLogs_NoUpdate BEFORE UPDATE ON AuditLogs
BEGIN SELECT RAISE(ABORT, 'Audit log entries are append-only.'); END;");
            migrationBuilder.Sql(@"CREATE TRIGGER TR_AuditLogs_NoDelete BEFORE DELETE ON AuditLogs
BEGIN SELECT RAISE(ABORT, 'Audit log entries are append-only.'); END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_AuditLogs_NoUpdate;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_AuditLogs_NoDelete;");
        }
    }
}
