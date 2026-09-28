using System.Text.Json;
using UniversalModemManager.Models;

namespace UniversalModemManager.Services;

public sealed class ContactStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public ContactStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BDFR",
            "UniversalModemManager");

        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "contacts.json");
    }

    public async Task<List<Contact>> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(_filePath))
                return [];

            var json = await File.ReadAllTextAsync(_filePath);
            var contacts =
                JsonSerializer.Deserialize<List<Contact>>(json, JsonOptions)
                ?? [];

            foreach (var contact in contacts)
            {
                contact.Numbers ??= [];

                foreach (var number in contact.Numbers)
                {
                    number.NormalizedNumber =
                        NormalizePhone(number.Number);
                }
            }

            return contacts
                .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyCollection<Contact> contacts)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var contact in contacts)
            {
                contact.UpdatedAtUtc = DateTime.UtcNow;

                foreach (var number in contact.Numbers)
                {
                    number.Number = number.Number.Trim();
                    number.NormalizedNumber =
                        NormalizePhone(number.Number);
                }
            }

            var json =
                JsonSerializer.Serialize(contacts, JsonOptions);

            await File.WriteAllTextAsync(_filePath, json);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string NormalizePhone(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
            return string.Empty;

        var value = number.Trim();

        var plus =
            value.StartsWith("+", StringComparison.Ordinal);

        var digits =
            new string(value.Where(char.IsDigit).ToArray());

        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
            plus = true;
        }

        // Iran local mobile format: 09xxxxxxxxx -> +989xxxxxxxxx.
        // Other countries remain in the exact digit form supplied by the user.
        if (!plus &&
            digits.Length == 11 &&
            digits.StartsWith("09", StringComparison.Ordinal))
        {
            return "98" + digits[1..];
        }

        return digits.TrimStart('0');
    }

    public static Contact? FindByNumber(
        IEnumerable<Contact> contacts,
        string? phone)
    {
        var key = NormalizePhone(phone);

        if (string.IsNullOrWhiteSpace(key))
            return null;

        return contacts.FirstOrDefault(contact =>
            contact.Numbers.Any(number =>
                PhoneKeysMatch(
                    number.NormalizedNumber,
                    key)));
    }

    private static bool PhoneKeysMatch(
        string? left,
        string? right)
    {
        if (string.IsNullOrWhiteSpace(left) ||
            string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (left.Equals(
                right,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Tolerate country-code vs local-number storage by comparing
        // the final 10 significant digits when both sides are long enough.
        return left.Length >= 10 &&
               right.Length >= 10 &&
               left[^10..].Equals(
                   right[^10..],
                   StringComparison.OrdinalIgnoreCase);
    }
}
