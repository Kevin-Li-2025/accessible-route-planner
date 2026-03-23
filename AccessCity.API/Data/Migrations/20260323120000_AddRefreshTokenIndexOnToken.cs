using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccessCity.API.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>Speeds up POST /auth/revoke-token (lookup by raw token string).</summary>
    public partial class AddRefreshTokenIndexOnToken : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_refresh_token_token",
                table: "refresh_token",
                column: "token");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_refresh_token_token",
                table: "refresh_token");
        }
    }
}
