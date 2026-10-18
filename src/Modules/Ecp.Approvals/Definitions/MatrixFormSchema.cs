using Ecp.Configuration.Forms;
using static Ecp.Configuration.Forms.FormFields;

namespace Ecp.Approvals.Definitions;

/// <summary>
/// The approval matrix form. A matrix is a set of rows; the first row that fits a submission (by value, category, classification and the rest) says who must approve and in what order. Conditions are chosen from
/// lists or typed as plain values: there is no expression to write. Overlaps, gaps and ties between rows are found when the draft is checked, and the simulator shows who would approve a sample submission.
/// </summary>
internal sealed class MatrixFormSchemas : IFormSchemaSource
{
    public IEnumerable<FormSchema> Schemas() =>
    [
        new FormSchema
        {
            Kind = ApprovalsModule.MatrixKind,
            Title = "Approval matrices",
            Intro = "Who must approve a submission. Each matrix has rows; a row says when it applies (value, category, classification and so on) and which approver groups decide, in what order. Check the draft, try it in the simulator, then submit it for a second person to publish.",
            Sections =
            [
                new FormSection("matrices", "Matrices", "A workflow opts in to a matrix by naming its code. Rows of one matrix use the matrix's currency; rates convert other currencies for comparison.",
                [
                    Rows("matrices", "Matrices", "matrix", "name",
                    [
                        Text("code", "Matrix code", "Lower-case letters, digits and hyphens (up to 50). Workflows refer to this code. Example: standard-approvals.", "standard-approvals", required: true),
                        Text("name", "Name", "Up to 200 characters. Example: Standard approvals.", "Standard approvals", required: true),
                        LongText("description", "Description", "What this matrix is for (up to 1000 characters).", maxLength: 1000),
                        Text("currency", "Currency", "Three-letter code. Row value ranges are in this currency. Example: CHF.", "CHF", required: true, maxLength: 3),
                        Date("effectiveFrom", "In force from", "Leave blank for no start date."),
                        Date("effectiveTo", "In force until", "Leave blank for no end date."),
                        Rows("rates", "Exchange rates", "rate", "currency",
                        [
                            Text("currency", "Foreign currency", "Three-letter code. Example: JPY.", "JPY", required: true, maxLength: 3),
                            Decimal("rate", "Rate", "Units of the matrix currency for one unit of this currency. Example: 1.5.", "1.5", required: true),
                            Date("effectiveFrom", "From"), Date("effectiveTo", "Until"),
                        ], "Only needed when contracts are valued in other currencies."),
                        Rows("rows", "Rows", "row", "code",
                        [
                            Text("code", "Row code", "Lower-case letters, digits and hyphens. Example: up-to-250k.", "up-to-250k", required: true),
                            LongText("description", "What this row covers", "Up to 500 characters.", maxLength: 500),
                            Select("kind", "Kind of row", ["Base", "Exception"], "A base row says who approves. An exception adds steps to, or replaces the steps of, the base row that also matches.", required: true, @default: "Base"),
                            Select("effect", "What an exception does", ["AddSteps", "ReplaceSteps"], "Only for an exception. AddSteps adds its steps after the base row's; ReplaceSteps uses its steps instead.", blankLabel: "(none: base row)"),
                            Integer("priority", "Priority", "0 to 10000. When two rows fit, the higher number wins. Two rows that can both fit must not share a priority.", "100", @default: "100"),
                            Date("effectiveFrom", "In force from"), Date("effectiveTo", "In force until"),
                            Group("when", "When it applies",
                            [
                                Decimal("valueMin", "Value from (included)", "Leave blank for no lower limit. Ranges include the lower figure and exclude the upper, so the next row starts where this one ends.", "0"),
                                Decimal("valueMax", "Value up to (excluded)", "Leave blank for no upper limit.", "250000"),
                                ManyFrom("categories", "Categories", "categories", "Leave empty for any category.", linesWhenNoOptions: true),
                                Boolean("includeSubcategories", "Include subcategories of the chosen categories", @default: true),
                                ManyFrom("contractTypes", "Contract types", "contractTypes", "Leave empty for any.", linesWhenNoOptions: true),
                                ManyFrom("legalEntities", "Legal entities", "legalEntities", "Leave empty for any.", linesWhenNoOptions: true),
                                ManyFrom("businessUnits", "Business units", "businessUnits", "Leave empty for any.", linesWhenNoOptions: true),
                                ManyFrom("sites", "Sites", "sites", "Leave empty for any.", linesWhenNoOptions: true),
                                ManyFrom("classifications", "Classifications", "levels", "Leave empty for any level."),
                                ManyFrom("riskLevels", "Risk levels", "riskLevels", "Leave empty for any.", linesWhenNoOptions: true),
                                Lines("supplierTypes", "Counterparty types", "One per line. Leave empty for any."),
                                Rows("custom", "Conditions on additional fields", "condition", "field",
                                [
                                    Text("field", "Field key", "The key of an additional field, lower-case. Example: project_number.", "project_number", required: true),
                                    Select("operator", "Is", CustomOperators.All.Select(o => new FormOption(o, o switch
                                    {
                                        CustomOperators.Eq => "Equal to", CustomOperators.Ne => "Not equal to", CustomOperators.In => "One of", CustomOperators.NotIn => "None of", CustomOperators.AtLeast => "At least (a number)", _ => "Less than (a number)"
                                    })), required: true, @default: CustomOperators.Eq),
                                    Lines("values", "Values", "One per line. Equal and not-equal take one value; at-least and less-than take one number."),
                                ], "Compares an additional field with fixed values. There are no formulas."),
                            ]),
                            Select("flow", "How the steps run", RowFlows.All, "Sequential: one after another. Parallel: all at the same time.", required: true, @default: RowFlows.Sequential),
                            Rows("steps", "Steps", "step", "display",
                            [
                                Text("key", "Step key", "Lower-case letters, digits and hyphens. Example: commercial.", "commercial", required: true),
                                Text("display", "Name people see", "Up to 200 characters. Example: Commercial Manager.", "Commercial Manager", required: true),
                                Text("approverGroup", "Approver group", "The group that decides. A matrix names groups, never people. Choose from the list or type an existing key.", "CommercialManagers", required: true, suggestions: "groups"),
                                Text("backupGroup", "Backup group", "Optional. Used when nobody in the approver group can respond. Must differ from the approver group.", suggestions: "groups"),
                                Select("mode", "Who must respond", StepModes.All.Select(m => new FormOption(m, m == StepModes.FirstResponse ? "The first valid response settles the step (FirstResponse)" : "Everyone named must respond (AllRequired)")), required: true, @default: StepModes.FirstResponse),
                                Integer("timeoutDays", "Escalate after (days)", "0 to 365. Leave blank for no timeout.", "5"),
                                Text("escalateTo", "Escalate to", "Optional. The group told when the step times out.", suggestions: "groups"),
                                YesNo("allowBackup", "Backup approvers may respond", "Follow the mode", "Follow the mode: yes for first-response steps, no for all-required steps."),
                            ], "Between 1 and 10 steps per row."),
                        ]),
                    ]),
                ]),
            ],
        },
    ];
}
