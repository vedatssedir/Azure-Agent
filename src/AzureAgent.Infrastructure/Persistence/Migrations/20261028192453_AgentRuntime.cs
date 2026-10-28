using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Runtime",
                schema: "app",
                table: "Agents",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Runtime",
                schema: "app",
                table: "Agents");
        }
    }
}
