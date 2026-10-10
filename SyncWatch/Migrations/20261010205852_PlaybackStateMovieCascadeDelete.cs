using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncWatch.Migrations
{
    /// <inheritdoc />
    public partial class PlaybackStateMovieCascadeDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlaybackStates_Movies_CurrentMovieId",
                table: "PlaybackStates");

            migrationBuilder.AddForeignKey(
                name: "FK_PlaybackStates_Movies_CurrentMovieId",
                table: "PlaybackStates",
                column: "CurrentMovieId",
                principalTable: "Movies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlaybackStates_Movies_CurrentMovieId",
                table: "PlaybackStates");

            migrationBuilder.AddForeignKey(
                name: "FK_PlaybackStates_Movies_CurrentMovieId",
                table: "PlaybackStates",
                column: "CurrentMovieId",
                principalTable: "Movies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
