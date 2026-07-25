using CogniLens.Core.Entities;

namespace CogniLens.Infrastructure.Persistence;

// Fixed GUIDs so HasData produces stable migrations across environments.
public static class RubricSeedData
{
    public static readonly Rubric[] All =
    [
        new()
        {
            Id = Guid.Parse("11111111-0000-0000-0000-000000000001"),
            Name = "Greeting and Identity Verification",
            Description = "Agent greets the customer professionally and verifies their identity before discussing account details.",
            Category = "Opening"
        },
        new()
        {
            Id = Guid.Parse("11111111-0000-0000-0000-000000000002"),
            Name = "Active Listening",
            Description = "Agent acknowledges the customer's issue, avoids interrupting, and paraphrases the problem back to confirm understanding.",
            Category = "Soft Skills"
        },
        new()
        {
            Id = Guid.Parse("11111111-0000-0000-0000-000000000003"),
            Name = "Compliance Disclosure",
            Description = "Agent reads any legally required disclosure (e.g. call recording notice, terms) verbatim before proceeding.",
            Category = "Compliance"
        },
        new()
        {
            Id = Guid.Parse("11111111-0000-0000-0000-000000000004"),
            Name = "Resolution Offered",
            Description = "Agent proposes a concrete resolution or next step that addresses the customer's stated issue.",
            Category = "Resolution"
        },
        new()
        {
            Id = Guid.Parse("11111111-0000-0000-0000-000000000005"),
            Name = "Professional Closing",
            Description = "Agent summarizes the outcome, confirms the customer has no further questions, and closes the call courteously.",
            Category = "Closing"
        }
    ];
}
