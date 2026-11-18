using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSupportCRM.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPersistentAnalysisQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AnalysisAttemptCount",
                table: "Tickets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AnalysisErrorCode",
                table: "Tickets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "AnalysisLastAttemptAt",
                table: "Tickets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnalysisModel",
                table: "Tickets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AnalysisNextAttemptAt",
                table: "Tickets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AnalysisStatus",
                table: "Tickets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Existing failures are not silently sent to a provider or treated as model results.
            migrationBuilder.Sql(@"
UPDATE Tickets SET AnalysisStatus = CASE
    WHEN NULLIF(LTRIM(RTRIM(AiSummary)), '') IS NOT NULL AND NULLIF(LTRIM(RTRIM(SuggestedReply)), '') IS NOT NULL THEN 2 ELSE 4 END;
UPDATE Tickets SET AnalysisModel = 'Legacy (model not recorded)' WHERE AnalysisStatus = 2;
UPDATE Tickets SET AnalysisErrorCode = 0, Sentiment = 3, SentimentScore = 0, Category = 'Unclassified'
    WHERE AnalysisStatus = 4;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnalysisAttemptCount",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AnalysisErrorCode",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AnalysisLastAttemptAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AnalysisModel",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AnalysisNextAttemptAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AnalysisStatus",
                table: "Tickets");
        }
    }
}
