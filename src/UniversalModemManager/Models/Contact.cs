namespace UniversalModemManager.Models;

public sealed class Contact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public List<ContactNumber> Numbers { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public string PrimaryNumber =>
        Numbers.FirstOrDefault()?.Number ?? string.Empty;

    public string Subtitle =>
        string.IsNullOrWhiteSpace(PrimaryNumber)
            ? "No phone number"
            : PrimaryNumber;
}

public sealed class ContactNumber
{
    public string Label { get; set; } = "Mobile";
    public string Number { get; set; } = string.Empty;
    public string NormalizedNumber { get; set; } = string.Empty;
}
