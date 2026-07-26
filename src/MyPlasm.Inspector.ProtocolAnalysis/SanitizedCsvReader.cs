using System.Text;

namespace MyPlasm.Inspector.ProtocolAnalysis;

internal static class SanitizedCsvReader
{
    public static IReadOnlyList<string[]> Read(
        string path,
        string[] expectedHeader,
        string reportName)
    {
        string text;
        try
        {
            text = File.ReadAllText(
                path,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true));
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                DecoderFallbackException)
        {
            throw new CampaignInputValidationException(
                $"{reportName} could not be read as UTF-8.",
                exception);
        }

        List<string[]> rows = Parse(text, reportName);
        if (rows.Count == 0 ||
            !rows[0].SequenceEqual(expectedHeader, StringComparer.Ordinal))
        {
            throw new CampaignInputValidationException(
                $"{reportName} has an incompatible CSV header.");
        }

        for (int index = 1; index < rows.Count; index++)
        {
            if (rows[index].Length != expectedHeader.Length)
            {
                throw new CampaignInputValidationException(
                    $"{reportName} row {index + 1} has {rows[index].Length} fields; " +
                    $"{expectedHeader.Length} are required.");
            }
        }

        return rows.Skip(1).ToArray();
    }

    private static List<string[]> Parse(string text, string reportName)
    {
        List<string[]> rows = [];
        List<string> row = [];
        StringBuilder field = new();
        bool quoted = false;
        bool afterQuote = false;

        for (int index = 0; index < text.Length; index++)
        {
            char value = text[index];
            if (quoted)
            {
                if (value != '"')
                {
                    field.Append(value);
                    continue;
                }

                if (index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                    continue;
                }

                quoted = false;
                afterQuote = true;
                continue;
            }

            if (afterQuote && value is not (',' or '\r' or '\n'))
            {
                throw new CampaignInputValidationException(
                    $"{reportName} has characters after a closing CSV quote.");
            }

            switch (value)
            {
                case '"' when field.Length == 0 && !afterQuote:
                    quoted = true;
                    break;
                case '"':
                    throw new CampaignInputValidationException(
                        $"{reportName} contains a quote inside an unquoted CSV field.");
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    afterQuote = false;
                    break;
                case '\r':
                    if (index + 1 >= text.Length || text[index + 1] != '\n')
                    {
                        throw new CampaignInputValidationException(
                            $"{reportName} contains a noncanonical carriage return.");
                    }

                    index++;
                    AddRow(rows, row, field);
                    afterQuote = false;
                    break;
                case '\n':
                    AddRow(rows, row, field);
                    afterQuote = false;
                    break;
                default:
                    field.Append(value);
                    break;
            }
        }

        if (quoted)
        {
            throw new CampaignInputValidationException(
                $"{reportName} contains an unterminated CSV quote.");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            AddRow(rows, row, field);
        }

        return rows;
    }

    private static void AddRow(
        ICollection<string[]> rows,
        List<string> row,
        StringBuilder field)
    {
        row.Add(field.ToString());
        field.Clear();
        rows.Add(row.ToArray());
        row.Clear();
    }
}
