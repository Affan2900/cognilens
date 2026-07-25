namespace CogniLens.Core.Entities;

public class Rubric
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string Category { get; set; }
    public bool IsActive { get; set; } = true;
}
