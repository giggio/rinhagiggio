using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rinha.Migrations;

/// <inheritdoc />
public partial class First : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Pessoas",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Apelido = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Nome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Nascimento = table.Column<DateOnly>(type: "date", nullable: false),
                Stack = table.Column<List<string>>(type: "text[]", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_Pessoas", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_Pessoas_Apelido",
            table: "Pessoas",
            column: "Apelido",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "Pessoas");
}
