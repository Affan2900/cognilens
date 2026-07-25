using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CogniLens.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedRubrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Rubrics",
                columns: new[] { "Id", "Category", "Description", "IsActive", "Name" },
                values: new object[,]
                {
                    { new Guid("11111111-0000-0000-0000-000000000001"), "Opening", "Agent greets the customer professionally and verifies their identity before discussing account details.", true, "Greeting and Identity Verification" },
                    { new Guid("11111111-0000-0000-0000-000000000002"), "Soft Skills", "Agent acknowledges the customer's issue, avoids interrupting, and paraphrases the problem back to confirm understanding.", true, "Active Listening" },
                    { new Guid("11111111-0000-0000-0000-000000000003"), "Compliance", "Agent reads any legally required disclosure (e.g. call recording notice, terms) verbatim before proceeding.", true, "Compliance Disclosure" },
                    { new Guid("11111111-0000-0000-0000-000000000004"), "Resolution", "Agent proposes a concrete resolution or next step that addresses the customer's stated issue.", true, "Resolution Offered" },
                    { new Guid("11111111-0000-0000-0000-000000000005"), "Closing", "Agent summarizes the outcome, confirms the customer has no further questions, and closes the call courteously.", true, "Professional Closing" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Rubrics",
                keyColumn: "Id",
                keyValue: new Guid("11111111-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "Rubrics",
                keyColumn: "Id",
                keyValue: new Guid("11111111-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "Rubrics",
                keyColumn: "Id",
                keyValue: new Guid("11111111-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "Rubrics",
                keyColumn: "Id",
                keyValue: new Guid("11111111-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "Rubrics",
                keyColumn: "Id",
                keyValue: new Guid("11111111-0000-0000-0000-000000000005"));
        }
    }
}
