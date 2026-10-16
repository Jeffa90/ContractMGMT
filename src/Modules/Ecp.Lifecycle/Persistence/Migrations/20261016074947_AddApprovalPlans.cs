using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Lifecycle.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveGroupKeys",
                schema: "lifecycle",
                table: "ApprovalRequest",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatrixCode",
                schema: "lifecycle",
                table: "ApprovalRequest",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MatrixRelease",
                schema: "lifecycle",
                table: "ApprovalRequest",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanJson",
                schema: "lifecycle",
                table: "ApprovalRequest",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanSha256",
                schema: "lifecycle",
                table: "ApprovalRequest",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ApprovalRequest_TenantId_Id",
                schema: "lifecycle",
                table: "ApprovalRequest",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "ApprovalStepResponse",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    StepKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OnBehalfOfUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OnBehalfOfName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Approved = table.Column<bool>(type: "bit", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RespondedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalStepResponse", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalStepResponse_ApprovalRequest_TenantId_RequestId",
                        columns: x => new { x.TenantId, x.RequestId },
                        principalSchema: "lifecycle",
                        principalTable: "ApprovalRequest",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalStepResponse_TenantId_RequestId_Sequence",
                schema: "lifecycle",
                table: "ApprovalStepResponse",
                columns: new[] { "TenantId", "RequestId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalStepResponse_TenantId_RequestId_StepKey_UserId",
                schema: "lifecycle",
                table: "ApprovalStepResponse",
                columns: new[] { "TenantId", "RequestId", "StepKey", "UserId" },
                unique: true);

            migrationBuilder.AddTenantPolicy("lifecycle", "ApprovalStepResponse");
            migrationBuilder.GrantRuntimeAccess("lifecycle");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("lifecycle", "ApprovalStepResponse");

            migrationBuilder.DropTable(
                name: "ApprovalStepResponse",
                schema: "lifecycle");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ApprovalRequest_TenantId_Id",
                schema: "lifecycle",
                table: "ApprovalRequest");

            migrationBuilder.DropColumn(
                name: "ActiveGroupKeys",
                schema: "lifecycle",
                table: "ApprovalRequest");

            migrationBuilder.DropColumn(
                name: "MatrixCode",
                schema: "lifecycle",
                table: "ApprovalRequest");

            migrationBuilder.DropColumn(
                name: "MatrixRelease",
                schema: "lifecycle",
                table: "ApprovalRequest");

            migrationBuilder.DropColumn(
                name: "PlanJson",
                schema: "lifecycle",
                table: "ApprovalRequest");

            migrationBuilder.DropColumn(
                name: "PlanSha256",
                schema: "lifecycle",
                table: "ApprovalRequest");
        }
    }
}
