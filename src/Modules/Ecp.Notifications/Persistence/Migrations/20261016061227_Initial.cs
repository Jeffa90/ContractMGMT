using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Notifications.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.CreateTable(
                name: "CapturedEmail",
                schema: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ToAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CapturedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapturedEmail", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DispatchCursor",
                schema: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchCursor", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobRun",
                schema: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Processed = table.Column<int>(type: "int", nullable: false),
                    Failures = table.Column<int>(type: "int", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notification",
                schema: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubjectKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Classification = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    LinkPath = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Mandatory = table.Column<bool>(type: "bit", nullable: false),
                    RequiresAcknowledgement = table.Column<bool>(type: "bit", nullable: false),
                    DedupeKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReminderKind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    WindowDays = table.Column<int>(type: "int", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReadUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notification", x => x.Id);
                    table.UniqueConstraint("AK_Notification_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ReminderTracking",
                schema: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Classification = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    LastReminderSentUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastReminderKind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    LastWindowDays = table.Column<int>(type: "int", nullable: false),
                    LastDueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AcknowledgedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EscalationDueUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderTracking", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReminderWindow",
                schema: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    WindowDays = table.Column<int>(type: "int", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Suppressed = table.Column<bool>(type: "bit", nullable: false),
                    RecipientCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderWindow", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDelivery",
                schema: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDelivery", x => x.Id);
                    table.UniqueConstraint("AK_NotificationDelivery_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_NotificationDelivery_Notification_TenantId_NotificationId",
                        columns: x => new { x.TenantId, x.NotificationId },
                        principalSchema: "notifications",
                        principalTable: "Notification",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryAttempt",
                schema: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    Succeeded = table.Column<bool>(type: "bit", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryAttempt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeliveryAttempt_NotificationDelivery_TenantId_DeliveryId",
                        columns: x => new { x.TenantId, x.DeliveryId },
                        principalSchema: "notifications",
                        principalTable: "NotificationDelivery",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CapturedEmail_TenantId_CapturedUtc",
                schema: "notifications",
                table: "CapturedEmail",
                columns: new[] { "TenantId", "CapturedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAttempt_TenantId_DeliveryId_Number",
                schema: "notifications",
                table: "DeliveryAttempt",
                columns: new[] { "TenantId", "DeliveryId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DispatchCursor_TenantId_Source",
                schema: "notifications",
                table: "DispatchCursor",
                columns: new[] { "TenantId", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRun_TenantId_JobName_StartedUtc",
                schema: "notifications",
                table: "JobRun",
                columns: new[] { "TenantId", "JobName", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notification_TenantId_ContractId_CreatedUtc",
                schema: "notifications",
                table: "Notification",
                columns: new[] { "TenantId", "ContractId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notification_TenantId_DedupeKey",
                schema: "notifications",
                table: "Notification",
                columns: new[] { "TenantId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notification_TenantId_RecipientUserId_CreatedUtc",
                schema: "notifications",
                table: "Notification",
                columns: new[] { "TenantId", "RecipientUserId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_TenantId_NotificationId_Channel",
                schema: "notifications",
                table: "NotificationDelivery",
                columns: new[] { "TenantId", "NotificationId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDelivery_TenantId_Status_NextAttemptUtc",
                schema: "notifications",
                table: "NotificationDelivery",
                columns: new[] { "TenantId", "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReminderTracking_TenantId_ContractId",
                schema: "notifications",
                table: "ReminderTracking",
                columns: new[] { "TenantId", "ContractId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReminderWindow_TenantId_ContractId_Kind_WindowDays_DueDate",
                schema: "notifications",
                table: "ReminderWindow",
                columns: new[] { "TenantId", "ContractId", "Kind", "WindowDays", "DueDate" },
                unique: true);

            migrationBuilder.AddTenantPolicy("notifications", "Notification");
            migrationBuilder.AddTenantPolicy("notifications", "NotificationDelivery");
            migrationBuilder.AddTenantPolicy("notifications", "DeliveryAttempt");
            migrationBuilder.AddTenantPolicy("notifications", "ReminderWindow");
            migrationBuilder.AddTenantPolicy("notifications", "ReminderTracking");
            migrationBuilder.AddTenantPolicy("notifications", "CapturedEmail");
            migrationBuilder.AddTenantPolicy("notifications", "JobRun");
            migrationBuilder.AddTenantPolicy("notifications", "DispatchCursor");
            migrationBuilder.GrantRuntimeAccess("notifications");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("notifications", "Notification");
            migrationBuilder.DropTenantPolicy("notifications", "NotificationDelivery");
            migrationBuilder.DropTenantPolicy("notifications", "DeliveryAttempt");
            migrationBuilder.DropTenantPolicy("notifications", "ReminderWindow");
            migrationBuilder.DropTenantPolicy("notifications", "ReminderTracking");
            migrationBuilder.DropTenantPolicy("notifications", "CapturedEmail");
            migrationBuilder.DropTenantPolicy("notifications", "JobRun");
            migrationBuilder.DropTenantPolicy("notifications", "DispatchCursor");

            migrationBuilder.DropTable(
                name: "CapturedEmail",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "DeliveryAttempt",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "DispatchCursor",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "JobRun",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "ReminderTracking",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "ReminderWindow",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "NotificationDelivery",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "Notification",
                schema: "notifications");
        }
    }
}
